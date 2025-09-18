namespace TransactionManager.Abstraction.Serialization
{
    public interface ISerializer
    {
        byte[] Serialize<T>(T value);
        T Deserialize<T>(ReadOnlyMemory<byte> bytes);

        IObjectEnvelope DeserializeEnvelope(ReadOnlyMemory<byte> bytes);
    }
}
