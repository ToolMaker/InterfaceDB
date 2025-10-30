using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using System.Threading.Channels;

namespace Idb.Libraries.Implementation.Repositories;

/// <summary>
/// Convenience extensions that orchestrate batched persistence against <see cref="IRepository{TContract}"/> instances.
/// </summary>
public static class RepositoryBulkExtensions
{
    /// <summary>
    /// Persists a collection of entities, optionally leveraging the supplied storage policy to control parallelism.
    /// Callers retain responsibility for ensuring the underlying repository supports concurrent writes.
    /// </summary>
    public static async Task SaveManyAsync<TContract>(
        this IRepository<TContract> repository,
        IEnumerable<TContract> items,
        StoragePolicy? policy = null,
        CancellationToken cancellationToken = default)
        where TContract : class
    {
        if (repository is null)
        {
            throw new ArgumentNullException(nameof(repository));
        }

        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        var materialized = items as IReadOnlyCollection<TContract> ?? new List<TContract>(items);
        if (materialized.Count == 0)
        {
            return;
        }

        //var effectivePolicy = policy ?? StoragePolicy.Default;
        //var concurrency = effectivePolicy.NormalizedConcurrency;

        //if (concurrency <= 1 || materialized.Count == 1)
        //{
        //    foreach (var item in materialized)
        //    {
        //        await repository.SaveAsync(item, cancellationToken).ConfigureAwait(false);
        //    }

        //    return;
        //}

        var effectivePolicy = policy ?? StoragePolicy.Default;
        var dop = effectivePolicy.MaxConcurrency;

        if (dop <= 1 || materialized.Count == 1)
        {
            foreach (var item in materialized)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await repository.SaveAsync(item, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        // Channel-based bounded pipeline to reduce scheduler overhead compared to Parallel.ForEachAsync
        var capacity = Math.Max(dop * 2, 32);
        var channel = Channel.CreateBounded<TContract>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = false
        });

        // Start consumers
        var workers = new Task[dop];
        for (int i = 0; i < dop; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                var reader = channel.Reader;
                while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var item))
                    {
                        await repository.SaveAsync(item, cancellationToken).ConfigureAwait(false);
                    }
                }
            }, cancellationToken);
        }

        // Produce
        var writer = channel.Writer;
        try
        {
            foreach (var item in materialized)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            writer.TryComplete();
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
    }
}
