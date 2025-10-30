using Idb.Libraries.Abstraction.Services.Serialization;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization
{
    public class ObjectReaderAndWriter<TContract, TLocationType> : IReaderAndWriter<TContract, TLocationType>
        where TContract : class, IRepoDto<TContract, TLocationType>
    {
        public ObjectReaderAndWriter(
            IObjectReader<TContract, TLocationType> reader,
            IObjectWriter<TContract, TLocationType> writer)
        {
            Reader = reader ?? throw new ArgumentNullException(nameof(reader));
            Writer = writer ?? throw new ArgumentNullException(nameof(writer));
        }

        public IObjectReader<TContract, TLocationType> Reader { get; }

        public IObjectWriter<TContract, TLocationType> Writer { get; }

    }
}
