using System.Text.Json;
using TransactionManager.Abstraction.Serialization;

namespace TransactionManager.Serializers
{
    public class JsonObjectSerializer : IObjectSerializer
    {
        public byte[] Serialize<T>(T obj) => JsonSerializer.SerializeToUtf8Bytes(obj);
        public T Deserialize<T>(byte[] data) => JsonSerializer.Deserialize<T>(data);
    }

}
