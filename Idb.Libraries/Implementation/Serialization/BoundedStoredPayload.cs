using System.Buffers;

namespace Idb.Libraries.Implementation.Serialization;

internal static class BoundedStoredPayload
{
    internal static int ValidateLength(long length, int maximumPayloadBytes)
    {
        if (maximumPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        }

        if (length < 0 || length > maximumPayloadBytes)
        {
            throw CreateLimitException(maximumPayloadBytes);
        }

        return checked((int)length);
    }

    internal static async ValueTask<byte[]> ReadFileAsync(
        string path,
        int maximumPayloadBytes,
        CancellationToken cancellationToken = default)
    {
        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadStreamAsync(source, maximumPayloadBytes, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<byte[]> ReadStreamAsync(
        Stream source,
        int maximumPayloadBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maximumPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        }

        if (source.CanSeek)
        {
            var length = ValidateLength(source.Length - source.Position, maximumPayloadBytes);
            var payload = new byte[length];
            await source.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

            var probe = new byte[1];
            if (await source.ReadAsync(probe, cancellationToken).ConfigureAwait(false) != 0)
            {
                throw CreateLimitException(maximumPayloadBytes);
            }

            return payload;
        }

        var bufferLength = maximumPayloadBytes >= 81920
            ? 81920
            : maximumPayloadBytes + 1;
        var rented = ArrayPool<byte>.Shared.Rent(bufferLength);
        try
        {
            using var destination = new MemoryStream(Math.Min(maximumPayloadBytes, 81920));
            var total = 0;
            while (true)
            {
                var remaining = maximumPayloadBytes - total;
                var requested = Math.Min(rented.Length, remaining == 0 ? 1 : remaining);
                var read = await source
                    .ReadAsync(rented.AsMemory(0, requested), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return destination.ToArray();
                }

                if (read > remaining)
                {
                    throw CreateLimitException(maximumPayloadBytes);
                }

                await destination.WriteAsync(rented.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    internal static InvalidDataException CreateLimitException(int maximumPayloadBytes)
        => new($"Stored envelope exceeds the configured {maximumPayloadBytes}-byte limit.");
}
