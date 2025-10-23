using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

        /// <summary>
        /// Serializes a contract instance directly to a destination stream.
        /// Default implementation falls back to <see cref="Serialize(TContract)"/>.
        /// </summary>
        ValueTask SerializeAsync(
            TContract obj,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            var payload = Serialize(obj);
            return destination.WriteAsync(payload.AsMemory(), cancellationToken);
        }

        /// <summary>
        /// Materializes a contract instance from an input stream.
        /// Default implementation buffers via <see cref="Deserialize(byte[])"/>.
        /// </summary>
        ValueTask<TContract> DeserializeAsync(
            Stream source,
            CancellationToken cancellationToken = default)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            return DeserializeBufferedAsync(source, cancellationToken);

            async ValueTask<TContract> DeserializeBufferedAsync(Stream reader, CancellationToken token)
            {
                using var memory = new MemoryStream();
                await reader.CopyToAsync(memory, token).ConfigureAwait(false);
                return Deserialize(memory.ToArray());
            }
        }
    }
}

