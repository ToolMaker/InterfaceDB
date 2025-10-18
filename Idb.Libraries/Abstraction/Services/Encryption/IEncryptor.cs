namespace Idb.Libraries.Abstractions.Services.Encryption
{
    /// <summary>
    /// Provides symmetric encryption and decryption services for payload byte buffers.
    /// </summary>
    public interface IEncryptor
    {
        /// <summary>
        /// Encrypts plaintext bytes into a cipher payload.
        /// </summary>
        /// <param name="plain">Raw bytes to protect.</param>
        /// <returns>Encrypted bytes that can be stored on disk.</returns>
        byte[] Encrypt(ReadOnlySpan<byte> plain);

        /// <summary>
        /// Decrypts ciphertext bytes back into the original plaintext.
        /// </summary>
        /// <param name="cipher">Encrypted payload produced by <see cref="Encrypt(ReadOnlySpan{byte})"/>.</param>
        /// <returns>Recovered plaintext bytes.</returns>
        byte[] Decrypt(ReadOnlySpan<byte> cipher);
    }
}


