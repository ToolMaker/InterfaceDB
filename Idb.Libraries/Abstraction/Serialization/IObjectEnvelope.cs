namespace Idb.Libraries.Abstractions.Serialization
{
    public interface IObjectEnvelope
    {
        byte[] Payload { get; init; }
        string VersionTag { get; init; }
        string? TypeName { get; init; }
    }
}
