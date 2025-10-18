using System;
using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization
{
    /// <summary>
    /// Transforms contract instances to and from their serialized binary representation.
    /// </summary>
    public interface IObjectSerializer<TContract>
        where TContract : class
    {
        /// <summary>
        /// Default file extension produced by this serializer (e.g., ".mpk").
        /// </summary>
        string FileExtension { get; }

        /// <summary>
        /// Serializes a contract instance into bytes.
        /// </summary>
        /// <param name="obj">Instance to serialize.</param>
        /// <returns>Serialized bytes.</returns>
        byte[] Serialize(TContract obj);

        /// <summary>
        /// Deserializes bytes into a contract instance.
        /// </summary>
        /// <param name="data">Serialized data buffer.</param>
        /// <returns>Materialized instance.</returns>
        TContract Deserialize(byte[] data);
    }
}

