namespace TransactionManager.Serializers
{
    using global::TransactionManager.Abstraction.Serialization;
    using MessagePack;
    using MessagePack.Resolvers;

    namespace TransactionManager.Serialization
    {

        public sealed class MessagePackObjectSerializer : IObjectSerializer
        {
            // Build a composite resolver once and re-use it everywhere.
            // Order matters: the first resolver that can handle the type wins.
            private static readonly IFormatterResolver _resolver =
                CompositeResolver.Create(
                    StandardResolver.Instance,               // honors [MessagePackObject], [Key], etc.
                    ContractlessStandardResolver.Instance    // falls back to property-name matching
                );

            // Pin options so every call uses the exact same setup
            private static readonly MessagePackSerializerOptions _options =
                MessagePackSerializerOptions.Standard
                                             .WithResolver(_resolver)
                                             .WithCompression(MessagePackCompression.Lz4Block) // optional perf boost
                                             .WithSecurity(MessagePackSecurity.UntrustedData); // recommended for deserializing external data

            public byte[] Serialize<T>(T obj)
                => MessagePackSerializer.Serialize(obj, _options);

            public T Deserialize<T>(byte[] data)
                => MessagePackSerializer.Deserialize<T>(data, _options);
        }
    }

}
