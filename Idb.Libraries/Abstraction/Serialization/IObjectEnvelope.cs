namespace Idb.Libraries.Abstractions.Serialization
{
    public interface IObjectEnvelope
    {
        byte[] Payload { get; set; }
        string VersionTag { get; set; }
        string? TypeName { get; set; }
        ObjectFingerprint Fingerprint { get; set; }
    }
}
