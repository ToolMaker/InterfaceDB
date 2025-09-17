namespace TransactionManager.Abstraction.Serialization
{
    public interface IObjectEnvelope
    {
        byte[] Payload { get; init; }
        short SchemaVersion { get; init; }
    }
}