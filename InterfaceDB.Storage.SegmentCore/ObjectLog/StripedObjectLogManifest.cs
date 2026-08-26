using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore.ObjectLog;

internal static class StripedObjectLogManifest
{
    private const int ManifestBytes = 32;
    private const int FormatVersion = 1;
    private const string FileName = "striped-log.manifest";
    private static ReadOnlySpan<byte> Magic => "IDBSTR01"u8;

    public static int ReadOrCreate(
        string rootDirectory,
        int requestedStripeCount,
        IDurableFileSystem durableFileSystem)
    {
        Directory.CreateDirectory(rootDirectory);
        var path = Path.Combine(rootDirectory, FileName);
        if (File.Exists(path))
        {
            var existing = Read(path);
            RequireMatchingCount(path, existing, requestedStripeCount);
            return existing;
        }

        var bytes = Create(requestedStripeCount);
        var temporaryPath = $"{path}.creating-{Guid.NewGuid():N}";
        try
        {
            using (var handle = File.OpenHandle(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.Read,
                       FileOptions.WriteThrough | FileOptions.RandomAccess))
            {
                RandomAccess.Write(handle, bytes, 0);
                durableFileSystem.FlushFile(handle);
            }

            try
            {
                durableFileSystem.PublishFile(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                File.Delete(temporaryPath);
            }

            var published = Read(path);
            RequireMatchingCount(path, published, requestedStripeCount);
            return published;
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public static int ReadRequired(string rootDirectory)
    {
        var path = Path.Combine(Path.GetFullPath(rootDirectory), FileName);
        if (!File.Exists(path))
        {
            throw new ObjectLogCorruptionException(
                $"Striped object-log manifest '{path}' is missing.");
        }

        return Read(path);
    }

    private static byte[] Create(int stripeCount)
    {
        var bytes = new byte[ManifestBytes];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), stripeCount);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), DateTimeOffset.UtcNow.UtcTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), Crc32C.Compute(bytes.AsSpan(0, 28)));
        return bytes;
    }

    private static int Read(string path)
    {
        Span<byte> bytes = stackalloc byte[ManifestBytes];
        using SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.RandomAccess);
        var read = RandomAccess.Read(handle, bytes, 0);
        if (read != ManifestBytes
            || !bytes[..Magic.Length].SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]) != FormatVersion
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]) != Crc32C.Compute(bytes[..28]))
        {
            throw new ObjectLogCorruptionException(
                $"Striped object-log manifest '{path}' is incomplete or corrupt.");
        }

        var stripeCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
        if (stripeCount is < 1 or > 64 || !System.Numerics.BitOperations.IsPow2((uint)stripeCount))
        {
            throw new ObjectLogCorruptionException(
                $"Striped object-log manifest '{path}' contains invalid stripe count {stripeCount}.");
        }

        return stripeCount;
    }

    private static void RequireMatchingCount(string path, int actual, int requested)
    {
        if (actual != requested)
        {
            throw new InvalidOperationException(
                $"Striped object log '{path}' was created with {actual} stripes and cannot be reopened with {requested}.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A crash-only temporary is outside the manifest recovery name.
        }
    }
}
