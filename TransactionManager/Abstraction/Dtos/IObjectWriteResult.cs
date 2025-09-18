using TransactionManager.Implementation.Dtos;

namespace TransactionManager.Abstraction.Dtos
{
    public interface IObjectWriteResult
    {
        ObjectEnvelope Envelope { get; init; }
        FileInfo File { get; init; }
        string Id { get; init; }
        Type ObjectType { get; init; }
        string VersionTag { get; init; }

        void Deconstruct(out string Id, out string VersionTag, out Type ObjectType, out FileInfo File, out ObjectEnvelope Envelope);
        bool Equals(object? obj);
        bool Equals(ObjectWriteResult? other);
        int GetHashCode();
        string ToString();
    }
}