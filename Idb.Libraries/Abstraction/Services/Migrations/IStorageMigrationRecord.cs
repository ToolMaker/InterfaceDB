using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Abstractions.Services.Migrations
{
    public interface IStorageMigrationRecord<T>
    {
        string Id { get; }
        IObjectEnvelope Envelope { get; }
        Type RuntimeType { get; }
        FileInfo File { get; }
        T Value { get; }
    }
}

