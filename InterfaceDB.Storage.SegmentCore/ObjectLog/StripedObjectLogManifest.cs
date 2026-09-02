using System.Buffers.Binary;
using Idb.Storage.SegmentCore;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore.ObjectLog;

internal readonly record struct StripedObjectLogManifestInfo(
    int StripeCount,
    SegmentCoreHashAlgorithm HashAlgorithm,
    long HashSeed);

internal static class StripedObjectLogManifest
{
    private const int LegacyManifestBytes = 32;
    private const int CurrentManifestBytes = 64;
    private const int LegacyFormatVersion = 1;
    private const int CurrentFormatVersion = 2;
    private const string FileName = "striped-log.manifest";
    private static ReadOnlySpan<byte> Magic => "IDBSTR01"u8;

    public static StripedObjectLogManifestInfo ReadOrCreate(
        string rootDirectory,
        StripedObjectLogProbeOptions requestedOptions,
        IDurableFileSystem durableFileSystem)
    {
        ArgumentNullException.ThrowIfNull(requestedOptions);
        Directory.CreateDirectory(rootDirectory);
        var path = Path.Combine(rootDirectory, FileName);
        if (File.Exists(path))
        {
            var existing = Read(path);
            RequireMatchingOptions(path, existing, requestedOptions);
            return existing;
        }

        var bytes = Create(requestedOptions);
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
            RequireMatchingOptions(path, published, requestedOptions);
            return published;
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public static StripedObjectLogManifestInfo ReadRequired(string rootDirectory)
    {
        var path = Path.Combine(Path.GetFullPath(rootDirectory), FileName);
        if (!File.Exists(path))
        {
            throw new ObjectLogCorruptionException(
                $"Striped object-log manifest '{path}' is missing.");
        }

        return Read(path);
    }

    private static byte[] Create(StripedObjectLogProbeOptions options)
    {
        if (options.HashAlgorithm == SegmentCoreHashAlgorithm.Fnv1a32
            && options.HashSeed == 0)
        {
            return CreateLegacy(options.StripeCount);
        }

        var bytes = new byte[CurrentManifestBytes];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), CurrentFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), CurrentManifestBytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), options.StripeCount);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), (int)options.HashAlgorithm);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), options.HashSeed);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), DateTimeOffset.UtcNow.UtcTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), Crc32C.Compute(bytes.AsSpan(0, 60)));
        return bytes;
    }

    private static byte[] CreateLegacy(int stripeCount)
    {
        var bytes = new byte[LegacyManifestBytes];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), LegacyFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), stripeCount);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), DateTimeOffset.UtcNow.UtcTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), Crc32C.Compute(bytes.AsSpan(0, 28)));
        return bytes;
    }

    private static StripedObjectLogManifestInfo Read(string path)
    {
        var length = new FileInfo(path).Length;
        if (length is not LegacyManifestBytes and not CurrentManifestBytes)
        {
            throw Corrupt(path);
        }

        Span<byte> bytes = stackalloc byte[CurrentManifestBytes];
        using SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.RandomAccess);
        var manifestBytes = bytes[..(int)length];
        var read = RandomAccess.Read(handle, manifestBytes, 0);
        if (read != manifestBytes.Length || !manifestBytes[..Magic.Length].SequenceEqual(Magic))
        {
            throw Corrupt(path);
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(manifestBytes[8..]);
        if (version == LegacyFormatVersion
            && length == LegacyManifestBytes
            && BinaryPrimitives.ReadUInt32LittleEndian(manifestBytes[28..]) == Crc32C.Compute(manifestBytes[..28]))
        {
            var legacyStripeCount = BinaryPrimitives.ReadInt32LittleEndian(manifestBytes[12..]);
            ValidateStripeCount(path, legacyStripeCount);
            return new StripedObjectLogManifestInfo(
                legacyStripeCount,
                SegmentCoreHashAlgorithm.Fnv1a32,
                0);
        }

        if (version != CurrentFormatVersion
            || length != CurrentManifestBytes
            || BinaryPrimitives.ReadInt32LittleEndian(manifestBytes[12..]) != CurrentManifestBytes
            || BinaryPrimitives.ReadUInt32LittleEndian(manifestBytes[60..]) != Crc32C.Compute(manifestBytes[..60]))
        {
            throw Corrupt(path);
        }

        var stripeCount = BinaryPrimitives.ReadInt32LittleEndian(manifestBytes[16..]);
        ValidateStripeCount(path, stripeCount);
        var algorithm = (SegmentCoreHashAlgorithm)BinaryPrimitives.ReadInt32LittleEndian(manifestBytes[20..]);
        if (!Enum.IsDefined(algorithm))
        {
            throw new ObjectLogCorruptionException(
                $"Striped object-log manifest '{path}' contains unsupported hash algorithm {(int)algorithm}.");
        }

        return new StripedObjectLogManifestInfo(
            stripeCount,
            algorithm,
            BinaryPrimitives.ReadInt64LittleEndian(manifestBytes[24..]));
    }

    private static void ValidateStripeCount(string path, int stripeCount)
    {
        if (stripeCount is < 1 or > 64 || !System.Numerics.BitOperations.IsPow2((uint)stripeCount))
        {
            throw new ObjectLogCorruptionException(
                $"Striped object-log manifest '{path}' contains invalid stripe count {stripeCount}.");
        }
    }

    private static void RequireMatchingOptions(
        string path,
        StripedObjectLogManifestInfo actual,
        StripedObjectLogProbeOptions requested)
    {
        if (actual.StripeCount != requested.StripeCount
            || actual.HashAlgorithm != requested.HashAlgorithm
            || actual.HashSeed != requested.HashSeed)
        {
            throw new InvalidOperationException(
                $"Striped object log '{path}' was created with {actual.StripeCount} stripes, "
                + $"{actual.HashAlgorithm}, and seed {actual.HashSeed}; it cannot be reopened with "
                + $"{requested.StripeCount} stripes, {requested.HashAlgorithm}, and seed {requested.HashSeed}.");
        }
    }

    private static ObjectLogCorruptionException Corrupt(string path) =>
        new($"Striped object-log manifest '{path}' is incomplete, corrupt, or unsupported.");

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
