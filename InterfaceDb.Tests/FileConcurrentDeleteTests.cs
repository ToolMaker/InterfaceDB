using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class FileConcurrentDeleteTests
{
    [TestMethod]
    [DataRow(0, false)] [DataRow(1, false)] [DataRow(2, false)] [DataRow(0, true)]
    public async Task CommittedDeleteBetweenCatalogLookupAndFileOpen_IsAbsent(int readMode, bool unrelatedMissingFile)
    {
        var root = Path.Combine(Path.GetTempPath(), "InterfaceDBConcurrentDelete", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var gate = new ReadGate { UnrelatedMissingFile = unrelatedMissingFile };
            var services = new ServiceCollection();
            services.AddFileRepository<IRecord>(new Config(root), storagePolicy: StoragePolicy.Default with
            { ObjectCacheMode = StorageObjectCacheMode.MetadataOnly, WatchStorageChanges = false, HydrateOnInitialize = false });
            var descriptor = services.Single(item => item.IsKeyedService && Equals(item.ServiceKey, "delete-race")
                && item.ServiceType == typeof(IObjectReader<IRecord>));
            services[services.IndexOf(descriptor)] = new ServiceDescriptor(typeof(IObjectReader<IRecord>), "delete-race",
                (sp, key) => new PausedReader((IObjectReader<IRecord>)descriptor.KeyedImplementationFactory!(sp, key), gate), descriptor.Lifetime);
            await using var provider = services.BuildServiceProvider();
            var repository = provider.GetRequiredService<IRepository<IRecord>>();
            var removed = await repository.SaveAsync(new Record { Id = "remove" });
            var kept = await repository.SaveAsync(new Record { Id = "keep" });
            gate.Key = removed.Envelope!.EnvelopeCacheKey;
            var envelopes = provider.GetRequiredService<IEnvelopeRepository<IRecord>>();
            var read = Read();
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsTrue(await repository.DeleteAsync(removed));
            }
            finally { gate.Release.TrySetResult(); }
            if (unrelatedMissingFile)
            {
                var error = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.AreEqual("dependency.dll", error.FileName);
                return;
            }
            var values = await read.WaitAsync(TimeSpan.FromSeconds(10));
            CollectionAssert.AreEqual(readMode == 1 ? Array.Empty<string>() : new[] { "keep" }, values);

            async Task<string[]> Read()
            {
                if (readMode == 1)
                {
                    var found = await envelopes.GetPhysicalAsync(gate.Key);
                    return found is null ? [] : [found.ObjectInstance!.Id];
                }
                if (readMode == 2)
                {
                    var batch = await ((IBatchedEnvelopeRepository<IRecord>)envelopes).GetPhysicalManyAsync([gate.Key!, kept.Envelope!.EnvelopeCacheKey]);
                    return batch.Select(item => item.ObjectInstance!.Id).ToArray();
                }
                var result = new List<string>();
                await foreach (var record in repository.QueryManyAsync(_ => true)) result.Add(record.Id);
                return result.Order(StringComparer.Ordinal).ToArray();
            }
        }
        finally
        {
            var boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "InterfaceDBConcurrentDelete")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(root, true);
        }
    }

    private sealed class ReadGate
    {
        public string? Key;
        public bool UnrelatedMissingFile;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class PausedReader(IObjectReader<IRecord> inner, ReadGate gate) : IIsolatedObjectReader<IRecord>
    {
        public async Task<IRecord> ReadAsync(FileInfo file, CancellationToken cancellationToken = default)
        {
            if (Path.GetFileNameWithoutExtension(file.Name) == gate.Key)
            {
                gate.Entered.TrySetResult();
                await gate.Release.Task.WaitAsync(cancellationToken);
                if (gate.UnrelatedMissingFile) throw new FileNotFoundException("Missing serializer dependency", "dependency.dll");
            }
            return await inner.ReadAsync(file, cancellationToken);
        }
    }

    [StorageScope(VersionTag = "1.0.0")]
    public interface IRecord : IStoredContract<IRecord> { string Id { get; set; } }
    [MessagePackObject(true)]
    public sealed class Record : IRecord
    {
        public string Id { get; set; } = "";
        [IgnoreMember] public IObjectEnvelope<IRecord>? Envelope { get; set; }
    }
    private sealed class Config(string root) : IDbContractConfigDto<IRecord>
    {
        public string EnvelopeDICacheKey => "delete-race";
        public string ContractName => "DeleteRace";
        public string FileExtension => ".mpk";
        public string? DataRoot => root;
        public string? ActiveVersion => "1.0.0";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public ContractRepositoryType RepositoryType => ContractRepositoryType.FileSystem;
        public StoragePolicy? StoragePolicy => null;
    }
}
