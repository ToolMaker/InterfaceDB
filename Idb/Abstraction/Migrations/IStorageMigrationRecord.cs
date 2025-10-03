using TransactionManager.Abstraction.Serialization;

namespace TransactionManager.Abstraction.Migrations
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
