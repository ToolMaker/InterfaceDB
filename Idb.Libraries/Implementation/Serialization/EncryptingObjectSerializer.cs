using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// Applies encryption after envelope serialization and decryption before deserialization.
/// </summary>
public sealed class EncryptingObjectSerializer<TContract> : IObjectSerializer<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly BinaryObjectSerializer<TContract> _serializer;
    private readonly IEncryptor _encryptor;

    public EncryptingObjectSerializer(
        BinaryObjectSerializer<TContract> serializer,
        IEncryptor encryptor)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
    }

    public byte[] Serialize(IObjectEnvelope<TContract> envalope)
        => _encryptor.Encrypt(_serializer.Serialize(envalope));

    public IObjectEnvelope<TContract> Deserialize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return _serializer.Deserialize(_encryptor.Decrypt(data));
    }

    public async ValueTask SerializeAsync(
        IObjectEnvelope<TContract> envalope,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var payload = Serialize(envalope);
        await destination.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IObjectEnvelope<TContract>> DeserializeAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var memory = new MemoryStream();
        await source.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return Deserialize(memory.ToArray());
    }
}
