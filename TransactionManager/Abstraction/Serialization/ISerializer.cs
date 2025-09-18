using System;
using TransactionManager.Implementation.Dtos;

namespace TransactionManager.Abstraction.Serialization
{
    public interface ISerializer
    {
        byte[] Serialize<T>(T value);
        T Deserialize<T>(ReadOnlyMemory<byte> bytes);

        ObjectEnvelope DeserializeEnvelope(ReadOnlyMemory<byte> bytes);
    }
}
