using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Abstractions.Services.Serialization
{
    public interface IReaderAndWriter<TContract>  
        where TContract : class, IStoredContract<TContract>
    {
        IObjectReader<TContract> Reader { get; }
        IObjectWriter<TContract> Writer { get; }
    }
}
