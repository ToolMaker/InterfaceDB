using MessagePack;
using MessagePack.Resolvers;

namespace Idb.Libraries.Implementation.Serialization;

internal static class MessagePackConfiguration
{
    private static readonly IFormatterResolver Resolver =
        CompositeResolver.Create(
            StandardResolver.Instance,
            ContractlessStandardResolver.Instance);

    public static MessagePackSerializerOptions Options { get; } =
        MessagePackSerializerOptions.Standard
                                     .WithResolver(Resolver)
                                     .WithCompression(MessagePackCompression.Lz4Block)
                                     .WithSecurity(MessagePackSecurity.UntrustedData);
}
