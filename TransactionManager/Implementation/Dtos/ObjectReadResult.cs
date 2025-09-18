using System.IO;
using TransactionManager.Abstraction.Dtos;

namespace TransactionManager.Implementation.Dtos
{
    public sealed record ObjectReadResult<T>(
        string Id,
        string VersionTag,
        FileInfo File,
        ObjectEnvelope Envelope,
        T Value) : IObjectReadResult<T>;
}
