using System.Buffers.Binary;
using System.Text;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore;

public sealed record SegmentCoreStorageFormatInfo(
    int EngineMajor,
    int EngineMinor,
    int ObjectLogVersion,
    int PrimaryCheckpointVersion,
    int PrimaryDeltaVersion,
    int PrimaryPageMapVersion,
    int MutationWalVersion,
    int CommitJournalVersion,
    string RouterId);

/// <summary>
/// Stable compatibility identifiers for SegmentCore's durable artifacts.
/// </summary>
public static class SegmentCoreStorageFormat
{
    public const int PublicApiMajor = 1;
    public const int PublicApiMinor = 0;
    public const int EngineMajor = 1;
    public const int EngineMinor = 0;
    public const int ObjectLogVersion = 1;
    public const int PrimaryCheckpointVersion = 2;
    public const int PrimaryDeltaVersion = 1;
    public const int PrimaryPageMapVersion = 1;
    public const int MutationWalVersion = 1;
    public const int CommitJournalVersion = 1;
    public const string RouterId = "fnv1a-utf16le-hash-stripes-v1";
    public const string EnvelopeSerializationId = "InterfaceDB.BinaryEnvelope.IDB4";

    public static SegmentCoreStorageFormatInfo Current { get; } = new(
        EngineMajor,
        EngineMinor,
        ObjectLogVersion,
        PrimaryCheckpointVersion,
        PrimaryDeltaVersion,
        PrimaryPageMapVersion,
        MutationWalVersion,
        CommitJournalVersion,
        RouterId);
}

public sealed class SegmentCoreCompatibilityException : IOException
{
    public SegmentCoreCompatibilityException(string message)
        : base(message)
    {
    }
}

internal static class SegmentCoreStorageFormatManifest
{
    private const int ManifestVersion = 1;
    private const int ManifestBytes = 128;
    private const int MaximumRouterBytes = 48;
    private const string FileName = "storage-format.manifest";
    private static ReadOnlySpan<byte> Magic => "IDBSFM01"u8;

    public static SegmentCoreStorageFormatInfo ReadOrCreate(
        string rootDirectory,
        IDurableFileSystem durableFileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(durableFileSystem);
        var root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, FileName);
        if (File.Exists(path))
        {
            return Read(path);
        }

        var bytes = Create();
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

            return Read(path);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static byte[] Create()
    {
        var bytes = new byte[ManifestBytes];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), ManifestVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), ManifestBytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), SegmentCoreStorageFormat.EngineMajor);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), SegmentCoreStorageFormat.EngineMinor);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), SegmentCoreStorageFormat.ObjectLogVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), SegmentCoreStorageFormat.PrimaryCheckpointVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32), SegmentCoreStorageFormat.PrimaryDeltaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(36), SegmentCoreStorageFormat.PrimaryPageMapVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), SegmentCoreStorageFormat.MutationWalVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(44), SegmentCoreStorageFormat.CommitJournalVersion);
        var routerLength = Encoding.UTF8.GetBytes(
            SegmentCoreStorageFormat.RouterId,
            bytes.AsSpan(60, MaximumRouterBytes));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56), routerLength);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(108), DateTimeOffset.UtcNow.UtcTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(124), Crc32C.Compute(bytes.AsSpan(0, 124)));
        return bytes;
    }

    private static SegmentCoreStorageFormatInfo Read(string path)
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
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]) != ManifestVersion
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]) != ManifestBytes
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[124..]) != Crc32C.Compute(bytes[..124]))
        {
            throw new SegmentCoreCompatibilityException(
                $"SegmentCore storage-format manifest '{path}' is incomplete or corrupt.");
        }

        var routerLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[56..]);
        if (routerLength is <= 0 or > MaximumRouterBytes)
        {
            throw new SegmentCoreCompatibilityException(
                $"SegmentCore storage-format manifest '{path}' has an invalid router identity.");
        }

        var info = new SegmentCoreStorageFormatInfo(
            BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[28..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[32..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[36..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[40..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[44..]),
            Encoding.UTF8.GetString(bytes.Slice(60, routerLength)));
        EnsureSupported(info, path);
        return info;
    }

    internal static void EnsureSupported(SegmentCoreStorageFormatInfo info, string context)
    {
        ArgumentNullException.ThrowIfNull(info);
        var current = SegmentCoreStorageFormat.Current;
        if (info.EngineMajor != current.EngineMajor
            || info.EngineMinor > current.EngineMinor
            || info.ObjectLogVersion != current.ObjectLogVersion
            || info.PrimaryCheckpointVersion > current.PrimaryCheckpointVersion
            || info.PrimaryDeltaVersion > current.PrimaryDeltaVersion
            || info.PrimaryPageMapVersion > current.PrimaryPageMapVersion
            || info.MutationWalVersion > current.MutationWalVersion
            || info.CommitJournalVersion > current.CommitJournalVersion
            || !string.Equals(info.RouterId, current.RouterId, StringComparison.Ordinal))
        {
            throw new SegmentCoreCompatibilityException(
                $"SegmentCore artifact '{context}' requires an unsupported storage format or router. "
                + $"Store={info}; runtime={current}.");
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
            // Crash-only temporary file.
        }
    }
}
