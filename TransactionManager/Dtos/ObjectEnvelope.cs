using System;
using TransactionManager.Abstraction.Serialization;

namespace TransactionManager.Dtos
{
    [Serializable]
    public sealed class ObjectEnvelope : IObjectEnvelope
    {
        public string VersionTag { get; init; } = string.Empty;
        public byte[] Payload { get; init; } = Array.Empty<byte>();
        public string? TypeName { get; init; }
    }

    [Serializable]
    public sealed class LinkEnvelope : ILinkEnvelope
    {
        public string VersionTag { get; init; } = string.Empty;
        public byte[] Payload { get; init; } = Array.Empty<byte>();
    }
}
