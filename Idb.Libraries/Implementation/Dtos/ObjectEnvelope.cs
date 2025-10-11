using System;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Serialization;

namespace Idb.Libraries.Implementation.Dtos
{
    [Serializable]
    public sealed class ObjectEnvelope : IObjectEnvelope
    {
        public string VersionTag { get; set; } = string.Empty;
        public byte[] Payload { get; set; } = Array.Empty<byte>();
        public string? TypeName { get; set; }
        public ObjectFingerprint Fingerprint { get; set; }
    }

    [Serializable]
    public sealed class LinkEnvelope : ILinkEnvelope
    {
        public string VersionTag { get; init; } = string.Empty;
        public byte[] Payload { get; init; } = Array.Empty<byte>();
    }
}
