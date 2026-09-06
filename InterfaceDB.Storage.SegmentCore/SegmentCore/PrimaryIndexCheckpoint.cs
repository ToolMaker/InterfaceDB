using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Idb.Storage.SegmentCore.ObjectLog;

namespace Idb.Storage.SegmentCore;

internal sealed record PrimaryIndexCheckpointState(
    long Generation,
    long TransactionWatermark,
    IReadOnlyList<ObjectLogWritePosition> Positions,
    IReadOnlyDictionary<string, StripedObjectLogRecordAddress> Entries,
    long FileBytes)
{
    public bool IsFull { get; init; } = true;

    public int ChainDepth { get; init; }

    public uint? FileChecksum { get; init; }
}

internal sealed record PrimaryIndexCheckpointFingerprint(
    long TransactionWatermark,
    int StripeCount,
    long FileBytes,
    uint FileChecksum);

internal static class PrimaryIndexCheckpoint
{
    private const int FormatVersion = 2;
    private const int LegacyFormatVersion = 1;
    private const int HeaderBytes = 48;
    private const int LegacyHeaderBytes = 40;
    private const int PositionBytes = 32;
    private const int EntryFixedBytes = 36;
    private const int FooterBytes = sizeof(uint);
    private const int StreamBufferBytes = 64 * 1024;
    private const int SequentialReadBufferBytes = 1024 * 1024;
    private const int PageMapHeaderBytes = 72;
    private const int PageMapReferenceBytes = 24;
    private const int PageMapFooterBytes = sizeof(uint);
    private const int PageMapFormatVersion = 1;
    private const int MaximumKeyBytes = 1024 * 1024;
    private const int MaximumEntries = 100_000_000;
    private const string DirectoryName = "checkpoints";
    private static ReadOnlySpan<byte> Magic => "IDBIDX01"u8;
    private static ReadOnlySpan<byte> PageMapMagic => "IDBMAP01"u8;

    public static PrimaryIndexCheckpointState? TryLoadLatest(
        string rootDirectory,
        int expectedStripeCount,
        SegmentCorePrimaryIndexOptions? pagingOptions = null)
    {
        var checkpointDirectory = Path.Combine(Path.GetFullPath(rootDirectory), DirectoryName);
        if (!Directory.Exists(checkpointDirectory))
        {
            return null;
        }

        var candidates = EnumerateAllPublished(checkpointDirectory);
        var byGeneration = candidates.ToDictionary(static item => item.Generation);
        foreach (var candidate in candidates)
        {
            try
            {
                var loaded = LoadComposed(
                    candidate.Generation,
                    byGeneration,
                    new Dictionary<long, PrimaryIndexCheckpointState>(),
                    pagingOptions);
                if (loaded.Generation != candidate.Generation || loaded.Positions.Count != expectedStripeCount)
                {
                    throw new ObjectLogCorruptionException(
                        $"Primary-index checkpoint '{candidate.Path}' does not match its filename or stripe manifest.");
                }

                return loaded;
            }
            catch (ObjectLogCorruptionException)
            {
                // A previously published generation remains a valid recovery point.
            }
        }

        return null;
    }

    public static long GetOldestRetainedTransactionWatermark(
        string rootDirectory,
        int expectedStripeCount,
        PrimaryIndexCheckpointState justWritten,
        int generationsToKeep,
        Dictionary<long, PrimaryIndexCheckpointFingerprint>? validatedRoots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(justWritten);
        var checkpointDirectory = Path.Combine(Path.GetFullPath(rootDirectory), DirectoryName);
        var candidates = EnumerateAllPublished(checkpointDirectory);
        validatedRoots ??= new Dictionary<long, PrimaryIndexCheckpointFingerprint>();
        var publishedFullGenerations = candidates.Where(static candidate => !candidate.IsDelta)
            .Select(static candidate => candidate.Generation).ToHashSet();
        foreach (var generation in validatedRoots.Keys.Where(generation => !publishedFullGenerations.Contains(generation)).ToArray())
        {
            validatedRoots.Remove(generation);
        }

        long? oldestWatermark = null;
        // A valid delta cannot precede its full base's watermark; all of its bases remain
        // published. Validating every retained full root therefore gives a conservative
        // recycle boundary without composing/allocating delta indexes.
        foreach (var candidate in candidates.Where(static candidate => !candidate.IsDelta))
        {
            try
            {
                PrimaryIndexCheckpointFingerprint fingerprint;
                var wasCached = validatedRoots.TryGetValue(candidate.Generation, out var cached);
                if (wasCached
                    && ValidateCheckpointChecksum(candidate.Path, cached!.FileBytes, cached.FileChecksum))
                {
                    fingerprint = cached!;
                }
                else
                {
                    validatedRoots.Remove(candidate.Generation);
                    var loaded = !wasCached && candidate.Generation == justWritten.Generation
                        ? justWritten
                        : Read(candidate.Path, pagingOptions: null);
                    if (loaded.Generation != candidate.Generation
                        || loaded.Positions.Count != expectedStripeCount
                        || loaded.FileChecksum is not uint checksum)
                    {
                        continue;
                    }

                    fingerprint = new PrimaryIndexCheckpointFingerprint(
                        loaded.TransactionWatermark, loaded.Positions.Count, loaded.FileBytes, checksum);
                    validatedRoots[candidate.Generation] = fingerprint;
                }

                if (fingerprint.StripeCount == expectedStripeCount)
                {
                    oldestWatermark = Math.Min(oldestWatermark ?? long.MaxValue, fingerprint.TransactionWatermark);
                }
            }
            catch (ObjectLogCorruptionException)
            {
                // Corrupt generations cannot serve as fallback recovery points.
            }
        }

        if (oldestWatermark is null)
        {
            throw new ObjectLogCorruptionException(
                "No valid retained primary-index checkpoint exists after checkpoint publication.");
        }

        return oldestWatermark.Value;
    }

    public static async ValueTask<PrimaryIndexCheckpointState> WriteOptimizedAsync(
        string rootDirectory,
        IReadOnlyList<ObjectLogWritePosition> positions,
        IReadOnlyDictionary<string, StripedObjectLogRecordAddress>? entries,
        int liveEntryCount,
        IReadOnlyDictionary<string, PrimaryIndexChange> changes,
        long previousGeneration,
        long transactionWatermark,
        int generationsToKeep,
        bool useFull,
        PrimaryIndexCheckpointState? latest,
        SegmentCorePrimaryIndexOptions? pagingOptions = null,
        IDurableFileSystem? durableFileSystem = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        durableFileSystem ??= PlatformDurableFileSystem.Instance;
        var checkpointDirectory = Path.Combine(Path.GetFullPath(rootDirectory), DirectoryName);
        Directory.CreateDirectory(checkpointDirectory);
        var existing = EnumerateAllPublished(checkpointDirectory);
        PrimaryIndexCheckpointState state;
        if (useFull)
        {
            ArgumentNullException.ThrowIfNull(entries);
            state = await WriteAsync(
                rootDirectory,
                positions,
                entries,
                previousGeneration,
                transactionWatermark,
                generationsToKeep,
                pagingOptions,
                durableFileSystem,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (latest is null || latest.Generation != previousGeneration)
            {
                throw new InvalidOperationException(
                    "An incremental checkpoint requires the matching in-memory base checkpoint.");
            }

            var highest = existing.Select(static item => item.Generation).DefaultIfEmpty(0).Max();
            var generation = checked(Math.Max(previousGeneration, highest) + 1);
            var finalPath = Path.Combine(checkpointDirectory, $"checkpoint-{generation:D20}.idbdlt");
            var temporaryPath = $"{finalPath}.creating-{Guid.NewGuid():N}";
            var delta = await PrimaryIndexDeltaCheckpoint.WriteAsync(
                temporaryPath,
                finalPath,
                generation,
                latest!.Generation,
                transactionWatermark,
                positions,
                changes,
                durableFileSystem,
                cancellationToken).ConfigureAwait(false);
            state = Compose(latest, delta);
        }

        PrunePublished(checkpointDirectory, generationsToKeep);
        return state;
    }

    public static bool ShouldWriteFull(
        string rootDirectory,
        int liveEntryCount,
        int changeCount,
        long previousGeneration,
        int generationsToKeep,
        bool forceFull,
        PrimaryIndexCheckpointState? latest)
    {
        if (forceFull
            || generationsToKeep == 1
            || previousGeneration == 0
            || latest is null
            || latest.Generation != previousGeneration
            || latest.ChainDepth >= 1
            || changeCount >= Math.Max(256, Math.Max(1, liveEntryCount / 4)))
        {
            return true;
        }

        var checkpointDirectory = Path.Combine(Path.GetFullPath(rootDirectory), DirectoryName);
        return !Directory.Exists(checkpointDirectory)
            || EnumerateAllPublished(checkpointDirectory).Count(static item => !item.IsDelta)
                < generationsToKeep;
    }

    public static async ValueTask<PrimaryIndexCheckpointState> WriteAsync(
        string rootDirectory,
        IReadOnlyList<ObjectLogWritePosition> positions,
        IReadOnlyDictionary<string, StripedObjectLogRecordAddress> entries,
        long previousGeneration,
        long transactionWatermark,
        int generationsToKeep,
        SegmentCorePrimaryIndexOptions? pagingOptions = null,
        IDurableFileSystem? durableFileSystem = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(entries);
        cancellationToken.ThrowIfCancellationRequested();
        if (positions.Count is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(positions));
        }

        if (entries.Count > MaximumEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(entries));
        }

        if (generationsToKeep is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(generationsToKeep));
        }

        if (transactionWatermark < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transactionWatermark));
        }

        durableFileSystem ??= PlatformDurableFileSystem.Instance;
        var checkpointDirectory = Path.Combine(Path.GetFullPath(rootDirectory), DirectoryName);
        Directory.CreateDirectory(checkpointDirectory);
        var highestPublishedGeneration = EnumerateAllPublished(checkpointDirectory)
            .Select(static candidate => candidate.Generation)
            .DefaultIfEmpty(0)
            .Max();
        var generation = checked(Math.Max(previousGeneration, highestPublishedGeneration) + 1);
        var finalPath = Path.Combine(checkpointDirectory, $"checkpoint-{generation:D20}.idbidx");
        var temporaryPath = $"{finalPath}.creating-{Guid.NewGuid():N}";
        var pageMapPath = PageMapPath(finalPath);
        var pageMapTemporaryPath = $"{pageMapPath}.creating-{Guid.NewGuid():N}";
        var buildPageMap = ShouldUsePagedIndex(pagingOptions, entries.Count);
        var pageReferences = buildPageMap
            ? new List<PagedPrimaryIndexReference>(entries.Count)
            : null;
        var dataOffset = checked((long)HeaderBytes + ((long)positions.Count * PositionBytes));
        long fileBytes;
        uint checkpointChecksum;
        Task? pageMapWriteTask = null;

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             new FileStreamOptions
                             {
                                 Mode = FileMode.CreateNew,
                                 Access = FileAccess.ReadWrite,
                                 Share = FileShare.Read,
                                 BufferSize = StreamBufferBytes,
                                 Options = FileOptions.Asynchronous
                                     | FileOptions.SequentialScan
                             }))
            {
                var writer = new CheckpointWriter(stream);
                WriteHeader(writer, generation, positions.Count, entries.Count, transactionWatermark);
                foreach (var position in positions)
                {
                    writer.WriteInt64(position.SegmentId);
                    writer.WriteInt64(position.NextOffset);
                    writer.WriteInt64(position.LastLsn);
                    writer.WriteUInt32(position.SegmentHeaderChecksum);
                    writer.WriteUInt32(0);
                }

                byte[]? entryBuffer = null;
                try
                {
                    foreach (var pair in entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var keyLength = Encoding.UTF8.GetByteCount(pair.Key);
                        if (keyLength is <= 0 or > MaximumKeyBytes)
                        {
                            throw new ArgumentOutOfRangeException(
                                nameof(entries),
                                "Checkpoint keys must encode to 1..1,048,576 bytes.");
                        }

                        var entryLength = checked(EntryFixedBytes + keyLength);
                        if (entryBuffer is null || entryBuffer.Length < entryLength)
                        {
                            if (entryBuffer is not null)
                            {
                                ArrayPool<byte>.Shared.Return(entryBuffer, clearArray: false);
                            }

                            entryBuffer = ArrayPool<byte>.Shared.Rent(entryLength);
                        }

                        var entry = entryBuffer.AsSpan(0, entryLength);
                        BinaryPrimitives.WriteInt32LittleEndian(entry, keyLength);
                        var keySpan = entry.Slice(sizeof(int), keyLength);
                        _ = Encoding.UTF8.GetBytes(pair.Key, keySpan);
                        var locator = entry[(sizeof(int) + keyLength)..];
                        BinaryPrimitives.WriteInt32LittleEndian(locator, pair.Value.Stripe);
                        BinaryPrimitives.WriteInt64LittleEndian(locator[4..], pair.Value.Address.SegmentId);
                        BinaryPrimitives.WriteInt64LittleEndian(locator[12..], pair.Value.Address.Offset);
                        BinaryPrimitives.WriteInt32LittleEndian(locator[20..], pair.Value.Address.FrameLength);
                        BinaryPrimitives.WriteInt64LittleEndian(locator[24..], pair.Value.Address.Lsn);
                        var entryOffset = stream.Position;
                        writer.Write(entry);
                        if (pageReferences is not null)
                        {
                            pageReferences.Add(new PagedPrimaryIndexReference(
                                PrimaryIndexKeyHash.Compute(keySpan),
                                entryOffset,
                                entryLength,
                                Crc32C.Compute(entry)));
                        }
                    }
                }
                finally
                {
                    if (entryBuffer is not null)
                    {
                        ArrayPool<byte>.Shared.Return(entryBuffer, clearArray: false);
                    }
                }

                checkpointChecksum = writer.WriteChecksumFooter();
                fileBytes = stream.Position;
                if (pageReferences is { Count: >= 100_000 }
                    && Environment.ProcessorCount > 1
                    && ReferenceEquals(durableFileSystem, PlatformDurableFileSystem.Instance))
                {
                    pageMapWriteTask = Task.Run(
                        () => WritePageMapAsync(
                            pageMapTemporaryPath,
                            generation,
                            fileBytes,
                            checkpointChecksum,
                            dataOffset,
                            pageReferences,
                            durableFileSystem,
                            cancellationToken).AsTask(),
                        CancellationToken.None);
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                durableFileSystem.FlushFile(stream.SafeFileHandle);
            }

            if (pageReferences is not null)
            {
                if (pageMapWriteTask is not null)
                {
                    await pageMapWriteTask.ConfigureAwait(false);
                    pageMapWriteTask = null;
                }
                else
                {
                    await WritePageMapAsync(
                        pageMapTemporaryPath,
                        generation,
                        fileBytes,
                        checkpointChecksum,
                        dataOffset,
                        pageReferences,
                        durableFileSystem,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            durableFileSystem.PublishFile(temporaryPath, finalPath);
            if (pageReferences is not null)
            {
                try
                {
                    durableFileSystem.PublishFile(pageMapTemporaryPath, pageMapPath);
                }
                catch
                {
                    // The checkpoint is independently checksummed and remains a valid eager-load recovery root.
                    TryDelete(pageMapTemporaryPath);
                }
            }

            return new PrimaryIndexCheckpointState(
                generation,
                transactionWatermark,
                positions,
                entries,
                fileBytes)
            {
                IsFull = true,
                ChainDepth = 0,
                FileChecksum = checkpointChecksum
            };
        }
        catch
        {
            if (pageMapWriteTask is not null)
            {
                try
                {
                    await pageMapWriteTask.ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the primary checkpoint failure while draining the parallel map writer.
                }
            }

            TryDelete(temporaryPath);
            TryDelete(pageMapTemporaryPath);
            throw;
        }
    }

    private static async ValueTask WritePageMapAsync(
        string temporaryPath,
        long generation,
        long checkpointBytes,
        uint checkpointChecksum,
        long dataOffset,
        List<PagedPrimaryIndexReference> references,
        IDurableFileSystem durableFileSystem,
        CancellationToken cancellationToken)
    {
        references.Sort(static (left, right) =>
        {
            var hashComparison = left.Hash.CompareTo(right.Hash);
            return hashComparison != 0 ? hashComparison : left.Offset.CompareTo(right.Offset);
        });
        await using var stream = new FileStream(
            temporaryPath,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.Read,
                BufferSize = StreamBufferBytes,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
        var writer = new CheckpointWriter(stream);
        writer.Write(PageMapMagic);
        writer.WriteInt32(PageMapFormatVersion);
        writer.WriteInt32(PageMapHeaderBytes);
        writer.WriteInt64(generation);
        writer.WriteInt64(checkpointBytes);
        writer.WriteUInt32(checkpointChecksum);
        writer.WriteInt32(references.Count);
        writer.WriteInt64(dataOffset);
        writer.WriteInt64(checked(checkpointBytes - dataOffset - FooterBytes));
        writer.WriteInt64(DateTimeOffset.UtcNow.UtcTicks);
        writer.WriteInt32(PageMapReferenceBytes);
        writer.WriteUInt32(0);
        Span<byte> encoded = stackalloc byte[PageMapReferenceBytes];
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteUInt64LittleEndian(encoded, reference.Hash);
            BinaryPrimitives.WriteInt64LittleEndian(encoded[8..], reference.Offset);
            BinaryPrimitives.WriteInt32LittleEndian(encoded[16..], reference.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(encoded[20..], reference.Checksum);
            writer.Write(encoded);
        }

        _ = writer.WriteChecksumFooter();
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        durableFileSystem.FlushFile(stream.SafeFileHandle);
    }

    private static void WriteHeader(
        CheckpointWriter writer,
        long generation,
        int stripeCount,
        int entryCount,
        long transactionWatermark)
    {
        writer.Write(Magic);
        writer.WriteInt32(FormatVersion);
        writer.WriteInt32(HeaderBytes);
        writer.WriteInt64(generation);
        writer.WriteInt32(stripeCount);
        writer.WriteInt32(entryCount);
        writer.WriteInt64(DateTimeOffset.UtcNow.UtcTicks);
        writer.WriteInt64(transactionWatermark);
    }

    private static PrimaryIndexCheckpointState Read(
        string path,
        SegmentCorePrimaryIndexOptions? pagingOptions)
    {
        using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = StreamBufferBytes,
                Options = FileOptions.SequentialScan
            });
        if (stream.Length < LegacyHeaderBytes + PositionBytes + FooterBytes)
        {
            throw new ObjectLogCorruptionException($"Primary-index checkpoint '{path}' is incomplete.");
        }

        var reader = new CheckpointReader(stream, stream.Length - FooterBytes, path);
        Span<byte> magic = stackalloc byte[8];
        reader.ReadExactly(magic);
        var version = reader.ReadInt32();
        var headerBytes = reader.ReadInt32();
        if (!magic.SequenceEqual(Magic)
            || (version != FormatVersion && version != LegacyFormatVersion)
            || (version == FormatVersion && headerBytes != HeaderBytes)
            || (version == LegacyFormatVersion && headerBytes != LegacyHeaderBytes))
        {
            throw new ObjectLogCorruptionException($"Primary-index checkpoint '{path}' has an invalid header.");
        }

        var generation = reader.ReadInt64();
        var stripeCount = reader.ReadInt32();
        var entryCount = reader.ReadInt32();
        _ = reader.ReadInt64();
        var transactionWatermark = version == FormatVersion ? reader.ReadInt64() : 0;
        if (generation <= 0
            || stripeCount is < 1 or > 64
            || entryCount is < 0 or > MaximumEntries
            || transactionWatermark < 0)
        {
            throw new ObjectLogCorruptionException($"Primary-index checkpoint '{path}' has invalid counts.");
        }

        var minimumLength = checked(
            (long)headerBytes
            + ((long)stripeCount * PositionBytes)
            + ((long)entryCount * EntryFixedBytes)
            + FooterBytes);
        if (minimumLength > stream.Length)
        {
            throw new ObjectLogCorruptionException($"Primary-index checkpoint '{path}' is truncated.");
        }

        var positions = new ObjectLogWritePosition[stripeCount];
        for (var stripe = 0; stripe < positions.Length; stripe++)
        {
            positions[stripe] = new ObjectLogWritePosition(
                reader.ReadInt64(),
                reader.ReadInt64(),
                reader.ReadInt64(),
                reader.ReadUInt32());
            _ = reader.ReadUInt32();
            if (positions[stripe].SegmentId <= 0
                || positions[stripe].NextOffset < ObjectLogFormat.SegmentHeaderSize
                || positions[stripe].LastLsn < 0)
            {
                throw new ObjectLogCorruptionException(
                    $"Primary-index checkpoint '{path}' has an invalid stripe replay position.");
            }
        }

        var dataOffset = stream.Position;
        if (ShouldUsePagedIndex(pagingOptions, entryCount)
            && TryOpenPagedIndex(
                path,
                generation,
                stripeCount,
                entryCount,
                dataOffset,
                stream.Length,
                out var pagedEntries))
        {
            return new PrimaryIndexCheckpointState(
                generation,
                transactionWatermark,
                positions,
                pagedEntries,
                stream.Length)
            {
                IsFull = true,
                ChainDepth = 0
            };
        }

        var entries = new Dictionary<string, StripedObjectLogRecordAddress>(entryCount, StringComparer.Ordinal);
        byte[]? keyBuffer = null;
        try
        {
            for (var index = 0; index < entryCount; index++)
            {
                var keyLength = reader.ReadInt32();
                if (keyLength is <= 0 or > MaximumKeyBytes || keyLength > reader.RemainingDataBytes)
                {
                    throw new ObjectLogCorruptionException(
                        $"Primary-index checkpoint '{path}' has an invalid key length.");
                }

                if (keyBuffer is null || keyBuffer.Length < keyLength)
                {
                    if (keyBuffer is not null)
                    {
                        ArrayPool<byte>.Shared.Return(keyBuffer, clearArray: false);
                    }

                    keyBuffer = ArrayPool<byte>.Shared.Rent(keyLength);
                }

                var keySpan = keyBuffer.AsSpan(0, keyLength);
                reader.ReadExactly(keySpan);
                var key = Encoding.UTF8.GetString(keySpan);
                var stripe = reader.ReadInt32();
                var address = new ObjectLogRecordAddress(
                    reader.ReadInt64(),
                    reader.ReadInt64(),
                    reader.ReadInt32(),
                    reader.ReadInt64());
                if (stripe < 0
                    || stripe >= stripeCount
                    || address.SegmentId is 0 or long.MinValue
                    || address.Offset < ObjectLogFormat.SegmentHeaderSize
                    || address.FrameLength < ObjectLogFormat.FrameHeaderSize + ObjectLogFormat.FrameFooterSize
                    || address.FrameLength > ObjectLogFormat.MaximumFrameBytes
                    || address.Lsn <= 0
                    || !entries.TryAdd(key, new StripedObjectLogRecordAddress(stripe, address)))
                {
                    throw new ObjectLogCorruptionException(
                        $"Primary-index checkpoint '{path}' has an invalid or duplicate locator.");
                }
            }
        }
        finally
        {
            if (keyBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(keyBuffer, clearArray: false);
            }
        }

        reader.RequireDataEnd();
        var expectedChecksum = reader.ReadFooterChecksum();
        var actualChecksum = Crc32C.Complete(reader.CrcState);
        if (expectedChecksum != actualChecksum)
        {
            throw new ObjectLogCorruptionException($"Primary-index checkpoint '{path}' has an invalid checksum.");
        }

        return new PrimaryIndexCheckpointState(
            generation,
            transactionWatermark,
            positions,
            entries,
            stream.Length)
        {
            IsFull = true,
            ChainDepth = 0,
            FileChecksum = expectedChecksum
        };
    }

    private static bool ShouldUsePagedIndex(
        SegmentCorePrimaryIndexOptions? options,
        int entryCount) => options?.Mode switch
        {
            SegmentCorePrimaryIndexMode.Paged => true,
            SegmentCorePrimaryIndexMode.Automatic => entryCount >= options.AutomaticPagingMinimumEntries,
            _ => false
        };

    private static bool TryOpenPagedIndex(
        string checkpointPath,
        long generation,
        int stripeCount,
        int entryCount,
        long dataOffset,
        long checkpointLength,
        out PagedPrimaryIndex pagedIndex)
    {
        pagedIndex = null!;
        Task<bool>? checkpointValidation = null;
        var mapPath = PageMapPath(checkpointPath);
        if (!File.Exists(mapPath))
        {
            return false;
        }

        try
        {
            var expectedLength = checked((long)
                PageMapHeaderBytes
                + ((long)entryCount * PageMapReferenceBytes)
                + PageMapFooterBytes);
            using var mapStream = new FileStream(
                mapPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.SequentialScan);
            if (mapStream.Length != expectedLength)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[PageMapHeaderBytes];
            if (!TryReadExactly(mapStream, header)
                || !header[..PageMapMagic.Length].SequenceEqual(PageMapMagic)
                || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != PageMapFormatVersion
                || BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != PageMapHeaderBytes
                || BinaryPrimitives.ReadInt64LittleEndian(header[16..]) != generation
                || BinaryPrimitives.ReadInt64LittleEndian(header[24..]) != checkpointLength
                || BinaryPrimitives.ReadInt32LittleEndian(header[36..]) != entryCount
                || BinaryPrimitives.ReadInt64LittleEndian(header[40..]) != dataOffset
                || BinaryPrimitives.ReadInt64LittleEndian(header[48..])
                    != checkpointLength - dataOffset - FooterBytes
                || BinaryPrimitives.ReadInt32LittleEndian(header[64..]) != PageMapReferenceBytes)
            {
                return false;
            }

            var checkpointChecksum = BinaryPrimitives.ReadUInt32LittleEndian(header[32..]);
            if (Environment.ProcessorCount > 1
                && checked(checkpointLength + expectedLength) >= 4L * 1024 * 1024)
            {
                checkpointValidation = Task.Run(
                    () => ValidateCheckpointChecksum(checkpointPath, checkpointLength, checkpointChecksum));
            }
            else if (!ValidateCheckpointChecksum(checkpointPath, checkpointLength, checkpointChecksum))
            {
                return false;
            }

            var hashes = GC.AllocateUninitializedArray<ulong>(entryCount);
            var offsets = GC.AllocateUninitializedArray<long>(entryCount);
            var lengths = GC.AllocateUninitializedArray<int>(entryCount);
            var checksums = GC.AllocateUninitializedArray<uint>(entryCount);
            var priorHash = 0UL;
            long referencedBytes = 0;
            var minimumOffset = entryCount == 0 ? dataOffset : long.MaxValue;
            var maximumEnd = dataOffset;
            var crc = Crc32C.Append(Crc32C.InitialState, header);
            const int referencesPerBuffer = SequentialReadBufferBytes / PageMapReferenceBytes;
            var referenceBuffer = ArrayPool<byte>.Shared.Rent(referencesPerBuffer * PageMapReferenceBytes);
            try
            {
                var firstReference = 0;
                while (firstReference < entryCount)
                {
                    var batchCount = Math.Min(referencesPerBuffer, entryCount - firstReference);
                    var batch = referenceBuffer.AsSpan(0, batchCount * PageMapReferenceBytes);
                    if (!TryReadExactly(mapStream, batch))
                    {
                        return false;
                    }

                    crc = Crc32C.Append(crc, batch);
                    for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
                    {
                        var index = firstReference + batchIndex;
                        var encoded = batch.Slice(
                            batchIndex * PageMapReferenceBytes,
                            PageMapReferenceBytes);
                        var hash = BinaryPrimitives.ReadUInt64LittleEndian(encoded);
                        var offset = BinaryPrimitives.ReadInt64LittleEndian(encoded[8..]);
                        var length = BinaryPrimitives.ReadInt32LittleEndian(encoded[16..]);
                        var checksum = BinaryPrimitives.ReadUInt32LittleEndian(encoded[20..]);
                        if ((index > 0 && hash < priorHash)
                            || length < EntryFixedBytes + 1
                            || length > EntryFixedBytes + MaximumKeyBytes
                            || offset < dataOffset
                            || offset + length > checkpointLength - FooterBytes)
                        {
                            return false;
                        }

                        priorHash = hash;
                        referencedBytes = checked(referencedBytes + length);
                        minimumOffset = Math.Min(minimumOffset, offset);
                        maximumEnd = Math.Max(maximumEnd, checked(offset + length));
                        hashes[index] = hash;
                        offsets[index] = offset;
                        lengths[index] = length;
                        checksums[index] = checksum;
                    }

                    firstReference += batchCount;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(referenceBuffer, clearArray: false);
            }

            Span<byte> footer = stackalloc byte[PageMapFooterBytes];
            if (!TryReadExactly(mapStream, footer)
                || mapStream.Position != mapStream.Length
                || BinaryPrimitives.ReadUInt32LittleEndian(footer) != Crc32C.Complete(crc))
            {
                return false;
            }

            if (referencedBytes != checkpointLength - dataOffset - FooterBytes
                || minimumOffset != dataOffset
                || maximumEnd != checkpointLength - FooterBytes)
            {
                return false;
            }

            if (checkpointValidation is not null
                && !checkpointValidation.GetAwaiter().GetResult())
            {
                return false;
            }

            checkpointValidation = null;

            pagedIndex = new PagedPrimaryIndex(
                checkpointPath,
                dataOffset,
                stripeCount,
                hashes,
                offsets,
                lengths,
                checksums);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            return false;
        }
        finally
        {
            if (checkpointValidation is not null)
            {
                try
                {
                    _ = checkpointValidation.GetAwaiter().GetResult();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The caller will fall back to an eagerly loaded or older checkpoint.
                }
            }
        }
    }

    private static bool TryReadExactly(Stream stream, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = stream.Read(destination[total..]);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    private static bool ValidateCheckpointChecksum(
        string path,
        long expectedLength,
        uint expectedChecksum)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.SequentialScan);
        if (stream.Length != expectedLength || stream.Length < FooterBytes)
        {
            return false;
        }

        var remaining = stream.Length - FooterBytes;
        var crc = Crc32C.InitialState;
        var buffer = ArrayPool<byte>.Shared.Rent(SequentialReadBufferBytes);
        try
        {
            while (remaining > 0)
            {
                var requested = (int)Math.Min(buffer.Length, remaining);
                var read = stream.Read(buffer, 0, requested);
                if (read == 0)
                {
                    return false;
                }

                crc = Crc32C.Append(crc, buffer.AsSpan(0, read));
                remaining -= read;
            }

            Span<byte> footer = stackalloc byte[FooterBytes];
            stream.ReadExactly(footer);
            return BinaryPrimitives.ReadUInt32LittleEndian(footer) == expectedChecksum
                && Crc32C.Complete(crc) == expectedChecksum;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    private static PrimaryIndexCheckpointState? TryLoadGeneration(
        long generation,
        IReadOnlyList<PublishedCheckpoint> candidates)
    {
        try
        {
            return LoadComposed(
                generation,
                candidates.ToDictionary(static item => item.Generation),
                new Dictionary<long, PrimaryIndexCheckpointState>(),
                pagingOptions: null);
        }
        catch (ObjectLogCorruptionException)
        {
            return null;
        }
    }

    private static PrimaryIndexCheckpointState LoadComposed(
        long generation,
        IReadOnlyDictionary<long, PublishedCheckpoint> candidates,
        Dictionary<long, PrimaryIndexCheckpointState> cache,
        SegmentCorePrimaryIndexOptions? pagingOptions)
    {
        if (cache.TryGetValue(generation, out var cached))
        {
            return cached;
        }

        if (!candidates.TryGetValue(generation, out var candidate))
        {
            throw new ObjectLogCorruptionException($"Checkpoint generation {generation:N0} is missing.");
        }

        PrimaryIndexCheckpointState state;
        if (!candidate.IsDelta)
        {
            state = Read(candidate.Path, pagingOptions);
        }
        else
        {
            var delta = PrimaryIndexDeltaCheckpoint.Read(candidate.Path);
            if (delta.Generation != generation || delta.BaseGeneration >= generation)
            {
                throw new ObjectLogCorruptionException($"Primary-index delta '{candidate.Path}' has an invalid chain.");
            }

            var basis = LoadComposed(delta.BaseGeneration, candidates, cache, pagingOptions);
            state = Compose(basis, delta);
        }

        cache[generation] = state;
        return state;
    }

    private static PrimaryIndexCheckpointState Compose(
        PrimaryIndexCheckpointState basis,
        PrimaryIndexDeltaFileState delta)
    {
        if (delta.TransactionWatermark < basis.TransactionWatermark
            || delta.Positions.Count != basis.Positions.Count)
        {
            throw new ObjectLogCorruptionException(
                $"Primary-index delta generation {delta.Generation:N0} regresses its recovery boundary.");
        }

        for (var stripe = 0; stripe < delta.Positions.Count; stripe++)
        {
            if (delta.Positions[stripe].LastLsn < basis.Positions[stripe].LastLsn)
            {
                throw new ObjectLogCorruptionException(
                    $"Primary-index delta generation {delta.Generation:N0} regresses stripe {stripe}.");
            }
        }

        var entries = new PrimaryIndexOverlay(basis.Entries, delta.Changes);

        return new PrimaryIndexCheckpointState(
            delta.Generation,
            delta.TransactionWatermark,
            delta.Positions,
            entries,
            delta.FileBytes)
        {
            IsFull = false,
            ChainDepth = checked(basis.ChainDepth + 1)
        };
    }

    private static IReadOnlyList<(string Path, long Generation)> EnumeratePublished(string checkpointDirectory) =>
        Directory
            .EnumerateFiles(checkpointDirectory, "checkpoint-*.idbidx", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Generation: TryParseGeneration(path)))
            .Where(static candidate => candidate.Generation > 0)
            .OrderByDescending(static candidate => candidate.Generation)
            .ToArray();

    private static IReadOnlyList<PublishedCheckpoint> EnumerateAllPublished(string checkpointDirectory) =>
        Directory
            .EnumerateFiles(checkpointDirectory, "checkpoint-*.*", SearchOption.TopDirectoryOnly)
            .Where(static path => path.EndsWith(".idbidx", StringComparison.Ordinal)
                                  || path.EndsWith(".idbdlt", StringComparison.Ordinal))
            .Select(path => new PublishedCheckpoint(
                path,
                TryParseGeneration(path),
                path.EndsWith(".idbdlt", StringComparison.Ordinal)))
            .Where(static candidate => candidate.Generation > 0)
            .OrderByDescending(static candidate => candidate.Generation)
            .ToArray();

    private static void PruneOldGenerations(string checkpointDirectory, int generationsToKeep)
    {
        foreach (var candidate in EnumeratePublished(checkpointDirectory).Skip(generationsToKeep))
        {
            TryDelete(candidate.Path);
            TryDelete(PageMapPath(candidate.Path));
        }
    }

    private static void PrunePublished(string checkpointDirectory, int generationsToKeep)
    {
        var candidates = EnumerateAllPublished(checkpointDirectory);
        var byGeneration = candidates.ToDictionary(static item => item.Generation);
        var keep = new HashSet<long>();
        var recoveryRoots = new HashSet<long>();
        var retained = 0;
        foreach (var candidate in candidates)
        {
            if (retained == generationsToKeep)
            {
                break;
            }

            try
            {
                var current = candidate;
                while (true)
                {
                    keep.Add(current.Generation);
                    if (!current.IsDelta)
                    {
                        recoveryRoots.Add(current.Generation);
                        break;
                    }

                    var delta = PrimaryIndexDeltaCheckpoint.Read(current.Path);
                    if (!byGeneration.TryGetValue(delta.BaseGeneration, out current))
                    {
                        throw new ObjectLogCorruptionException("Incremental checkpoint base is missing.");
                    }
                }

                retained++;
            }
            catch (ObjectLogCorruptionException)
            {
                // Do not prune a chain merely because a newer candidate is corrupt.
            }
        }

        foreach (var candidate in candidates.Where(static item => !item.IsDelta))
        {
            if (recoveryRoots.Add(candidate.Generation))
            {
                keep.Add(candidate.Generation);
            }

            if (recoveryRoots.Count >= generationsToKeep)
            {
                break;
            }
        }

        foreach (var candidate in candidates.Where(candidate => !keep.Contains(candidate.Generation)))
        {
            TryDelete(candidate.Path);
            TryDelete(PageMapPath(candidate.Path));
        }
    }

    private static long TryParseGeneration(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("checkpoint-", StringComparison.Ordinal)
            && long.TryParse(name.AsSpan("checkpoint-".Length), out var generation)
            ? generation
            : 0;
    }

    private readonly record struct PublishedCheckpoint(string Path, long Generation, bool IsDelta);

    private sealed class PrimaryIndexOverlay :
        IReadOnlyDictionary<string, StripedObjectLogRecordAddress>,
        IPagedPrimaryIndexView,
        IDisposable
    {
        private readonly IReadOnlyDictionary<string, StripedObjectLogRecordAddress> _basis;
        private readonly IReadOnlyDictionary<string, PrimaryIndexChange> _changes;
        private readonly HashSet<string> _insertedKeys = new(StringComparer.Ordinal);
        private readonly int _count;

        public PrimaryIndexOverlay(
            IReadOnlyDictionary<string, StripedObjectLogRecordAddress> basis,
            IReadOnlyDictionary<string, PrimaryIndexChange> changes)
        {
            _basis = basis;
            _changes = changes;
            var count = basis.Count;
            foreach (var pair in changes)
            {
                var existed = basis.ContainsKey(pair.Key);
                if (pair.Value.IsDelete)
                {
                    if (existed)
                    {
                        count--;
                    }
                }
                else if (!existed)
                {
                    count++;
                    _insertedKeys.Add(pair.Key);
                }
            }

            _count = count;
        }

        public int Count => _count;

        public bool IsPaged => _basis is IPagedPrimaryIndexView { IsPaged: true };

        public IEnumerable<string> Keys => this.Select(static pair => pair.Key);

        public IEnumerable<StripedObjectLogRecordAddress> Values =>
            this.Select(static pair => pair.Value);

        public StripedObjectLogRecordAddress this[string key] =>
            TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();

        public bool ContainsKey(string key) => TryGetValue(key, out _);

        public bool TryGetValue(string key, out StripedObjectLogRecordAddress value)
        {
            if (_changes.TryGetValue(key, out var change))
            {
                value = change.Address;
                return !change.IsDelete;
            }

            return _basis.TryGetValue(key, out value);
        }

        public IEnumerator<KeyValuePair<string, StripedObjectLogRecordAddress>> GetEnumerator()
        {
            foreach (var pair in _basis)
            {
                if (!_changes.TryGetValue(pair.Key, out var change))
                {
                    yield return pair;
                }
                else if (!change.IsDelete)
                {
                    yield return new KeyValuePair<string, StripedObjectLogRecordAddress>(
                        pair.Key,
                        change.Address);
                }
            }

            foreach (var pair in _changes)
            {
                if (!pair.Value.IsDelete && _insertedKeys.Contains(pair.Key))
                {
                    yield return new KeyValuePair<string, StripedObjectLogRecordAddress>(
                        pair.Key,
                        pair.Value.Address);
                }
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose()
        {
            if (_basis is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static string PageMapPath(string checkpointPath) =>
        Path.ChangeExtension(checkpointPath, ".idbmap");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Crash-only temporary files and old generations are safe to retain.
        }
    }

    private sealed class CheckpointWriter
    {
        private readonly Stream _stream;
        private uint _crc = Crc32C.InitialState;

        public CheckpointWriter(Stream stream)
        {
            _stream = stream;
        }

        public void Write(ReadOnlySpan<byte> value)
        {
            _crc = Crc32C.Append(_crc, value);
            _stream.Write(value);
        }

        public void WriteInt32(int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            Write(bytes);
        }

        public void WriteUInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            Write(bytes);
        }

        public void WriteInt64(long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            Write(bytes);
        }

        public uint WriteChecksumFooter()
        {
            var checksum = Crc32C.Complete(_crc);
            Span<byte> bytes = stackalloc byte[FooterBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, checksum);
            _stream.Write(bytes);
            return checksum;
        }
    }

    private sealed class CheckpointReader
    {
        private readonly Stream _stream;
        private readonly long _dataLength;
        private readonly string _path;

        public CheckpointReader(Stream stream, long dataLength, string path)
        {
            _stream = stream;
            _dataLength = dataLength;
            _path = path;
        }

        public uint CrcState { get; private set; } = Crc32C.InitialState;

        public long RemainingDataBytes => _dataLength - _stream.Position;

        public void ReadExactly(Span<byte> destination)
        {
            if (destination.Length > RemainingDataBytes)
            {
                throw new ObjectLogCorruptionException($"Primary-index checkpoint '{_path}' is truncated.");
            }

            var total = 0;
            while (total < destination.Length)
            {
                var read = _stream.Read(destination[total..]);
                if (read == 0)
                {
                    throw new ObjectLogCorruptionException($"Primary-index checkpoint '{_path}' is truncated.");
                }

                CrcState = Crc32C.Append(CrcState, destination.Slice(total, read));
                total += read;
            }
        }

        public int ReadInt32()
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            ReadExactly(bytes);
            return BinaryPrimitives.ReadInt32LittleEndian(bytes);
        }

        public uint ReadUInt32()
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            ReadExactly(bytes);
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }

        public long ReadInt64()
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            ReadExactly(bytes);
            return BinaryPrimitives.ReadInt64LittleEndian(bytes);
        }

        public void RequireDataEnd()
        {
            if (_stream.Position != _dataLength)
            {
                throw new ObjectLogCorruptionException(
                    $"Primary-index checkpoint '{_path}' has trailing data.");
            }
        }

        public uint ReadFooterChecksum()
        {
            Span<byte> bytes = stackalloc byte[FooterBytes];
            var total = 0;
            while (total < bytes.Length)
            {
                var read = _stream.Read(bytes[total..]);
                if (read == 0)
                {
                    throw new ObjectLogCorruptionException($"Primary-index checkpoint '{_path}' has no checksum footer.");
                }

                total += read;
            }

            if (_stream.Position != _stream.Length)
            {
                throw new ObjectLogCorruptionException($"Primary-index checkpoint '{_path}' has trailing footer data.");
            }

            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }
    }
}
