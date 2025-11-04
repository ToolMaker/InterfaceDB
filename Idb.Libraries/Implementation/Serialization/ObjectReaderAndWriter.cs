using Idb.Libraries.Abstraction.Services.Serialization;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization
{
    public class ObjectReaderAndWriter<TContract> : IReaderAndWriter<TContract>
        where TContract : class, IRepoDto<TContract>
    {
        public ObjectReaderAndWriter(
            IObjectReader<TContract> reader,
            IObjectWriter<TContract> writer)
        {
            Reader = reader ?? throw new ArgumentNullException(nameof(reader));
            Writer = writer ?? throw new ArgumentNullException(nameof(writer));
        }

        public IObjectReader<TContract> Reader { get; }

        public IObjectWriter<TContract> Writer { get; }

    }
}
