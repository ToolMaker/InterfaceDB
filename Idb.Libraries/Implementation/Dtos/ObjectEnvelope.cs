using System;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Serialization;

namespace Idb.Libraries.Implementation.Dtos
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
