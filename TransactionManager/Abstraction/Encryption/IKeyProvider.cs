namespace TransactionManager.Abstraction.Encruption
{

    public interface IKeyProvider
    {
        /// <summary>Returns the current key; a rotation strategy can return different keys over time.</summary>
        ReadOnlyMemory<byte> GetCurrentKey();
    }

}
