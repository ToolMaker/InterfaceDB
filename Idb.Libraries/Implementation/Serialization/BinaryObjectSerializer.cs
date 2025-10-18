using System;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;
using MessagePack;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// Binary serializer (MessagePack-backed) that produces compact payloads compatible across contract versions.
/// </summary>
internal sealed class BinaryObjectSerializer<TContract> : IObjectSerializer<TContract>
    where TContract : class, IRepoDto<TContract>
{
    private const string DefaultExtension = ".mpk";
    private readonly MessagePackSerializerOptions _options;

    public BinaryObjectSerializer(MessagePackSerializerOptions? options = null)
    {
        _options = options ?? MessagePackSerializerOptions.Standard;
    }

    public string FileExtension => DefaultExtension;

    public byte[] Serialize(TContract obj)
    {
        if (obj is null)
        {
            throw new ArgumentNullException(nameof(obj));
        }

        return MessagePackSerializer.Serialize(obj, _options);
    }

    public TContract Deserialize(byte[] data)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (data.Length == 0)
        {
            throw new ArgumentException("Serialized payload cannot be empty.", nameof(data));
        }

        return MessagePackSerializer.Deserialize<TContract>(data, _options);
    }
}
