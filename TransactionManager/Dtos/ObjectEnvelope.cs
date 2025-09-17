using System;
using TransactionManager.Abstraction.Serialization;

namespace TransactionManager.Dtos
{
    [Serializable]
    public sealed class ObjectEnvelope : IObjectEnvelope
    {
        public short SchemaVersion { get; init; }
        public byte[] Payload { get; init; } = Array.Empty<byte>();
        public string? TypeName { get; init; }
    }

    [Serializable]
    public sealed class LinkEnvelope : ILinkEnvelope
    {
        public short SchemaVersion { get; init; }
        public byte[] Payload { get; init; } = Array.Empty<byte>();
    }
}
