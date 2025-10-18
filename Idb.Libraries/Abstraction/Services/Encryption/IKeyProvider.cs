namespace Idb.Libraries.Abstractions.Services.Encryption
{
    /// <summary>
    /// Supplies encryption keys for the active scope, optionally supporting key rotation.
    /// </summary>
    public interface IKeyProvider
    {
        /// <summary>
        /// Returns the encryption key that should be used for the current operation.
        /// </summary>
        /// <remarks>
        /// Implementations can surface rotation strategies by returning different keys over time.
        /// </remarks>
        ReadOnlyMemory<byte> GetCurrentKey();
    }

}

