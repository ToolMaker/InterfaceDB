using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Encryption.Options;

namespace Idb.Libraries.Implementation.Encryption;

/// <summary>
/// Encrypts complete serialized payloads with AES-GCM.
/// </summary>
public sealed class AesGcmEncryptor : IRotatingEncryptor, IContextualEncryptor
{
    private static ReadOnlySpan<byte> Magic => "IDBE"u8;
    private const byte KeyedFormatVersion = 0x02;
    private const byte ContextualFormatVersion = 0x03;
    private const int TagSize = 16;
    private const int LegacyHeaderSize = 7;
    private const int KeyedFixedHeaderSize = 9;
    private const int ContextHashSize = 32;

    private readonly IKeyProvider _keyProvider;
    private readonly IRotatingKeyProvider? _rotatingKeyProvider;
    private readonly AesGcmEncryptorOptions _options;

    public AesGcmEncryptor(
        IKeyProvider keyProvider,
        AesGcmEncryptorOptions? options = null)
    {
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _rotatingKeyProvider = keyProvider as IRotatingKeyProvider;
        _options = options ?? new AesGcmEncryptorOptions();

        if (_options.AlgorithmVersion is KeyedFormatVersion or ContextualFormatVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Legacy algorithm versions '{KeyedFormatVersion}' and '{ContextualFormatVersion}' are reserved for InterfaceDB framing.");
        }

        if (_options.NonceSize > byte.MaxValue || !IsLegalSize(_options.NonceSize, AesGcm.NonceByteSizes))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"AES-GCM nonce size '{_options.NonceSize}' is not supported by this platform.");
        }
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plain)
        => _rotatingKeyProvider is null
            ? EncryptLegacy(plain)
            : EncryptKeyed(plain, _rotatingKeyProvider.GetActiveKeyId());

    public byte[] Decrypt(ReadOnlySpan<byte> cipher)
        => DecryptWithMetadata(cipher).Plaintext;

    public byte[] Encrypt(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> associatedData)
        => EncryptContextual(plain, associatedData);

    public byte[] Decrypt(ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> associatedData)
        => DecryptWithMetadata(cipher, associatedData).Plaintext;

    public EncryptionDecryptionResult DecryptWithMetadata(ReadOnlySpan<byte> cipher)
    {
        if (!HasEncryptedHeader(cipher))
        {
            if (_options.AllowUnencryptedPayloads)
            {
                return new EncryptionDecryptionResult(
                    cipher.ToArray(),
                    null,
                    _options.RotationMode == EncryptionRotationMode.ReencryptOnRead,
                    _rotatingKeyProvider is null);
            }

            throw new CryptographicException("Payload is not an InterfaceDB encrypted payload.");
        }

        if (cipher.Length < LegacyHeaderSize)
        {
            throw new CryptographicException("Encrypted payload header is incomplete.");
        }

        return cipher[4] switch
        {
            ContextualFormatVersion => throw new CryptographicException(
                "This encrypted payload is bound to a storage context and must be decrypted through the contextual API."),
            KeyedFormatVersion => DecryptKeyed(cipher),
            var version when version == _options.AlgorithmVersion => DecryptLegacy(cipher),
            var version => throw new CryptographicException(
                $"Unsupported encrypted payload format version '{version}'.")
        };
    }

    public EncryptionDecryptionResult DecryptWithMetadata(
        ReadOnlySpan<byte> cipher,
        ReadOnlySpan<byte> associatedData)
    {
        if (!HasEncryptedHeader(cipher))
        {
            if (_options.AllowUnencryptedPayloads && _options.AllowContextlessPayloadMigration)
            {
                return new EncryptionDecryptionResult(
                    cipher.ToArray(), null, RequiresReencryption: true, UsesCurrentEncryption: false);
            }

            throw new CryptographicException("Payload is not an InterfaceDB encrypted payload.");
        }

        if (cipher.Length < LegacyHeaderSize)
        {
            throw new CryptographicException("Encrypted payload header is incomplete.");
        }

        if (cipher[4] == ContextualFormatVersion)
        {
            return DecryptContextual(cipher, associatedData);
        }

        if (!_options.AllowContextlessPayloadMigration)
        {
            throw new CryptographicException(
                "The encrypted payload predates repository-context authentication. Enable the explicit contextless migration option only for a controlled upgrade.");
        }

        var legacy = DecryptWithMetadata(cipher);
        return legacy with { RequiresReencryption = true, UsesCurrentEncryption = false };
    }

    private byte[] EncryptLegacy(ReadOnlySpan<byte> plain)
    {
        var key = GetValidatedKey(_keyProvider.GetCurrentKey());
        var nonce = RandomNumberGenerator.GetBytes(_options.NonceSize);
        var result = new byte[LegacyHeaderSize + nonce.Length + TagSize + plain.Length];
        var header = result.AsSpan(0, LegacyHeaderSize);

        Magic.CopyTo(header);
        header[4] = _options.AlgorithmVersion;
        header[5] = checked((byte)nonce.Length);
        header[6] = TagSize;

        EncryptPayload(plain, key.Span, nonce, header, result.AsSpan(LegacyHeaderSize));
        return result;
    }

    private byte[] EncryptKeyed(ReadOnlySpan<byte> plain, string keyId)
    {
        ValidateKeyId(keyId);
        var keyIdBytes = Encoding.UTF8.GetBytes(keyId);
        if (keyIdBytes.Length > ushort.MaxValue)
        {
            throw new CryptographicException("Encryption key identifiers cannot exceed 65535 UTF-8 bytes.");
        }

        var key = GetValidatedKey(_rotatingKeyProvider!.GetKey(keyId));
        var nonce = RandomNumberGenerator.GetBytes(_options.NonceSize);
        var headerSize = KeyedFixedHeaderSize + keyIdBytes.Length;
        var result = new byte[headerSize + nonce.Length + TagSize + plain.Length];
        var header = result.AsSpan(0, headerSize);

        Magic.CopyTo(header);
        header[4] = KeyedFormatVersion;
        header[5] = checked((byte)nonce.Length);
        header[6] = TagSize;
        BinaryPrimitives.WriteUInt16LittleEndian(header[7..9], checked((ushort)keyIdBytes.Length));
        keyIdBytes.CopyTo(header[KeyedFixedHeaderSize..]);

        EncryptPayload(plain, key.Span, nonce, header, result.AsSpan(headerSize));
        return result;
    }

    private byte[] EncryptContextual(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> associatedData)
    {
        string? keyId = null;
        ReadOnlyMemory<byte> key;
        if (_rotatingKeyProvider is null)
        {
            key = GetValidatedKey(_keyProvider.GetCurrentKey());
        }
        else
        {
            keyId = _rotatingKeyProvider.GetActiveKeyId();
            ValidateKeyId(keyId);
            key = GetValidatedKey(_rotatingKeyProvider.GetKey(keyId));
        }

        var keyIdBytes = keyId is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(keyId);
        if (keyIdBytes.Length > ushort.MaxValue)
        {
            throw new CryptographicException("Encryption key identifiers cannot exceed 65535 UTF-8 bytes.");
        }

        var nonce = RandomNumberGenerator.GetBytes(_options.NonceSize);
        var headerSize = KeyedFixedHeaderSize + keyIdBytes.Length + ContextHashSize;
        var result = new byte[headerSize + nonce.Length + TagSize + plain.Length];
        var header = result.AsSpan(0, headerSize);

        Magic.CopyTo(header);
        header[4] = ContextualFormatVersion;
        header[5] = checked((byte)nonce.Length);
        header[6] = TagSize;
        BinaryPrimitives.WriteUInt16LittleEndian(header[7..9], checked((ushort)keyIdBytes.Length));
        keyIdBytes.CopyTo(header[KeyedFixedHeaderSize..]);
        SHA256.HashData(associatedData, header[(KeyedFixedHeaderSize + keyIdBytes.Length)..]);

        EncryptPayload(plain, key.Span, nonce, header, result.AsSpan(headerSize));
        return result;
    }

    private EncryptionDecryptionResult DecryptLegacy(ReadOnlySpan<byte> cipher)
    {
        var nonceSize = cipher[5];
        var tagSize = cipher[6];
        ValidateFraming(cipher, LegacyHeaderSize, nonceSize, tagSize);

        var keyId = _options.LegacyKeyId;
        ReadOnlyMemory<byte> key;
        if (_rotatingKeyProvider is not null && !string.IsNullOrWhiteSpace(keyId))
        {
            key = _rotatingKeyProvider.GetKey(keyId);
        }
        else
        {
            key = _keyProvider.GetCurrentKey();
        }

        var plaintext = DecryptPayload(
            cipher,
            LegacyHeaderSize,
            nonceSize,
            tagSize,
            GetValidatedKey(key).Span);
        var needsRotation = _rotatingKeyProvider is not null
            && _options.RotationMode == EncryptionRotationMode.ReencryptOnRead;
        return new EncryptionDecryptionResult(
            plaintext,
            keyId,
            needsRotation,
            _rotatingKeyProvider is null);
    }

    private EncryptionDecryptionResult DecryptKeyed(ReadOnlySpan<byte> cipher)
    {
        if (_rotatingKeyProvider is null)
        {
            throw new CryptographicException(
                "This encrypted payload identifies a historical key, but the configured key provider cannot resolve keys by identifier.");
        }

        if (cipher.Length < KeyedFixedHeaderSize)
        {
            throw new CryptographicException("Encrypted payload key header is incomplete.");
        }

        var nonceSize = cipher[5];
        var tagSize = cipher[6];
        var keyIdSize = BinaryPrimitives.ReadUInt16LittleEndian(cipher[7..9]);
        var headerSize = KeyedFixedHeaderSize + keyIdSize;
        ValidateFraming(cipher, headerSize, nonceSize, tagSize);

        var keyId = Encoding.UTF8.GetString(cipher.Slice(KeyedFixedHeaderSize, keyIdSize));
        ValidateKeyId(keyId);
        var plaintext = DecryptPayload(
            cipher,
            headerSize,
            nonceSize,
            tagSize,
            GetValidatedKey(_rotatingKeyProvider.GetKey(keyId)).Span);
        var usesCurrentEncryption = string.Equals(
            keyId,
            _rotatingKeyProvider.GetActiveKeyId(),
            StringComparison.Ordinal);
        var needsRotation = _options.RotationMode == EncryptionRotationMode.ReencryptOnRead
            && !usesCurrentEncryption;
        return new EncryptionDecryptionResult(
            plaintext,
            keyId,
            needsRotation,
            usesCurrentEncryption);
    }

    private EncryptionDecryptionResult DecryptContextual(
        ReadOnlySpan<byte> cipher,
        ReadOnlySpan<byte> associatedData)
    {
        if (cipher.Length < KeyedFixedHeaderSize + ContextHashSize)
        {
            throw new CryptographicException("Encrypted payload context header is incomplete.");
        }

        var nonceSize = cipher[5];
        var tagSize = cipher[6];
        var keyIdSize = BinaryPrimitives.ReadUInt16LittleEndian(cipher[7..9]);
        var headerSize = KeyedFixedHeaderSize + keyIdSize + ContextHashSize;
        ValidateFraming(cipher, headerSize, nonceSize, tagSize);

        Span<byte> expectedContextHash = stackalloc byte[ContextHashSize];
        SHA256.HashData(associatedData, expectedContextHash);
        var storedContextHash = cipher.Slice(KeyedFixedHeaderSize + keyIdSize, ContextHashSize);
        if (!CryptographicOperations.FixedTimeEquals(storedContextHash, expectedContextHash))
        {
            throw new CryptographicException(
                "Encrypted payload authentication context does not match this repository record.");
        }

        string? keyId = null;
        ReadOnlyMemory<byte> key;
        if (keyIdSize == 0)
        {
            key = GetValidatedKey(_keyProvider.GetCurrentKey());
        }
        else
        {
            if (_rotatingKeyProvider is null)
            {
                throw new CryptographicException(
                    "This encrypted payload identifies a historical key, but the configured key provider cannot resolve keys by identifier.");
            }

            keyId = Encoding.UTF8.GetString(cipher.Slice(KeyedFixedHeaderSize, keyIdSize));
            ValidateKeyId(keyId);
            key = GetValidatedKey(_rotatingKeyProvider.GetKey(keyId));
        }

        var plaintext = DecryptPayload(cipher, headerSize, nonceSize, tagSize, key.Span);
        var usesCurrentEncryption = _rotatingKeyProvider is null
            || string.Equals(keyId, _rotatingKeyProvider.GetActiveKeyId(), StringComparison.Ordinal);
        var needsRotation = _options.RotationMode == EncryptionRotationMode.ReencryptOnRead
            && !usesCurrentEncryption;
        return new EncryptionDecryptionResult(
            plaintext,
            keyId,
            needsRotation,
            usesCurrentEncryption);
    }

    private static void EncryptPayload(
        ReadOnlySpan<byte> plain,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> header,
        Span<byte> payload)
    {
        nonce.CopyTo(payload);
        var tag = payload.Slice(nonce.Length, TagSize);
        var ciphertext = payload[(nonce.Length + TagSize)..];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, ciphertext, tag, header);
    }

    private static byte[] DecryptPayload(
        ReadOnlySpan<byte> cipher,
        int headerSize,
        int nonceSize,
        int tagSize,
        ReadOnlySpan<byte> key)
    {
        var header = cipher[..headerSize];
        var nonce = cipher.Slice(headerSize, nonceSize);
        var tag = cipher.Slice(headerSize + nonceSize, tagSize);
        var ciphertext = cipher[(headerSize + nonceSize + tagSize)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, tagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, header);
        return plaintext;
    }

    private static void ValidateFraming(
        ReadOnlySpan<byte> cipher,
        int headerSize,
        int nonceSize,
        int tagSize)
    {
        if (nonceSize == 0 || tagSize == 0 || cipher.Length < headerSize + nonceSize + tagSize)
        {
            throw new CryptographicException("Encrypted payload framing is invalid.");
        }
    }

    private static ReadOnlyMemory<byte> GetValidatedKey(ReadOnlyMemory<byte> key)
    {
        if (key.Length is not (16 or 24 or 32))
        {
            throw new CryptographicException("AES-GCM requires a 128, 192, or 256-bit key.");
        }

        return key;
    }

    private static void ValidateKeyId(string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new CryptographicException("Encryption key identifiers cannot be null or whitespace.");
        }
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
