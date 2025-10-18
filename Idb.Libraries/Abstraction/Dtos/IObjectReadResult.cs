using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Abstractions.Dtos
{
    public interface IObjectReadResult<TContract>
        where TContract : class
    {
        string Id { get; }
        string VersionTag { get; }
        FileInfo File { get; }
        IObjectEnvelope<TContract> Envelope { get; }
        TContract Value { get; }
    }
}

