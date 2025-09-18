using TransactionManager.Abstraction.Serialization;

namespace TransactionManager.Abstraction.Dtos
{
    public interface IObjectWriteResult
    {
        string Id { get; }
        string VersionTag { get; }
        Type ObjectType { get; }
        FileInfo File { get; }
        IObjectEnvelope Envelope { get; }
    }
}
