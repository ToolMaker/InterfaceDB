using TransactionManager.Abstraction.Serialization;

namespace TransactionManager.Abstraction.Dtos
{
    public interface IObjectReadResult<T>
    {
        string Id { get; }
        string VersionTag { get; }
        FileInfo File { get; }
        IObjectEnvelope Envelope { get; }
        T Value { get; }
    }
}
