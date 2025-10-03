using Idb.Libraries.Abstractions.Serialization;

namespace Idb.Libraries.Abstractions.Dtos
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
