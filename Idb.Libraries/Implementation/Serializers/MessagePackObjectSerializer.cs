using System;
using MessagePack;
using MessagePack.Resolvers;
using Idb.Libraries.Abstractions.Serialization;

namespace Idb.Libraries.Implementation.Serializers
{
    public sealed class MessagePackObjectSerializer : IObjectSerializer
    {
        private static readonly IFormatterResolver Resolver =
            CompositeResolver.Create(
                StandardResolver.Instance,
                ContractlessStandardResolver.Instance);

        private static readonly MessagePackSerializerOptions Options =
            MessagePackSerializerOptions.Standard
                                         .WithResolver(Resolver)
                                         .WithCompression(MessagePackCompression.Lz4Block)
                                         .WithSecurity(MessagePackSecurity.UntrustedData);

        public string FileExtension => ".mpk";

        public byte[] Serialize<T>(T obj)
            => Serialize(obj!, typeof(T));

        public byte[] Serialize(object value, Type valueType)
            => MessagePackSerializer.Serialize(valueType, value, Options);

        public T Deserialize<T>(byte[] data)
            => (T)Deserialize(data, typeof(T));

        public object Deserialize(byte[] data, Type valueType)
            => MessagePackSerializer.Deserialize(valueType, data, Options)!;
    }
}
