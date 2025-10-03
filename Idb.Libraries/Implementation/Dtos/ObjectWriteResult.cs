using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Serialization;

namespace Idb.Libraries.Implementation.Dtos
{
    public sealed record ObjectWriteResult(
        string Id,
        string VersionTag,
        Type ObjectType,
        FileInfo File,
        IObjectEnvelope Envelope) : IObjectWriteResult;
}
