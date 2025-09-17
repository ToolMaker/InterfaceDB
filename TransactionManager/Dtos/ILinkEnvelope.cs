namespace TransactionManager.Dtos
{
    /// <summary>
    /// Used in the future to define the caching scheme for links.
    /// </summary>
    public interface ILinkEnvelope
    {
        byte[] Payload { get; init; }
        string VersionTag { get; init; }
    }
}
