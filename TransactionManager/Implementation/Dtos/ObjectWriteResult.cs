using System;
using System.IO;
using TransactionManager.Abstraction.Dtos;

namespace TransactionManager.Implementation.Dtos
{
    public sealed record ObjectWriteResult(
        string Id,
        string VersionTag,
        Type ObjectType,
        FileInfo File,
        ObjectEnvelope Envelope) : IObjectWriteResult;
}
