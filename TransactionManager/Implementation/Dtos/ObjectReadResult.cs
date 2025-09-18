using TransactionManager.Abstraction.Dtos;
using TransactionManager.Abstraction.Serialization;

namespace TransactionManager.Implementation.Dtos
{
    public sealed record ObjectReadResult<T>(
        string Id,
        string VersionTag,
        FileInfo File,
        IObjectEnvelope Envelope,
        T Value) : IObjectReadResult<T>;
}
