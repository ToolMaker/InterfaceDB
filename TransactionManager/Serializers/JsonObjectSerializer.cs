using System;
using System.Text.Json;
using TransactionManager.Abstraction.Serialization;

namespace TransactionManager.Serializers
{
    public class JsonObjectSerializer : IObjectSerializer
    {
        public string FileExtension => ".json";

        public byte[] Serialize<T>(T obj) => Serialize(obj!, typeof(T));

        public byte[] Serialize(object value, Type valueType)
            => JsonSerializer.SerializeToUtf8Bytes(value, valueType);

        public T Deserialize<T>(byte[] data) => (T)Deserialize(data, typeof(T));

        public object Deserialize(byte[] data, Type valueType)
            => JsonSerializer.Deserialize(data, valueType)!;
    }
}
