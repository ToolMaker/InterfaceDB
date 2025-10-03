using Idb.Libraries.Abstractions.Serialization;

namespace Idb.Libraries.Abstractions.Dtos
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
