using System;
using System.IO;

namespace TransactionManager.Dtos
{
    public sealed record ObjectWriteResult(
        string Id,
        string VersionTag,
        Type ObjectType,
        FileInfo File,
        ObjectEnvelope Envelope);
}
