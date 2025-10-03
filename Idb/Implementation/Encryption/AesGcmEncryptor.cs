using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using TransactionManager.Abstraction.Encryption;
using TransactionManager.Implementation.Options;

namespace TransactionManager.Implementation.Encryption
{
    public sealed class AesGcmEncryptor : IEncryptor, IDisposable
    {
        private readonly AesGcm _aes;
        private readonly AesGcmEncryptorOptions _opts;

        public AesGcmEncryptor(IKeyProvider keyProvider,
                               IOptions<AesGcmEncryptorOptions> options)
        {
            _opts = options.Value;
            var key = keyProvider.GetCurrentKey();
            _aes = new AesGcm(key.Span);
        }

        public byte[] Encrypt(ReadOnlySpan<byte> plain)
        {
            Span<byte> nonce = stackalloc byte[_opts.NonceSize];
            RandomNumberGenerator.Fill(nonce);

            Span<byte> tag = stackalloc byte[AesGcm.TagByteSizes.MaxSize];
            byte[] cipher = new byte[plain.Length];

            _aes.Encrypt(nonce, plain, cipher, tag);

            var headerSize = 1 + _opts.NonceSize + tag.Length;
            var output = new byte[headerSize + cipher.Length];

            output[0] = _opts.AlgorithmVersion;
            nonce.CopyTo(output.AsSpan(1));
            tag.CopyTo(output.AsSpan(1 + _opts.NonceSize));
            cipher.CopyTo(output.AsSpan(headerSize));

            return output;
        }

        public byte[] Decrypt(ReadOnlySpan<byte> packed)
        {
            const byte ExpectedVersion = 0x01;
            const int VersionSize = 1;
            int nonceSize = AesGcm.NonceByteSizes.MaxSize; // 12
            int tagSize = AesGcm.TagByteSizes.MaxSize;   // 16
            
            var minRequired = VersionSize + nonceSize + tagSize;
            if (packed.Length < minRequired)
                throw new CryptographicException("Ciphertext too short");

            if (packed[0] != ExpectedVersion)
                throw new CryptographicException($"Unsupported cipher version {packed[0]}");

            ReadOnlySpan<byte> nonce = packed.Slice(VersionSize, nonceSize);
            ReadOnlySpan<byte> tag = packed.Slice(VersionSize + nonceSize, tagSize);
            ReadOnlySpan<byte> cipher = packed.Slice(VersionSize + nonceSize + tagSize);

            byte[] plain = new byte[cipher.Length];
            _aes.Decrypt(nonce, cipher, tag, plain);

            return plain;
        }


        public void Dispose() => _aes.Dispose();
    }
}
