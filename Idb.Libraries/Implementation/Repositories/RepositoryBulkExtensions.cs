using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;

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

        var effectivePolicy = policy ?? StoragePolicy.Default;
        var concurrency = effectivePolicy.NormalizedConcurrency;

        if (concurrency <= 1 || materialized.Count == 1)
        {
            foreach (var item in materialized)
            {
                await repository.SaveAsync(item, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(materialized, parallelOptions, async (item, token) =>
        {
            await repository.SaveAsync(item, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}
