using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// Applies encryption after envelope serialization and decryption before deserialization.
/// </summary>
public sealed class EncryptingObjectSerializer<TContract> :
    IRevisionObjectSerializer<TContract>,
    IRotationAwareObjectSerializer<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly BinaryObjectSerializer<TContract> _serializer;
    private readonly IEncryptor _encryptor;
    private readonly bool _encryptionDisabled;

    public long ReadRevision(ReadOnlyMemory<byte> payload)
    {
        ValidateLength(payload.Length);
        if (_encryptionDisabled)
        {
            return _serializer.ReadRevision(payload);
        }

        var plaintext = _encryptor.Decrypt(payload.Span);
        ValidateLength(plaintext.Length);
        return _serializer.ReadRevision(plaintext);
    }

    public async ValueTask<long> ReadRevisionAsync(Stream source, CancellationToken cancellationToken = default)
    {
        if (_encryptionDisabled) return await _serializer.ReadRevisionAsync(source, cancellationToken).ConfigureAwait(false);
        var payload = await BoundedStoredPayload
            .ReadStreamAsync(source, _serializer.MaximumPayloadBytes, cancellationToken)
            .ConfigureAwait(false);
        return ReadRevision(payload);
    }

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
        ValidateLength(serialized.Length);
        var payload = _encryptionDisabled ? serialized : _encryptor.Encrypt(serialized);
        ValidateLength(payload.Length);
        return payload;
    }

    public byte[] Serialize(
        IObjectEnvelope<TContract> envelope,
        ReadOnlySpan<byte> associatedData)
    {
        var serialized = _serializer.Serialize(envelope);
        ValidateLength(serialized.Length);
        if (_encryptionDisabled)
        {
            return serialized;
        }

        var contextual = _encryptor as IContextualEncryptor
            ?? throw new InvalidOperationException(
                $"Encryptor '{_encryptor.GetType().FullName}' does not support authenticated storage contexts.");
        var payload = contextual.Encrypt(serialized, associatedData);
        ValidateLength(payload.Length);
        return payload;
    }

    public IObjectEnvelope<TContract> Deserialize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ValidateLength(data.Length);
        if (_encryptionDisabled)
        {
            return _serializer.Deserialize(data);
        }

        var plaintext = _encryptor.Decrypt(data);
        ValidateLength(plaintext.Length);
        return _serializer.Deserialize(plaintext);
    }

    public IObjectEnvelope<TContract> Deserialize(
        byte[] data,
        ReadOnlySpan<byte> associatedData)
    {
        ArgumentNullException.ThrowIfNull(data);
        ValidateLength(data.Length);
        if (_encryptionDisabled)
        {
            return _serializer.Deserialize(data);
        }

        var contextual = _encryptor as IContextualEncryptor
            ?? throw new InvalidOperationException(
                $"Encryptor '{_encryptor.GetType().FullName}' does not support authenticated storage contexts.");
        var plaintext = contextual.Decrypt(data, associatedData);
        ValidateLength(plaintext.Length);
        return _serializer.Deserialize(plaintext);
    }

    public RotationAwareDeserializationResult<TContract> DeserializeForRead(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ValidateLength(data.Length);
        if (_encryptionDisabled)
        {
            return new RotationAwareDeserializationResult<TContract>(_serializer.Deserialize(data), null);
        }

        if (_encryptor is not IRotatingEncryptor rotatingEncryptor)
        {
            return new RotationAwareDeserializationResult<TContract>(Deserialize(data), null);
        }

        var decrypted = rotatingEncryptor.DecryptWithMetadata(data);
        ValidateLength(decrypted.Plaintext.Length);
        var envelope = _serializer.Deserialize(decrypted.Plaintext);
        var replacement = decrypted.RequiresReencryption
            ? _encryptor.Encrypt(decrypted.Plaintext)
            : null;
        if (replacement is not null)
        {
            ValidateLength(replacement.Length);
        }
        return new RotationAwareDeserializationResult<TContract>(envelope, replacement);
    }

    public RotationAwareDeserializationResult<TContract> DeserializeForRead(
        byte[] data,
        ReadOnlySpan<byte> associatedData)
    {
        ArgumentNullException.ThrowIfNull(data);
        ValidateLength(data.Length);
        if (_encryptionDisabled)
        {
            return new RotationAwareDeserializationResult<TContract>(_serializer.Deserialize(data), null);
        }

        var contextual = _encryptor as IContextualEncryptor
            ?? throw new InvalidOperationException(
                $"Encryptor '{_encryptor.GetType().FullName}' does not support authenticated storage contexts.");
        var decrypted = contextual.DecryptWithMetadata(data, associatedData);
        ValidateLength(decrypted.Plaintext.Length);
        var envelope = _serializer.Deserialize(decrypted.Plaintext);
        var replacement = decrypted.RequiresReencryption
            ? contextual.Encrypt(decrypted.Plaintext, associatedData)
            : null;
        if (replacement is not null)
        {
            ValidateLength(replacement.Length);
        }
        return new RotationAwareDeserializationResult<TContract>(envelope, replacement);
    }

    public long ReadRevision(ReadOnlyMemory<byte> payload, ReadOnlySpan<byte> associatedData)
    {
        ValidateLength(payload.Length);
        if (_encryptionDisabled)
        {
            return _serializer.ReadRevision(payload);
        }

        var contextual = _encryptor as IContextualEncryptor
            ?? throw new InvalidOperationException(
                $"Encryptor '{_encryptor.GetType().FullName}' does not support authenticated storage contexts.");
        var plaintext = contextual.Decrypt(payload.Span, associatedData);
        ValidateLength(plaintext.Length);
        return _serializer.ReadRevision(plaintext);
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

        var payload = await BoundedStoredPayload
            .ReadStreamAsync(source, _serializer.MaximumPayloadBytes, cancellationToken)
            .ConfigureAwait(false);
        return Deserialize(payload);
    }

    private void ValidateLength(long length)
        => _ = BoundedStoredPayload.ValidateLength(length, _serializer.MaximumPayloadBytes);
}
