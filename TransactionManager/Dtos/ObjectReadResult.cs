using System.IO;

namespace TransactionManager.Dtos
{
    public sealed record ObjectReadResult<T>(
        string Id,
        short SchemaVersion,
        FileInfo File,
        ObjectEnvelope Envelope,
        T Value);
}
