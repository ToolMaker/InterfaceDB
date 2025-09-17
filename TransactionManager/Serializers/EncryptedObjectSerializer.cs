using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TransactionManager.Abstraction.Encryption;
using TransactionManager.Abstraction.Serialization;

namespace TransactionManager.Serializers
{
    public sealed class EncryptedObjectSerializer : IObjectSerializer
    {
        private readonly IObjectSerializer _innerSerializer;
        private readonly IEncryptor _encryptor;

        public EncryptedObjectSerializer(
            IObjectSerializer innerSerializer,
            IEncryptor encryptor)
        {
            _innerSerializer = innerSerializer;
            _encryptor = encryptor;
        }

        public byte[] Serialize<T>(T obj)
        {
            var plain = _innerSerializer.Serialize(obj);
            return _encryptor.Encrypt(plain);
        }

        public T Deserialize<T>(byte[] data)
        {
            var plain = _encryptor.Decrypt(data);
            return _innerSerializer.Deserialize<T>(plain);
        }
    }
}

