namespace TransactionManager.Dtos
{
    public interface ISerializer
    {
        byte[] Serialize<T>(T value);
        T Deserialize<T>(ReadOnlyMemory<byte> bytes);

        // Convenience for Envelope
        Envelope DeserializeEnvelope(ReadOnlyMemory<byte> bytes);
    }
}