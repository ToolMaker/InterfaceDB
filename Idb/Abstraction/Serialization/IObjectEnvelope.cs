namespace TransactionManager.Abstraction.Serialization
{
    public interface IObjectEnvelope
    {
        byte[] Payload { get; init; }
        string VersionTag { get; init; }
        string? TypeName { get; init; }
    }
}
