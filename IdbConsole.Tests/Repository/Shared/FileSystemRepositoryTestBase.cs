using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Repository;
using Idb.Libraries.Abstractions.Serialization;
using Idb.Libraries.Implementation.Repository;

namespace IdbConsole.Tests.Repository.Shared;

public abstract class FileSystemRepositoryTestBase
{
    protected static readonly ITestContract SampleContract = new TestContract("person-001");
    protected static readonly FakeWriteResult DefaultWriteResult = new("person-001", "v1", typeof(ITestContract));
    protected static readonly FakeReadResult<ITestContract> DefaultReadResult = new("person-001", "v1", SampleContract);

    protected static ITestContract CreateContract(string id)
        => new TestContract(id);

    protected static SortedListIndexConfiguration<ITestContract, int> CreateLengthIndex()
        => new("Length", c => c.Id.Length);

    protected static FileSystemRepository CreateRepository(
        StubObjectWriter writer,
        StubObjectReader reader,
        bool configureLengthIndex = true)
    {
        var repository = new FileSystemRepository(writer, reader);
        if (configureLengthIndex)
        {
            repository.ConfigureIndex(CreateLengthIndex());
        }

        return repository;
    }

    protected static FileSystemRepository CreateRepositoryWithLengthIndex()
        => CreateRepository(new StubObjectWriter(DefaultWriteResult), new StubObjectReader().AddResult(DefaultReadResult, exists: true));

    protected interface ITestContract
    {
        string Id { get; }
    }

    protected sealed record TestContract(string Id) : ITestContract;

    protected sealed class NonContract
    {
        public string Id { get; set; } = string.Empty;
    }

    protected sealed class StubObjectWriter : IObjectWriter
    {
        private readonly IObjectWriteResult _result;

        public StubObjectWriter(IObjectWriteResult result)
        {
            _result = result;
        }

        public object? LastEntity { get; private set; }
        public string? LastId { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }

        public ValueTask<IObjectWriteResult> WriteAsync<T>(
            T obj,
            string? id = null,
            CancellationToken cancellationToken = default)
        {
            LastEntity = obj;
            LastId = id;
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(_result);
        }
    }

    protected sealed class StubObjectReader : IObjectReader
    {
        private readonly ConcurrentDictionary<Type, object> _readResults = new();
        private readonly ConcurrentDictionary<Type, bool> _existsResults = new();

        public string? LastReadId { get; private set; }
        public string? LastReadVersion { get; private set; }
        public CancellationToken LastReadCancellationToken { get; private set; }

        public string? LastExistsId { get; private set; }
        public string? LastExistsVersion { get; private set; }
        public CancellationToken LastExistsCancellationToken { get; private set; }

        public StubObjectReader AddResult<T>(IObjectReadResult<T> result, bool exists)
        {
            _readResults[typeof(T)] = result!;
            _existsResults[typeof(T)] = exists;
            return this;
        }

        public ValueTask<bool> ExistsAsync<T>(
            string id,
            string? versionTag = null,
            CancellationToken cancellationToken = default)
        {
            LastExistsId = id;
            LastExistsVersion = versionTag;
            LastExistsCancellationToken = cancellationToken;

            return ValueTask.FromResult(_existsResults.TryGetValue(typeof(T), out var exists) && exists);
        }

        public ValueTask<IObjectReadResult<T>> ReadAsync<T>(
            string id,
            string? versionTag = null,
            CancellationToken cancellationToken = default)
        {
            LastReadId = id;
            LastReadVersion = versionTag;
            LastReadCancellationToken = cancellationToken;

            if (_readResults.TryGetValue(typeof(T), out var result) && result is IObjectReadResult<T> typed)
            {
                return ValueTask.FromResult(typed);
            }

            throw new InvalidCastException($"Stub reader was not configured for contract type '{typeof(T).FullName}'.");
        }
    }

    protected sealed class FakeWriteResult : IObjectWriteResult
    {
        public FakeWriteResult(string id, string versionTag, Type? objectType = null)
        {
            Id = id;
            VersionTag = versionTag;
            ObjectType = objectType ?? typeof(object);
            File = new FileInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bin"));
            Envelope = new FakeEnvelope();
        }

        public string Id { get; }
        public string VersionTag { get; }
        public Type ObjectType { get; }
        public FileInfo File { get; }
        public IObjectEnvelope Envelope { get; }
    }

    protected sealed class FakeReadResult<T> : IObjectReadResult<T>
    {
        public FakeReadResult(string id, string versionTag, T value)
        {
            Id = id;
            VersionTag = versionTag;
            Value = value;
            File = new FileInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bin"));
            Envelope = new FakeEnvelope();
        }

        public string Id { get; }
        public string VersionTag { get; }
        public FileInfo File { get; }
        public IObjectEnvelope Envelope { get; }
        public T Value { get; }
    }

    protected sealed class FakeEnvelope : IObjectEnvelope
    {
        public byte[] Payload { get; set; } = Array.Empty<byte>();
        public string VersionTag { get; set; } = "v1";
        public string? TypeName { get; set; } = typeof(TestContract).AssemblyQualifiedName;
        public ObjectFingerprint Fingerprint { get; set; } = new(1, 2);
    }
}
