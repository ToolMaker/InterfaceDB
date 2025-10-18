using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Abstractions.Dtos
{
    public interface IObjectWriteResult <TContract>
        where TContract : class
    {
        IObjectEnvelope<TContract> Envelope { get; }
    }
}

