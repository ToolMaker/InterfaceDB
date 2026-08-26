using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// Applies encryption after envelope serialization and decryption before deserialization.
/// </summary>
public sealed class EncryptingObjectSerializer<TContract> :
    IObjectSerializer<TContract>,
    IRotationAwareObjectSerializer<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly BinaryObjectSerializer<TContract> _serializer;
    private readonly IEncryptor _encryptor;
    private readonly bool _encryptionDisabled;

    public EncryptingObjectSerializer(
        BinaryObjectSerializer<TContract> serializer,
        IEncryptor encryptor)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
        _encryptionDisabled = encryptor is EncryptionNone;
    }

    public byte[] Serialize(IObjectEnvelope<TContract> envalope)
    {
        var serialized = _serializer.Serialize(envalope);
        return _encryptionDisabled ? serialized : _encryptor.Encrypt(serialized);
    }

    public IObjectEnvelope<TContract> Deserialize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return _serializer.Deserialize(_encryptionDisabled ? data : _encryptor.Decrypt(data));
    }

    public RotationAwareDeserializationResult<TContract> DeserializeForRead(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (_encryptionDisabled)
        {
            return new RotationAwareDeserializationResult<TContract>(_serializer.Deserialize(data), null);
        }

        if (_encryptor is not IRotatingEncryptor rotatingEncryptor)
        {
            return new RotationAwareDeserializationResult<TContract>(Deserialize(data), null);
        }

        var decrypted = rotatingEncryptor.DecryptWithMetadata(data);
        var envelope = _serializer.Deserialize(decrypted.Plaintext);
        var replacement = decrypted.RequiresReencryption
            ? _encryptor.Encrypt(decrypted.Plaintext)
            : null;
        return new RotationAwareDeserializationResult<TContract>(envelope, replacement);
    }

    public async ValueTask SerializeAsync(
        IObjectEnvelope<TContract> envalope,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (_encryptionDisabled)
        {
            await _serializer.SerializeAsync(envalope, destination, cancellationToken).ConfigureAwait(false);
            return;
        }

        var payload = Serialize(envalope);
        await destination.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IObjectEnvelope<TContract>> DeserializeAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_encryptionDisabled)
        {
            return await _serializer.DeserializeAsync(source, cancellationToken).ConfigureAwait(false);
        }

        using var memory = new MemoryStream();
        await source.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return Deserialize(memory.ToArray());
    }
}
