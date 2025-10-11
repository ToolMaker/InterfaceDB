using Idb.Libraries.Abstractions.Services.Serialization;

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
