using TransactionManager.Implementation.Dtos;

namespace TransactionManager.Abstraction.Dtos
{
    public interface IObjectReadResult<T>
    {
        ObjectEnvelope Envelope { get; init; }
        FileInfo File { get; init; }
        string Id { get; init; }
        T Value { get; init; }
        string VersionTag { get; init; }

        void Deconstruct(out string Id, out string VersionTag, out FileInfo File, out ObjectEnvelope Envelope, out T Value);
        bool Equals(object? obj);
        bool Equals(ObjectReadResult<T>? other);
        int GetHashCode();
        string ToString();
    }
}