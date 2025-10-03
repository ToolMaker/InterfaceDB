using System;
using Idb.Libraries.Abstractions.Encryption;
using Idb.Libraries.Abstractions.Serialization;

namespace Idb.Libraries.Implementation.Serializers
{
    public sealed class EncryptedObjectSerializer : IObjectSerializer
    {
        private readonly IObjectSerializer _innerSerializer;
        private readonly IEncryptor _encryptor;

        public EncryptedObjectSerializer(IObjectSerializer innerSerializer, IEncryptor encryptor)
        {
            _innerSerializer = innerSerializer;
            _encryptor = encryptor;
        }

        public string FileExtension => ".enc";

        public byte[] Serialize<T>(T obj)
            => Serialize(obj!, typeof(T));

        public byte[] Serialize(object value, Type valueType)
        {
            var plain = _innerSerializer.Serialize(value, valueType);
            return _encryptor.Encrypt(plain);
        }

        public T Deserialize<T>(byte[] data)
            => (T)Deserialize(data, typeof(T));

        public object Deserialize(byte[] data, Type valueType)
        {
            var plain = _encryptor.Decrypt(data);
            return _innerSerializer.Deserialize(plain, valueType);
        }
    }
}

