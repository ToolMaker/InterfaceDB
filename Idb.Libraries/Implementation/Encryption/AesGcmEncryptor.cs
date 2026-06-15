using System.Security.Cryptography;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Encryption.Options;

namespace Idb.Libraries.Implementation.Encryption;

/// <summary>
/// Encrypts complete serialized payloads with AES-GCM.
/// </summary>
public sealed class AesGcmEncryptor : IEncryptor
{
    private static ReadOnlySpan<byte> Magic => "IDBE"u8;
    private const int TagSize = 16;
    private const int HeaderSize = 7;

    private readonly IKeyProvider _keyProvider;
    private readonly AesGcmEncryptorOptions _options;

    public AesGcmEncryptor(
        IKeyProvider keyProvider,
        AesGcmEncryptorOptions? options = null)
    {
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _options = options ?? new AesGcmEncryptorOptions();

        if (_options.NonceSize > byte.MaxValue || !IsLegalSize(_options.NonceSize, AesGcm.NonceByteSizes))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"AES-GCM nonce size '{_options.NonceSize}' is not supported by this platform.");
        }
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plain)
    {
        var key = GetValidatedKey();
        var nonce = RandomNumberGenerator.GetBytes(_options.NonceSize);
        var result = new byte[HeaderSize + nonce.Length + TagSize + plain.Length];
        var header = result.AsSpan(0, HeaderSize);

        Magic.CopyTo(header);
        header[4] = _options.AlgorithmVersion;
        header[5] = checked((byte)nonce.Length);
        header[6] = TagSize;

        nonce.CopyTo(result.AsSpan(HeaderSize, nonce.Length));
        var tag = result.AsSpan(HeaderSize + nonce.Length, TagSize);
        var ciphertext = result.AsSpan(HeaderSize + nonce.Length + TagSize);

        using var aes = new AesGcm(key.Span, TagSize);
        aes.Encrypt(nonce, plain, ciphertext, tag, header);
        return result;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> cipher)
    {
        if (!HasEncryptedHeader(cipher))
        {
            if (_options.AllowUnencryptedPayloads)
            {
                return cipher.ToArray();
            }

            throw new CryptographicException("Payload is not an InterfaceDB encrypted payload.");
        }

        if (cipher.Length < HeaderSize)
        {
            throw new CryptographicException("Encrypted payload header is incomplete.");
        }

        var algorithmVersion = cipher[4];
        if (algorithmVersion != _options.AlgorithmVersion)
        {
            throw new CryptographicException(
                $"Unsupported encrypted payload algorithm version '{algorithmVersion}'.");
        }

        var nonceSize = cipher[5];
        var tagSize = cipher[6];
        if (nonceSize == 0 || tagSize == 0 || cipher.Length < HeaderSize + nonceSize + tagSize)
        {
            throw new CryptographicException("Encrypted payload framing is invalid.");
        }

        var header = cipher[..HeaderSize];
        var nonce = cipher.Slice(HeaderSize, nonceSize);
        var tag = cipher.Slice(HeaderSize + nonceSize, tagSize);
        var ciphertext = cipher[(HeaderSize + nonceSize + tagSize)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(GetValidatedKey().Span, tagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, header);
        return plaintext;
    }

    private ReadOnlyMemory<byte> GetValidatedKey()
    {
        var key = _keyProvider.GetCurrentKey();
        if (key.Length is not (16 or 24 or 32))
        {
            throw new CryptographicException("AES-GCM requires a 128, 192, or 256-bit key.");
        }

        return key;
    }

    private static bool HasEncryptedHeader(ReadOnlySpan<byte> payload)
        => payload.Length >= Magic.Length && payload[..Magic.Length].SequenceEqual(Magic);

    private static bool IsLegalSize(int size, KeySizes legalSizes)
        => size >= legalSizes.MinSize
            && size <= legalSizes.MaxSize
            && (legalSizes.SkipSize == 0
                ? size == legalSizes.MinSize
                : (size - legalSizes.MinSize) % legalSizes.SkipSize == 0);
}
