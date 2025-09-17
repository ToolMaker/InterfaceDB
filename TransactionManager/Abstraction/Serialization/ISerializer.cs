using TransactionManager.Dtos;

namespace TransactionManager.Abstraction.Serialization
{
    public interface ISerializer
    {
        byte[] Serialize<T>(T value);
        T Deserialize<T>(ReadOnlyMemory<byte> bytes);

        // Convenience for Envelope
        ObjectEnvelope DeserializeEnvelope(ReadOnlyMemory<byte> bytes);
    }
}