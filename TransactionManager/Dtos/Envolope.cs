namespace TransactionManager.Dtos
{
    [Serializable]           // Serializer-agnostic marker
    public sealed class Envelope
    {
        public short SchemaVersion { get; init; }  // 2-byte version
        public byte[] Payload { get; init; }  // object bytes
    }
}
