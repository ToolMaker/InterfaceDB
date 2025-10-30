using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Abstraction.Services.Serialization
{
    public interface IReaderAndWriter<TContract, TLocationType>  
        where TContract : class, IRepoDto<TContract, TLocationType>
    {
        IObjectReader<TContract, TLocationType> Reader { get; }
        IObjectWriter<TContract, TLocationType> Writer { get; }
    }
}
