using ProtoBuf;
namespace Idb.Serialization.Protobuf;

// Persistent field numbers must never be reused or renumbered.
[ProtoContract]
internal sealed class EnvelopeMessage
{
    [ProtoMember(1)] public int FormatVersion { get; set; }
    [ProtoMember(2)] public string Key { get; set; } = "";
    [ProtoMember(3)] public string ContractVersion { get; set; } = "";
    [ProtoMember(4)] public long Revision { get; set; }
    [ProtoMember(5)] public string TypeId { get; set; } = "";
    [ProtoMember(6)] public bool CacheObject { get; set; }
    [ProtoMember(7)] public byte[] Payload { get; set; } = [];
    [ProtoMember(8)] public List<EncryptedFieldMessage> EncryptedFields { get; } = [];
    [ProtoMember(9)] public bool HasExtensionMetadata { get; set; }
    [ProtoMember(10)] public byte[] ExtensionMetadata { get; set; } = [];
}

[ProtoContract]
internal sealed class EncryptedFieldMessage
{
    [ProtoMember(1)] public string Name { get; set; } = "";
    [ProtoMember(2)] public string ProfileName { get; set; } = "";
    [ProtoMember(3)] public string PropertyTypeName { get; set; } = "";
    [ProtoMember(4)] public byte[] Ciphertext { get; set; } = [];
}
