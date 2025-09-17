using System.IO;

namespace TransactionManager.Dtos
{
    public sealed record ObjectReadResult<T>(
        string Id,
        string VersionTag,
        FileInfo File,
        ObjectEnvelope Envelope,
        T Value);
}
