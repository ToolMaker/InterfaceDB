using System.Buffers.Binary;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore;

internal static class TransactionProtocolManifest
{
    private const int ManifestBytes = 32;
    private const int FormatVersion = 1;
    private const string FileName = "transaction-protocol.manifest";
    private static ReadOnlySpan<byte> Magic => "IDBTRP01"u8;

    public static SegmentCoreTransactionProtocol ReadOrCreate(
        string rootDirectory,
        SegmentCoreTransactionProtocol requested,
        IDurableFileSystem durableFileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(durableFileSystem);
        var fullRoot = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(fullRoot);
        var path = Path.Combine(fullRoot, FileName);
        if (File.Exists(path))
        {
            var actual = Read(path);
            RequireMatchingProtocol(path, actual, requested);
            return actual;
        }

        RequireCompatibleLegacyFiles(fullRoot, requested);
        var bytes = new byte[ManifestBytes];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), (int)requested);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), DateTimeOffset.UtcNow.UtcTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), Crc32C.Compute(bytes.AsSpan(0, 28)));
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
            RequireMatchingProtocol(path, published, requested);
            return published;
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static SegmentCoreTransactionProtocol Read(string path)
    {
        Span<byte> bytes = stackalloc byte[ManifestBytes];
        using SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.RandomAccess);
        var read = 0;
        while (read < ManifestBytes)
        {
            var count = RandomAccess.Read(handle, bytes[read..], read);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        if (read != ManifestBytes)
        {
            throw new ObjectLogCorruptionException(
                $"Transaction protocol manifest '{path}' is incomplete or corrupt.");
        }

        var protocol = (SegmentCoreTransactionProtocol)BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
        if (!bytes[..Magic.Length].SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]) != FormatVersion
            || !Enum.IsDefined(protocol)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]) != Crc32C.Compute(bytes[..28]))
        {
            throw new ObjectLogCorruptionException(
                $"Transaction protocol manifest '{path}' is incomplete or corrupt.");
        }

        return protocol;
    }

    private static void RequireCompatibleLegacyFiles(
        string rootDirectory,
        SegmentCoreTransactionProtocol requested)
    {
        var participantJournal = Path.Combine(rootDirectory, "transactions", "commit-journal.idbtxn");
        var mutationWal = Path.Combine(rootDirectory, "transactions", "mutation-wal.idbwal");
        if (requested != SegmentCoreTransactionProtocol.ParticipantCommit
            && File.Exists(participantJournal)
            && new FileInfo(participantJournal).Length > 0)
        {
            throw new InvalidOperationException(
                "This SegmentCore store already contains participant-commit transactions and cannot change protocols.");
        }

        if (requested != SegmentCoreTransactionProtocol.MutationWriteAheadLog
            && File.Exists(mutationWal)
            && new FileInfo(mutationWal).Length > 0)
        {
            throw new InvalidOperationException(
                "This SegmentCore store already contains mutation-WAL transactions and cannot change protocols.");
        }
    }

    private static void RequireMatchingProtocol(
        string path,
        SegmentCoreTransactionProtocol actual,
        SegmentCoreTransactionProtocol requested)
    {
        if (actual != requested)
        {
            throw new InvalidOperationException(
                $"SegmentCore store '{path}' uses transaction protocol {actual} and cannot reopen as {requested}.");
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
