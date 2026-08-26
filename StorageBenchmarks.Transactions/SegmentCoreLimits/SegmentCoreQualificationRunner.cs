using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Idb.Storage.SegmentCore;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;

internal static class SegmentCoreQualificationRunner
{
    public static async Task RunAsync(string[] args)
    {
        var options = QualificationOptions.Parse(args);
        var root = options.RootDirectory
            ?? Path.Combine(Path.GetTempPath(), "InterfaceDbSegmentCoreQualification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var report = new QualificationReport
        {
            StartedUtc = DateTimeOffset.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            EnvironmentKind = options.EnvironmentKind,
            Options = options,
            AvailableManagedMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            ProcessKillHarnessAvailable = true,
            PhysicalPowerCutProtocolAvailable = true
        };

        try
        {
            report.MixedLoad = await RunMixedLoadAsync(
                Path.Combine(root, "mixed"),
                options).ConfigureAwait(false);
            report.DiskExhaustion = await RunDiskExhaustionFaultAsync(
                Path.Combine(root, "disk-fault"),
                options).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            report.Failures.Add($"{exception.GetType().Name}: {exception.Message}");
            Console.Error.WriteLine(exception);
        }
        finally
        {
            report.CompletedUtc = DateTimeOffset.UtcNow;
            if (report.MixedLoad is not null)
            {
                report.DataToManagedMemoryRatio = report.AvailableManagedMemoryBytes <= 0
                    ? 0
                    : (double)report.MixedLoad.LivePayloadBytes / report.AvailableManagedMemoryBytes;
                report.DataLargerThanRamQualified = report.MixedLoad.LivePayloadBytes
                    > report.AvailableManagedMemoryBytes;
            }

            report.BareMetalLinuxQualified = OperatingSystem.IsLinux()
                && string.Equals(options.EnvironmentKind, "bare-metal", StringComparison.OrdinalIgnoreCase);
            report.PhysicalPowerCutExecuted = false;
            await WriteReportAsync(report, options.OutputPath).ConfigureAwait(false);
            if (!options.KeepData)
            {
                TryDelete(root);
            }
        }

        Console.WriteLine(report.EngineeringPass
            ? "SegmentCore automated qualification: PASS"
            : "SegmentCore automated qualification: FAIL");
        Console.WriteLine($"Mixed operations: {report.MixedLoad?.Operations:N0}; reopen: {report.MixedLoad?.ReopenMilliseconds:N2} ms");
        Console.WriteLine($"Disk-exhaustion recovery: {(report.DiskExhaustion?.Passed == true ? "PASS" : "FAIL")}");
        Console.WriteLine($"Live payload / available managed memory: {report.DataToManagedMemoryRatio:N3}x");
        Console.WriteLine($"Bare-metal Linux certified: {report.BareMetalLinuxQualified}");
        Console.WriteLine($"Physical power cut executed: {report.PhysicalPowerCutExecuted}");
        Console.WriteLine($"Report: {Path.GetFullPath(options.OutputPath)}");
        if (!report.EngineeringPass)
        {
            Environment.ExitCode = 1;
        }
    }

    private static async ValueTask<MixedLoadResult> RunMixedLoadAsync(
        string root,
        QualificationOptions options)
    {
        var storeOptions = StoreOptions(options, PlatformDurableFileSystem.Instance);
        var model = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        SegmentCoreStore? store = await SegmentCoreStore.OpenAsync(root, storeOptions).ConfigureAwait(false);
        try
        {
            for (var index = 0; index < options.SeedRecords; index++)
            {
                var key = Key(index);
                var payload = Payload(index, revision: 0, options.PayloadBytes);
                await store.SaveAsync(key, payload).ConfigureAwait(false);
                model[key] = payload;
            }

            _ = await store.CheckpointAsync().ConfigureAwait(false);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(options.DurationSeconds));
            var operationCount = 0L;
            var revision = 0;
            var writer = Task.Run(async () =>
            {
                var random = new Random(173);
                while (!stop.IsCancellationRequested)
                {
                    var ordinal = random.Next(options.SeedRecords);
                    var key = Key(ordinal);
                    if (random.Next(10) < 2)
                    {
                        // Cancellation controls admission to the next operation; an admitted
                        // durability operation is allowed to finish so the external model can
                        // record an unambiguous acknowledged outcome.
                        if (await store.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false))
                        {
                            model.TryRemove(key, out _);
                        }
                    }
                    else
                    {
                        var payload = Payload(ordinal, ++revision, options.PayloadBytes);
                        await store.SaveAsync(key, payload, CancellationToken.None).ConfigureAwait(false);
                        model[key] = payload;
                    }

                    Interlocked.Increment(ref operationCount);
                }
            }, CancellationToken.None);
            var readers = Enumerable.Range(0, options.ReaderTasks).Select(readerIndex => Task.Run(async () =>
            {
                var random = new Random(1_000 + readerIndex);
                while (!stop.IsCancellationRequested)
                {
                    var ordinal = random.Next(options.SeedRecords);
                    var payload = await store.GetAsync(Key(ordinal), CancellationToken.None).ConfigureAwait(false);
                    if (payload is not null)
                    {
                        ValidatePayload(payload, ordinal, options.PayloadBytes);
                    }

                    Interlocked.Increment(ref operationCount);
                }
            }, CancellationToken.None)).ToArray();
            var maintenance = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stop.Token).ConfigureAwait(false);
                    _ = await store.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }, CancellationToken.None);

            try
            {
                await Task.WhenAll(readers.Append(writer).Append(maintenance)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }

            _ = await store.CheckpointAsync().ConfigureAwait(false);
            var integrity = await store.VerifyIntegrityAsync().ConfigureAwait(false);
            if (!integrity.IsValid)
            {
                throw new InvalidDataException(string.Join("; ", integrity.Failures));
            }

            await store.DisposeAsync().ConfigureAwait(false);
            store = null;
            var reopen = Stopwatch.StartNew();
            store = await SegmentCoreStore.OpenAsync(root, storeOptions).ConfigureAwait(false);
            reopen.Stop();
            if (store.Count != model.Count)
            {
                throw new InvalidDataException(
                    $"Mixed-load reopen has {store.Count:N0} keys; model has {model.Count:N0}.");
            }

            foreach (var pair in model)
            {
                var actual = await store.GetAsync(pair.Key).ConfigureAwait(false)
                    ?? throw new InvalidDataException($"Mixed-load reopen lost '{pair.Key}'.");
                if (!actual.AsSpan().SequenceEqual(pair.Value))
                {
                    throw new InvalidDataException($"Mixed-load reopen changed '{pair.Key}'.");
                }
            }

            return new MixedLoadResult
            {
                Passed = true,
                Operations = operationCount,
                LiveEntries = model.Count,
                LivePayloadBytes = model.Values.Sum(static payload => (long)payload.Length),
                StoreBytes = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Sum(static path => new FileInfo(path).Length),
                ReopenMilliseconds = reopen.Elapsed.TotalMilliseconds,
                WalPrefixBytesSkipped = store.Statistics.WalRecoveryPrefixBytesSkipped,
                PagedPrimaryIndex = store.Statistics.PrimaryIndexIsPaged
            };
        }
        finally
        {
            if (store is not null)
            {
                await store.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask<DiskExhaustionResult> RunDiskExhaustionFaultAsync(
        string root,
        QualificationOptions options)
    {
        var fileSystem = new DiskExhaustionDurableFileSystem();
        // The fault injector observes explicit durability barriers. Force this
        // scenario onto the explicit-flush path even when the main qualification
        // run uses write-through handles (where the OS performs each flush).
        var faultOptions = StoreOptions(options, fileSystem, forceExplicitFlush: true);
        SegmentCoreStore? store = await SegmentCoreStore.OpenAsync(root, faultOptions).ConfigureAwait(false);
        var failedAsExpected = false;
        try
        {
            await store.SaveAsync("known-durable", Payload(1, 1, options.PayloadBytes)).ConfigureAwait(false);
            _ = await store.CheckpointAsync().ConfigureAwait(false);
            fileSystem.FailNextFlush();
            try
            {
                await using var transaction = await store.BeginTransactionAsync().ConfigureAwait(false);
                await transaction.SaveAsync(
                    "uncertain",
                    Payload(2, 2, options.PayloadBytes)).ConfigureAwait(false);
                await transaction.CommitAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                failedAsExpected = true;
            }
        }
        finally
        {
            if (store is not null)
            {
                try
                {
                    await store.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        var reopenOptions = StoreOptions(options, PlatformDurableFileSystem.Instance);
        await using var reopened = await SegmentCoreStore.OpenAsync(root, reopenOptions).ConfigureAwait(false);
        var durable = await reopened.GetAsync("known-durable").ConfigureAwait(false);
        if (durable is null)
        {
            throw new InvalidDataException("Disk-exhaustion recovery lost a previously acknowledged object.");
        }

        ValidatePayload(durable, 1, options.PayloadBytes);
        var uncertain = await reopened.GetAsync("uncertain").ConfigureAwait(false);
        if (uncertain is not null)
        {
            ValidatePayload(uncertain, 2, options.PayloadBytes);
        }

        var integrity = await reopened.VerifyIntegrityAsync().ConfigureAwait(false);
        return new DiskExhaustionResult
        {
            Passed = failedAsExpected && integrity.IsValid,
            FailureObserved = failedAsExpected,
            UncertainTransactionRecovered = uncertain is not null,
            IntegrityValid = integrity.IsValid
        };
    }

    private static SegmentCoreOptions StoreOptions(
        QualificationOptions options,
        IDurableFileSystem durableFileSystem,
        bool forceExplicitFlush = false) => new()
    {
        TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
        CheckpointGenerationsToKeep = 2,
        DurableFileSystem = durableFileSystem,
        PrimaryIndexOptions = new SegmentCorePrimaryIndexOptions
        {
            Mode = SegmentCorePrimaryIndexMode.Automatic,
            AutomaticPagingMinimumEntries = Math.Max(1_000, options.PagingThreshold)
        },
        LogOptions = new StripedObjectLogProbeOptions
        {
            StripeCount = options.Stripes,
            MaximumBatchRecords = 256,
            QueueCapacityPerStripe = 4_096,
            MaxSegmentBytes = 64L * 1024 * 1024,
            MaximumCombinedWriteBytes = 4 * 1024 * 1024,
            DurabilityStrategy = options.WriteThrough && !forceExplicitFlush
                ? ObjectLogDurabilityStrategy.WriteThrough
                : ObjectLogDurabilityStrategy.ExplicitFlush
        }
    };

    private static byte[] Payload(int ordinal, int revision, int length)
    {
        var payload = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, ordinal);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), revision);
        for (var index = 8; index < payload.Length; index++)
        {
            payload[index] = (byte)((ordinal * 31 + revision * 17 + index) & 0xFF);
        }

        return payload;
    }

    private static void ValidatePayload(byte[] payload, int ordinal, int length)
    {
        if (payload.Length != length || BinaryPrimitives.ReadInt32LittleEndian(payload) != ordinal)
        {
            throw new InvalidDataException($"Qualification payload {ordinal:N0} has an invalid header.");
        }

        var revision = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4));
        for (var index = 8; index < payload.Length; index++)
        {
            if (payload[index] != (byte)((ordinal * 31 + revision * 17 + index) & 0xFF))
            {
                throw new InvalidDataException($"Qualification payload {ordinal:N0} is corrupt.");
            }
        }
    }

    private static string Key(int ordinal) => $"qualification-{ordinal:D10}";

    private static async ValueTask WriteReportAsync(QualificationReport report, string outputPath)
    {
        var path = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class DiskExhaustionDurableFileSystem : IDurableFileSystem
    {
        private int _failNextFlush;

        public string DirectoryFlushStrategy => PlatformDurableFileSystem.Instance.DirectoryFlushStrategy;

        public void FailNextFlush() => Interlocked.Exchange(ref _failNextFlush, 1);

        public void FlushFile(SafeFileHandle handle)
        {
            if (Interlocked.Exchange(ref _failNextFlush, 0) != 0)
            {
                throw new IOException("Injected ENOSPC-equivalent durability failure.");
            }

            PlatformDurableFileSystem.Instance.FlushFile(handle);
        }

        public void FlushDirectory(string directoryPath) =>
            PlatformDurableFileSystem.Instance.FlushDirectory(directoryPath);

        public void PublishFile(string temporaryPath, string finalPath) =>
            PlatformDurableFileSystem.Instance.PublishFile(temporaryPath, finalPath);

        public void ReplaceFile(string temporaryPath, string finalPath) =>
            PlatformDurableFileSystem.Instance.ReplaceFile(temporaryPath, finalPath);
    }

    internal sealed record QualificationOptions
    {
        public int DurationSeconds { get; init; }
        public int SeedRecords { get; init; }
        public int PayloadBytes { get; init; }
        public int ReaderTasks { get; init; }
        public int Stripes { get; init; }
        public int PagingThreshold { get; init; }
        public bool WriteThrough { get; init; }
        public bool KeepData { get; init; }
        public string? RootDirectory { get; init; }
        public string OutputPath { get; init; } = "segment-core-qualification.json";
        public string EnvironmentKind { get; init; } = "virtualized-or-development";

        public static QualificationOptions Parse(string[] args) => new()
        {
            DurationSeconds = ParseInt(args, "--duration-seconds", 60, 1, 86_400),
            SeedRecords = ParseInt(args, "--records", 100_000, 100, 50_000_000),
            PayloadBytes = ParseInt(args, "--payload-bytes", 1_024, 64, 1024 * 1024),
            ReaderTasks = ParseInt(args, "--readers", 4, 1, 64),
            Stripes = ParsePowerOfTwo(args, "--stripes", 8),
            PagingThreshold = ParseInt(args, "--paging-threshold", 100_000, 1_000, 100_000_000),
            WriteThrough = !args.Contains("--explicit-flush", StringComparer.OrdinalIgnoreCase),
            KeepData = args.Contains("--keep-data", StringComparer.OrdinalIgnoreCase),
            RootDirectory = PhaseTwoOptions.ParseString(args, "--root") is { } root ? Path.GetFullPath(root) : null,
            OutputPath = PhaseTwoOptions.ParseString(args, "--output") ?? "segment-core-qualification.json",
            EnvironmentKind = PhaseTwoOptions.ParseString(args, "--qualification-environment")
                ?? "virtualized-or-development"
        };

        private static int ParseInt(string[] args, string name, int fallback, int minimum, int maximum)
        {
            var text = PhaseTwoOptions.ParseString(args, name);
            var value = text is null
                ? fallback
                : int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            return value >= minimum && value <= maximum
                ? value
                : throw new ArgumentOutOfRangeException(name);
        }

        private static int ParsePowerOfTwo(string[] args, string name, int fallback)
        {
            var value = ParseInt(args, name, fallback, 1, 64);
            return System.Numerics.BitOperations.IsPow2((uint)value)
                ? value
                : throw new ArgumentOutOfRangeException(name, "Stripe count must be a power of two.");
        }
    }

    internal sealed record QualificationReport
    {
        public DateTimeOffset StartedUtc { get; init; }
        public DateTimeOffset CompletedUtc { get; set; }
        public string Runtime { get; init; } = string.Empty;
        public string OperatingSystem { get; init; } = string.Empty;
        public string EnvironmentKind { get; init; } = string.Empty;
        public required QualificationOptions Options { get; init; }
        public long AvailableManagedMemoryBytes { get; init; }
        public MixedLoadResult? MixedLoad { get; set; }
        public DiskExhaustionResult? DiskExhaustion { get; set; }
        public bool ProcessKillHarnessAvailable { get; init; }
        public bool PhysicalPowerCutProtocolAvailable { get; init; }
        public bool PhysicalPowerCutExecuted { get; set; }
        public bool BareMetalLinuxQualified { get; set; }
        public bool DataLargerThanRamQualified { get; set; }
        public double DataToManagedMemoryRatio { get; set; }
        public List<string> Failures { get; init; } = [];
        public bool EngineeringPass => Failures.Count == 0
            && MixedLoad?.Passed == true
            && DiskExhaustion?.Passed == true;
        public bool ProductionCertificationComplete => EngineeringPass
            && BareMetalLinuxQualified
            && DataLargerThanRamQualified
            && PhysicalPowerCutExecuted;
    }

    internal sealed record MixedLoadResult
    {
        public bool Passed { get; init; }
        public long Operations { get; init; }
        public int LiveEntries { get; init; }
        public long LivePayloadBytes { get; init; }
        public long StoreBytes { get; init; }
        public double ReopenMilliseconds { get; init; }
        public long WalPrefixBytesSkipped { get; init; }
        public bool PagedPrimaryIndex { get; init; }
    }

    internal sealed record DiskExhaustionResult
    {
        public bool Passed { get; init; }
        public bool FailureObserved { get; init; }
        public bool UncertainTransactionRecovered { get; init; }
        public bool IntegrityValid { get; init; }
    }
}
