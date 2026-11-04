using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;
using Idb.Storage.SegmentCore.ObjectLog;

namespace Idb.Storage.AtlasCore.Internal;

internal sealed record AtlasGroupCheckpointState(
    long Generation,
    long SegmentId,
    long Offset,
    long LastLsn,
    long TransactionWatermark,
    Dictionary<string, AtlasGroupAddress> Entries,
    long Bytes);

internal static class AtlasGroupCheckpoint
{
    private const int Version = 2;
    private static ReadOnlySpan<byte> Magic => "IDBAGI02"u8;

    public static int CountCandidates(string rootDirectory)
    {
        var directory = Path.Combine(rootDirectory, "checkpoints");
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "checkpoint-*.idbagi", SearchOption.TopDirectoryOnly).Count()
            : 0;
    }

    public static AtlasGroupCheckpointState? TryLoadLatest(
        string rootDirectory,
        int maximumMetadataBytes)
    {
        var directory = Path.Combine(rootDirectory, "checkpoints");
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var candidates = Directory
            .EnumerateFiles(directory, "checkpoint-*.idbagi", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Parsed: TryParseGeneration(path, out var generation), Generation: generation))
            .Where(static item => item.Parsed)
            .OrderByDescending(static item => item.Generation)
            .ToArray();
        InvalidDataException? newestFailure = null;
        foreach (var candidate in candidates)
        {
            try
            {
                return Read(candidate.Path, candidate.Generation, maximumMetadataBytes);
            }
            catch (InvalidDataException exception) when (newestFailure is null)
            {
                newestFailure = exception;
            }
        }

        if (newestFailure is not null)
        {
            throw newestFailure;
        }

        return null;
    }

    public static async ValueTask<AtlasGroupCheckpointState> WriteAsync(
        string rootDirectory,
        AtlasGroupCheckpointState capture,
        int generationsToKeep,
        IDurableFileSystem durableFileSystem,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(rootDirectory, "checkpoints");
        var existed = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        if (!existed)
        {
            durableFileSystem.FlushDirectory(rootDirectory);
        }

        // Encode once into the final image, including space for its hash. The format
        // remains byte-for-byte identical; keys no longer need individual byte arrays.
        var ordered = capture.Entries.ToArray();
        Array.Sort(ordered, static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        var payloadLength = checked(Magic.Length + sizeof(int) + 5 * sizeof(long) + sizeof(int));
        foreach (var pair in ordered)
        {
            payloadLength = checked(payloadLength + 41 + Encoding.UTF8.GetByteCount(pair.Key));
        }

        var contents = GC.AllocateUninitializedArray<byte>(checked(payloadLength + DurableMetadata.HashBytes));
        using var stream = new MemoryStream(contents, 0, payloadLength, writable: true, publiclyVisible: true);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(capture.Generation);
            writer.Write(capture.SegmentId);
            writer.Write(capture.Offset);
            writer.Write(capture.LastLsn);
            writer.Write(capture.TransactionWatermark);
            writer.Write(capture.Entries.Count);
            foreach (var pair in ordered)
            {
                var keyLength = Encoding.UTF8.GetByteCount(pair.Key);
                writer.Write(keyLength);
                _ = Encoding.UTF8.GetBytes(pair.Key, contents.AsSpan(checked((int)stream.Position), keyLength));
                stream.Position += keyLength;
                writer.Write((byte)pair.Value.Kind);
                writer.Write(pair.Value.SegmentId);
                writer.Write(pair.Value.Offset);
                writer.Write(pair.Value.FrameLength);
                writer.Write(pair.Value.Lsn);
                writer.Write(pair.Value.PlacementGeneration);
            }
        }

        SHA256.HashData(contents.AsSpan(0, payloadLength), contents.AsSpan(payloadLength));
        var path = Path.Combine(directory, $"checkpoint-{capture.Generation:D20}.idbagi");
        await DurableMetadata.PublishNewAsync(path, contents, durableFileSystem, cancellationToken)
            .ConfigureAwait(false);
        Cleanup(directory, capture.Generation, generationsToKeep, durableFileSystem);
        return capture with { Bytes = contents.LongLength };
    }

    private static AtlasGroupCheckpointState Read(
        string path,
        long expectedGeneration,
        int maximumMetadataBytes)
    {
        var length = new FileInfo(path).Length;
        if (length <= DurableMetadata.HashBytes || length > maximumMetadataBytes)
        {
            throw new InvalidDataException(
                $"AtlasCore group checkpoint '{path}' exceeds the configured metadata limit.");
        }
        var bytes = File.ReadAllBytes(path);
        var payload = DurableMetadata.ValidateAndGetPayload(
            bytes,
            $"AtlasCore group checkpoint generation {expectedGeneration:N0}");
        var source = payload.Span;
        var cursor = 0;
        if (source.Length < Magic.Length || !source[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("AtlasCore group checkpoint format is unsupported.");
        }

        cursor += Magic.Length;
        if (ReadInt32(source, ref cursor) != Version)
        {
            throw new InvalidDataException("AtlasCore group checkpoint format is unsupported.");
        }

        var generation = ReadInt64(source, ref cursor);
        var segmentId = ReadInt64(source, ref cursor);
        var offset = ReadInt64(source, ref cursor);
        var lastLsn = ReadInt64(source, ref cursor);
        var transactionWatermark = ReadInt64(source, ref cursor);
        var count = ReadInt32(source, ref cursor);
        if (generation != expectedGeneration
            || segmentId <= 0
            || offset < AtlasGroupLogFormat.SegmentHeaderBytes
            || lastLsn < 0
            || transactionWatermark < 0
            || count < 0
            || count > 100_000_000
            || count > (source.Length - cursor) / 42)
        {
            throw new InvalidDataException("AtlasCore group checkpoint metadata is invalid.");
        }

        var entries = new Dictionary<string, AtlasGroupAddress>(count, StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var keyLength = ReadInt32(source, ref cursor);
            if (keyLength <= 0 || keyLength > 1024 * 1024)
            {
                throw new InvalidDataException("AtlasCore group checkpoint contains an invalid key length.");
            }

            if (source.Length - cursor < keyLength)
            {
                throw new InvalidDataException("AtlasCore group checkpoint is truncated.");
            }

            var key = Encoding.UTF8.GetString(source.Slice(cursor, keyLength));
            cursor += keyLength;
            var address = new AtlasGroupAddress(
                (AtlasGroupRecordKind)ReadByte(source, ref cursor),
                ReadInt64(source, ref cursor),
                ReadInt64(source, ref cursor),
                ReadInt32(source, ref cursor),
                ReadInt64(source, ref cursor),
                ReadInt64(source, ref cursor));
            if (!Enum.IsDefined(address.Kind)
                || address.SegmentId <= 0
                || address.Offset < AtlasGroupLogFormat.SegmentHeaderBytes
                || address.FrameLength < AtlasGroupLogFormat.FrameHeaderBytes + AtlasGroupLogFormat.FrameFooterBytes
                || address.FrameLength > AtlasGroupLogFormat.MaximumFrameBytes
                || address.Lsn <= 0
                || address.Lsn > lastLsn
                || address.PlacementGeneration <= 0
                || !entries.TryAdd(key, address))
            {
                throw new InvalidDataException("AtlasCore group checkpoint contains an invalid locator.");
            }
        }

        if (cursor != source.Length)
        {
            throw new InvalidDataException("AtlasCore group checkpoint has trailing bytes.");
        }

        return new AtlasGroupCheckpointState(
            generation,
            segmentId,
            offset,
            lastLsn,
            transactionWatermark,
            entries,
            bytes.LongLength);
    }

    private static byte ReadByte(ReadOnlySpan<byte> source, ref int cursor)
    {
        EnsureAvailable(source, cursor, 1);
        return source[cursor++];
    }

    private static int ReadInt32(ReadOnlySpan<byte> source, ref int cursor)
    {
        EnsureAvailable(source, cursor, sizeof(int));
        var value = BinaryPrimitives.ReadInt32LittleEndian(source[cursor..]);
        cursor += sizeof(int);
        return value;
    }

    private static long ReadInt64(ReadOnlySpan<byte> source, ref int cursor)
    {
        EnsureAvailable(source, cursor, sizeof(long));
        var value = BinaryPrimitives.ReadInt64LittleEndian(source[cursor..]);
        cursor += sizeof(long);
        return value;
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> source, int cursor, int required)
    {
        if (cursor < 0 || source.Length - cursor < required)
        {
            throw new InvalidDataException("AtlasCore group checkpoint is truncated.");
        }
    }

    private static void Cleanup(
        string directory,
        long latestGeneration,
        int generationsToKeep,
        IDurableFileSystem durableFileSystem)
    {
        var minimum = latestGeneration - generationsToKeep + 1;
        foreach (var path in Directory.EnumerateFiles(directory, "checkpoint-*.idbagi", SearchOption.TopDirectoryOnly))
        {
            if (TryParseGeneration(path, out var generation) && generation < minimum)
            {
                DurableMetadata.TryDelete(path);
            }
        }

        durableFileSystem.FlushDirectory(directory);
    }

    private static bool TryParseGeneration(string path, out long generation)
    {
        generation = 0;
        var name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("checkpoint-", StringComparison.Ordinal)
            && long.TryParse(name.AsSpan("checkpoint-".Length), out generation)
            && generation > 0;
    }
}
