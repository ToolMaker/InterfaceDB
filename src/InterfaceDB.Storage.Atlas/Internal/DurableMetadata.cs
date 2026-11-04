using System.Security.Cryptography;
using Idb.Storage.SegmentCore.ObjectLog;

namespace Idb.Storage.AtlasCore.Internal;

internal static class DurableMetadata
{
    public const int HashBytes = 32;

    public static byte[] AppendHash(ReadOnlySpan<byte> payload)
    {
        var result = GC.AllocateUninitializedArray<byte>(checked(payload.Length + HashBytes));
        payload.CopyTo(result);
        SHA256.HashData(payload, result.AsSpan(payload.Length));
        return result;
    }

    public static ReadOnlyMemory<byte> ValidateAndGetPayload(byte[] bytes, string description)
    {
        if (bytes.Length < HashBytes)
        {
            throw new InvalidDataException($"{description} is truncated.");
        }

        var payloadLength = bytes.Length - HashBytes;
        Span<byte> actual = stackalloc byte[HashBytes];
        SHA256.HashData(bytes.AsSpan(0, payloadLength), actual);
        if (!CryptographicOperations.FixedTimeEquals(actual, bytes.AsSpan(payloadLength)))
        {
            throw new InvalidDataException($"{description} checksum is invalid.");
        }

        return bytes.AsMemory(0, payloadLength);
    }

    public static async ValueTask PublishNewAsync(
        string finalPath,
        ReadOnlyMemory<byte> contents,
        IDurableFileSystem durableFileSystem,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(finalPath))!;
        Directory.CreateDirectory(directory);
        var temporaryPath = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.Read,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            durableFileSystem.PublishFile(temporaryPath, finalPath);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
