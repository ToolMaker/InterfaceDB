using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Services.Migrations
{
    public sealed class StorageMigrationRecord<T> : IStorageMigrationRecord<T>
    {
        public StorageMigrationRecord(string id,
                                      IObjectEnvelope envelope,
                                      Type runtimeType,
                                      FileInfo file,
                                      T value)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
            RuntimeType = runtimeType ?? throw new ArgumentNullException(nameof(runtimeType));
            File = file ?? throw new ArgumentNullException(nameof(file));
            Value = value ?? throw new ArgumentNullException(nameof(value));
        }

        public string Id { get; }
        public IObjectEnvelope Envelope { get; }
        public Type RuntimeType { get; }
        public FileInfo File { get; }
        public T Value { get; }
    }
}

