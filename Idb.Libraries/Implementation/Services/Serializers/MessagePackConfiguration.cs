using MessagePack;
using MessagePack.Resolvers;

namespace Idb.Libraries.Implementation.Services.Serializers;

internal static class MessagePackConfiguration
{
    public static MessagePackSerializerOptions Options { get; } =
        MessagePackSerializerOptions.Standard
            .WithResolver(CompositeResolver.Create(StandardResolver.Instance, ContractlessStandardResolver.Instance))
            .WithCompression(MessagePackCompression.Lz4BlockArray)
            .WithSecurity(MessagePackSecurity.UntrustedData);
}
