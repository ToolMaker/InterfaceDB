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
        where TContract : class, IRepoDto<TContract>
    {

        /// <summary>
        /// Serializes a contract instance into bytes.
        /// </summary>
        /// <param name="envalope">Instance to serialize.</param>
        /// <returns>Serialized bytes.</returns>
        byte[] Serialize(IObjectEnvelope<TContract> envalope);

        /// <summary>
        /// Deserializes bytes into a contract instance.
        /// </summary>
        /// <param name="data">Serialized data buffer.</param>
        /// <returns>Materialized instance.</returns>
        IObjectEnvelope<TContract> Deserialize(byte[] data);

        /// <summary>
        /// Serializes a contract instance directly to a destination stream.
        /// Default implementation falls back to <see cref="Serialize(TContract)"/>.
        /// </summary>
        ValueTask SerializeAsync(
            IObjectEnvelope<TContract> envalope,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            var payload = Serialize(envalope);
            return destination.WriteAsync(payload.AsMemory(), cancellationToken);
        }

        ValueTask<IObjectEnvelope<TContract>> DeserializeAsync(
            Stream source,
            CancellationToken cancellationToken = default)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            return DeserializeBufferedAsync(source, cancellationToken);

            async ValueTask<IObjectEnvelope<TContract>> DeserializeBufferedAsync(Stream reader, CancellationToken token)
            {
                using var memory = new MemoryStream();
                await reader.CopyToAsync(memory, token).ConfigureAwait(false);
                return Deserialize(memory.ToArray());
            }
        }
    }
}

