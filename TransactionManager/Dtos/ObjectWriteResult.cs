using System;
using System.IO;

namespace TransactionManager.Dtos
{
    public sealed record ObjectWriteResult(
        string Id,
        short SchemaVersion,
        Type ObjectType,
        FileInfo File,
        ObjectEnvelope Envelope);
}
