namespace Idb.Libraries.Abstractions.Services
{
    using Idb.Libraries.Abstractions.Dtos;

    /// <summary>
    /// Creates a storage key value for a contract instance.
    /// </summary>
    /// <typeparam name="TContract">Contract type associated with the value.</typeparam>
    /// <typeparam name="TKey">Key value type produced by the factory.</typeparam>
    public interface IEnvelopeKeyFactory<TContract, TKey>
        where TContract : class, IStoredContract<TContract>
    {
        /// <summary>
        /// Creates a new key for the supplied contract instance.
        /// </summary>
        TKey CreateKey(TContract instance);
    }
}
