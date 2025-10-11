using MessagePack;
using MessagePack.Resolvers;
using RepoTcpHost.Configuration;

namespace RepoTcpHost.Serialization;

public interface ISerializer
{
    ReadOnlyMemory<byte> Pack<T>(T value);
    T Unpack<T>(ReadOnlySpan<byte> span);
    MessagePackSerializerOptions Options { get; }
}

public sealed class MsgPackSerializer : ISerializer
{
    public MsgPackSerializer(SerializationConfig config)
    {
        var resolver = CompositeResolver.Create(
            StandardResolverAllowPrivate.Instance,
            ContractlessStandardResolverAllowPrivate.Instance,
            StandardResolver.Instance);

        var options = MessagePackSerializerOptions.Standard
            .WithResolver(resolver)
            .WithSecurity(MessagePackSecurity.UntrustedData);

        if (string.Equals(config.Compression, "LZ4", StringComparison.OrdinalIgnoreCase))
        {
            options = options.WithCompression(MessagePackCompression.Lz4BlockArray);
        }

        Options = options;
    }

    public MessagePackSerializerOptions Options { get; }

    public ReadOnlyMemory<byte> Pack<T>(T value)
    {
        var bytes = MessagePackSerializer.Serialize(value, Options);
        return new ReadOnlyMemory<byte>(bytes);
    }

    public T Unpack<T>(ReadOnlySpan<byte> span)
        => MessagePackSerializer.Deserialize<T>(span.ToArray(), Options);
}

