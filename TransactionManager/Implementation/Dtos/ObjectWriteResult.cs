using TransactionManager.Abstraction.Dtos;
using TransactionManager.Abstraction.Serialization;

namespace TransactionManager.Implementation.Dtos
{
    public sealed record ObjectWriteResult(
        string Id,
        string VersionTag,
        Type ObjectType,
        FileInfo File,
        IObjectEnvelope Envelope) : IObjectWriteResult;
}
