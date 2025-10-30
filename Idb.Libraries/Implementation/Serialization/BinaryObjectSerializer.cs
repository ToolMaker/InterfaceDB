using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;
using MessagePack;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// Binary serializer (MessagePack-backed) that produces compact payloads compatible across contract versions.
/// </summary>
public sealed class BinaryObjectSerializer<TContract, TLocation> : IObjectSerializer<TContract, TLocation>
    where TContract : class, IRepoDto<TContract, TLocation>
{
    private const string DefaultExtension = ".mpk";
    private readonly MessagePackSerializerOptions _options;

    public BinaryObjectSerializer(MessagePackSerializerOptions? options = null)
    {
        _options = options ?? MessagePackSerializerOptions.Standard;
    }

    //protected MessagePackSerializerOptions SerializerOptions { get; }
    //private static MessagePackSerializerOptions ResolveSerializerOptions(IObjectSerializer<TContract, string> serializer)
    //{
    //    if (serializer is BinaryObjectSerializer<TContract, string> binarySerializer)
    //    {
    //        var field = typeof(BinaryObjectSerializer<TContract, string>)
    //            .GetField("_options", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    //        if (field?.GetValue(binarySerializer) is MessagePackSerializerOptions options)
    //        {
    //            return options;
    //        }
    //    }

    //    return MessagePackSerializerOptions.Standard;
    //}

    public byte[] Serialize(IObjectEnvelope<TContract, TLocation> obj)
    {
        if (obj is null)
        {
            throw new ArgumentNullException(nameof(obj));
        }

        return MessagePackSerializer.Serialize(obj, _options);
    }

    public IObjectEnvelope<TContract, TLocation> Deserialize(byte[] data)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (data.Length == 0)
        {
            throw new ArgumentException("Serialized payload cannot be empty.", nameof(data));
        }

        return MessagePackSerializer.Deserialize<IObjectEnvelope<TContract, TLocation>>(data, _options);
    }

    public ValueTask SerializeAsync(
        IObjectEnvelope<TContract, TLocation> envalope,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        return new ValueTask(MessagePackSerializer.SerializeAsync(destination, envalope, _options, cancellationToken));
    }

    public ValueTask<IObjectEnvelope<TContract, TLocation>> DeserializeAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        return MessagePackSerializer.DeserializeAsync<IObjectEnvelope<TContract, TLocation>>(source, _options, cancellationToken);
    }
}
