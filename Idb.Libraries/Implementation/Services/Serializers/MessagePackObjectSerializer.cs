using System;
using MessagePack;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Services.Serializers;

public sealed class MessagePackObjectSerializer : IObjectSerializer
{
    private static readonly MessagePackSerializerOptions Options = MessagePackConfiguration.Options;

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


