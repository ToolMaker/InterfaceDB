namespace TransactionManager.Dtos
{
    /// <summary>
    /// Used in the future to define the caching scheme for links.                                                                              
    /// </summary>
    public interface ILinkEnvelope
    {
        byte[] Payload { get; init; }
        short SchemaVersion { get; init; }
    }
}