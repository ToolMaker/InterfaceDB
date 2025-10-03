using Idb.Libraries.Abstractions.Serialization;

namespace Idb.Libraries.Abstractions.Migrations
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
