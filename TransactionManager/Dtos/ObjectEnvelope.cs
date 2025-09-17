using TransactionManager.Abstraction.Serialization;

namespace TransactionManager.Dtos
{
    [Serializable]           // Serializer-agnostic marker
    public sealed class ObjectEnvelope : IObjectEnvelope
    {
        public short SchemaVersion { get; init; }  // 2-byte version
        public byte[] Payload { get; init; }  // object bytes
    }

    [Serializable]
    public sealed class LinkEnvelope : ILinkEnvelope
    {
        public short SchemaVersion { get; init; }  // 2-byte version
        public byte[] Payload { get; init; }  // object bytes
    }
}
