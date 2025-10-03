using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Serialization;

namespace Idb.Libraries.Implementation.Dtos
{
    public sealed record ObjectReadResult<T>(
        string Id,
        string VersionTag,
        FileInfo File,
        IObjectEnvelope Envelope,
        T Value) : IObjectReadResult<T>;
}
