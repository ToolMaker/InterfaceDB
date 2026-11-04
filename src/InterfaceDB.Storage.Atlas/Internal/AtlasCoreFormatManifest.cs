using System.Buffers.Binary;
using Idb.Storage.SegmentCore.ObjectLog;

namespace Idb.Storage.AtlasCore.Internal;

internal static class AtlasCoreFormatManifest
{
    public static async ValueTask ReadOrCreateAsync(string root, AtlasCoreOptions options,
        IDurableFileSystem fileSystem, CancellationToken token)
    {
        var path = Path.Combine(root, "storage-format.idbatlas");
        var expected = new byte[24];
        "IDBATHYB"u8.CopyTo(expected);
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(12), options.Partitioning.InitialPrefixBytes);
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(16), options.Partitioning.MaximumPrefixBytes);
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(20), (int)options.TransactionProtocol);
        if (!File.Exists(path))
        {
            if (Directory.EnumerateFileSystemEntries(root).Any(p => Path.GetFileName(p) != "writer.lock"))
                throw new InvalidDataException("Hybrid stores require an empty directory or their original format manifest.");
            await DurableMetadata.PublishNewAsync(path, DurableMetadata.AppendHash(expected), fileSystem, token).ConfigureAwait(false);
            return;
        }
        if (new FileInfo(path).Length != expected.Length + DurableMetadata.HashBytes)
            throw new InvalidDataException("Not an Atlas hybrid format. Legacy variants require explicit migration.");
        var stored = DurableMetadata.ValidateAndGetPayload(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false), "hybrid format");
        if (!stored.Span.SequenceEqual(expected))
            throw new InvalidDataException("Atlas hybrid prefix encoding/depth or transaction format does not match.");
    }
}
