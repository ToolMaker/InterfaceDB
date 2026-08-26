using System.Buffers.Binary;
using System.Security.Cryptography;
using Idb.Storage.SegmentCore;
using Idb.Storage.SegmentCore.ObjectLog;

internal static class SegmentCorePowerLossQualificationRunner
{
    private const int LedgerDataBytes = 48;
    private const int LedgerBytes = LedgerDataBytes + 32;
    private static ReadOnlySpan<byte> LedgerMagic => "IDBPLQ01"u8;

    public static async Task RunWriterAsync(string[] args)
    {
        var root = RequiredRoot(args);
        var batchSize = ParseInt(args, "--batch-size", 32, 1, 10_000);
        var payloadBytes = ParseInt(args, "--payload-bytes", 256, 16, 1024 * 1024);
        var maximumBatches = ParseInt(args, "--batches", 0, 0, 100_000_000);
        var ledger = ReadLatestLedger(root);
        if (ledger is null && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new InvalidOperationException(
                "The qualification root is not empty and has no valid ledger. Use a new dedicated directory.");
        }

        var state = ledger ?? new PowerLossLedger(0, -1, payloadBytes, batchSize);
        if (state.PayloadBytes != payloadBytes || state.BatchSize != batchSize)
        {
            throw new InvalidOperationException("Writer options do not match the retained qualification ledger.");
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += OnCancel;
        try
        {
            await using var store = await SegmentCoreStore.OpenAsync(root, Options()).ConfigureAwait(false);
            Console.WriteLine("SegmentCore physical power-loss writer is active.");
            Console.WriteLine($"Root: {root}");
            Console.WriteLine("After acknowledged batches are reported, remove system power without a graceful shutdown.");
            var next = state.HighestAcknowledgedOrdinal + 1;
            var generation = state.Generation;
            var completedBatches = 0;
            while (!cancellation.IsCancellationRequested
                   && (maximumBatches == 0 || completedBatches < maximumBatches))
            {
                await using var transaction = await store.BeginTransactionAsync(cancellation.Token).ConfigureAwait(false);
                for (var index = 0; index < batchSize; index++)
                {
                    var ordinal = checked(next + index);
                    await transaction.SaveAsync(
                        Key(ordinal),
                        CreatePayload(ordinal, payloadBytes),
                        cancellation.Token).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellation.Token).ConfigureAwait(false);
                var highest = checked(next + batchSize - 1);
                generation++;
                WriteLedger(root, new PowerLossLedger(generation, highest, payloadBytes, batchSize));
                next = highest + 1;
                completedBatches++;
                if (generation % 100 == 0)
                {
                    Console.WriteLine(
                        $"Acknowledged through record {highest:N0}; ledger generation {generation:N0}.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }

        void OnCancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        }
    }

    public static async Task RunValidatorAsync(string[] args)
    {
        var root = RequiredRoot(args);
        var ledger = ReadLatestLedger(root)
            ?? throw new InvalidDataException("No valid physical power-loss qualification ledger exists.");
        await using var store = await SegmentCoreStore.OpenAsync(root, Options()).ConfigureAwait(false);
        var requiredCount = checked((int)(ledger.HighestAcknowledgedOrdinal + 1));
        if (store.Count < requiredCount)
        {
            throw new InvalidDataException(
                $"Store contains {store.Count:N0} records but {requiredCount:N0} were acknowledged in the durable ledger.");
        }

        if (store.Count % ledger.BatchSize != 0)
        {
            throw new InvalidDataException(
                $"Recovered {store.Count:N0} records, which exposes a partial {ledger.BatchSize:N0}-object transaction.");
        }

        for (var ordinal = 0; ordinal < store.Count; ordinal++)
        {
            var payload = await store.GetAsync(Key(ordinal)).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Acknowledged record {ordinal:N0} is missing.");
            ValidatePayload(payload, ordinal, ledger.PayloadBytes);
        }

        var integrity = await store.VerifyIntegrityAsync().ConfigureAwait(false);
        if (!integrity.IsValid)
        {
            throw new InvalidDataException(string.Join("; ", integrity.Failures));
        }

        Console.WriteLine("SegmentCore physical power-loss validation: PASS");
        Console.WriteLine(
            $"Validated {requiredCount:N0} ledger-acknowledged records, {store.Count - requiredCount:N0} "
            + $"conservatively newer records, and {integrity.EntriesChecked:N0} live frames.");
        Console.WriteLine($"Ledger generation: {ledger.Generation:N0}; store root: {root}");
    }

    private static SegmentCoreOptions Options() => new()
    {
        TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
        CheckpointGenerationsToKeep = 2,
        WalRecycleMinimumReclaimBytes = 16L * 1024 * 1024,
        WalMaximumBytesBeforeForcedRecycle = 256L * 1024 * 1024,
        LogOptions = new StripedObjectLogProbeOptions
        {
            StripeCount = 8,
            MaximumBatchRecords = 256,
            QueueCapacityPerStripe = 4_096,
            MaxSegmentBytes = 256L * 1024 * 1024,
            MaximumCombinedWriteBytes = 4 * 1024 * 1024,
            DurabilityStrategy = ObjectLogDurabilityStrategy.WriteThrough
        },
        ReadOptions = new SegmentCoreReadOptions { StreamingBatchRecords = 8_192 }
    };

    private static void WriteLedger(string root, PowerLossLedger ledger)
    {
        var directory = Path.Combine(root, "power-loss-qualification");
        Directory.CreateDirectory(directory);
        var slot = (int)(ledger.Generation & 1);
        var path = Path.Combine(directory, $"ledger-{slot}.idbplq");
        var temporaryPath = $"{path}.writing-{Guid.NewGuid():N}";
        var bytes = new byte[LedgerBytes];
        LedgerMagic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), LedgerDataBytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), ledger.Generation);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), ledger.HighestAcknowledgedOrdinal);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32), ledger.PayloadBytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(36), ledger.BatchSize);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(40), DateTimeOffset.UtcNow.UtcTicks);
        SHA256.HashData(bytes.AsSpan(0, LedgerDataBytes)).CopyTo(bytes.AsSpan(LedgerDataBytes));
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                PlatformDurableFileSystem.Instance.ReplaceFile(temporaryPath, path);
            }
            else
            {
                PlatformDurableFileSystem.Instance.PublishFile(temporaryPath, path);
            }
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static PowerLossLedger? ReadLatestLedger(string root)
    {
        var directory = Path.Combine(root, "power-loss-qualification");
        if (!Directory.Exists(directory)) return null;
        PowerLossLedger? latest = null;
        foreach (var path in Directory.EnumerateFiles(directory, "ledger-*.idbplq"))
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length != LedgerBytes
                    || !bytes.AsSpan(0, 8).SequenceEqual(LedgerMagic)
                    || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) != 1
                    || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)) != LedgerDataBytes
                    || !SHA256.HashData(bytes.AsSpan(0, LedgerDataBytes))
                        .AsSpan().SequenceEqual(bytes.AsSpan(LedgerDataBytes)))
                {
                    continue;
                }

                var candidate = new PowerLossLedger(
                    BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)),
                    BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(32)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(36)));
                if (candidate.Generation > 0
                    && candidate.HighestAcknowledgedOrdinal >= 0
                    && candidate.PayloadBytes >= 16
                    && candidate.BatchSize > 0
                    && (latest is null || candidate.Generation > latest.Generation))
                {
                    latest = candidate;
                }
            }
            catch (IOException)
            {
            }
        }

        return latest;
    }

    private static byte[] CreatePayload(long ordinal, int length)
    {
        var payload = new byte[length];
        BinaryPrimitives.WriteInt64LittleEndian(payload, ordinal);
        for (var index = sizeof(long); index < length; index++)
            payload[index] = (byte)((ordinal * 31 + index * 17) & 0xFF);
        return payload;
    }

    private static void ValidatePayload(byte[] payload, long ordinal, int expectedLength)
    {
        if (payload.Length != expectedLength || BinaryPrimitives.ReadInt64LittleEndian(payload) != ordinal)
            throw new InvalidDataException($"Acknowledged record {ordinal:N0} has an invalid payload.");
        for (var index = sizeof(long); index < payload.Length; index++)
            if (payload[index] != (byte)((ordinal * 31 + index * 17) & 0xFF))
                throw new InvalidDataException($"Acknowledged record {ordinal:N0} has corrupt payload data.");
    }

    private static string Key(long ordinal) => $"power-loss-{ordinal:D12}";

    private static string RequiredRoot(string[] args)
    {
        var value = PhaseTwoOptions.ParseString(args, "--root");
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A dedicated --root directory is required.");
        var root = Path.GetFullPath(value);
        Directory.CreateDirectory(root);
        return root;
    }

    private static int ParseInt(string[] args, string name, int fallback, int minimum, int maximum)
    {
        var text = PhaseTwoOptions.ParseString(args, name);
        var value = text is null ? fallback : int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        return value >= minimum && value <= maximum ? value : throw new ArgumentOutOfRangeException(name);
    }

    private sealed record PowerLossLedger(
        long Generation,
        long HighestAcknowledgedOrdinal,
        int PayloadBytes,
        int BatchSize);
}
