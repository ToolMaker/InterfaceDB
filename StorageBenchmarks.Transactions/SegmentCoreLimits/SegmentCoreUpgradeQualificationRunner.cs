using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Idb.Storage.SegmentCore;
using Idb.Storage.SegmentCore.ObjectLog;

internal static class SegmentCoreUpgradeQualificationRunner
{
    private static readonly byte[] BaselinePayload = [1, 3, 5, 7, 9, 11];
    private static readonly byte[] CurrentPayload = [2, 4, 6, 8, 10, 12];

    public static async Task RunAsync(string[] args)
    {
        var root = Path.GetFullPath(Required(args, "--root"));
        var baselineVersion = Required(args, "--baseline-version");
        var output = PhaseTwoOptions.ParseString(args, "--output")
            ?? "segment-core-upgrade-qualification.json";
        var backup = root + "-current-backup";
        var restored = root + "-current-restored";
        var report = new UpgradeQualificationReport
        {
            StartedUtc = DateTimeOffset.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            BaselineVersion = baselineVersion,
            StoreRoot = root
        };

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var options = Options();
            await using (var store = await SegmentCoreStore.OpenAsync(root, options).ConfigureAwait(false))
            {
                RequirePayload(await store.GetAsync("baseline-key").ConfigureAwait(false), BaselinePayload, "baseline-key");
                await using var transaction = await store.BeginTransactionAsync().ConfigureAwait(false);
                await transaction.SaveAsync("current-key", CurrentPayload).ConfigureAwait(false);
                await transaction.CommitAsync().ConfigureAwait(false);
                _ = await store.CheckpointAsync().ConfigureAwait(false);
                var integrity = await store.VerifyIntegrityAsync().ConfigureAwait(false);
                if (!integrity.IsValid)
                {
                    throw new InvalidDataException(string.Join("; ", integrity.Failures));
                }

                _ = await store.BackupAsync(backup).ConfigureAwait(false);
                report.SourceEntries = store.Count;
                report.SourceCheckpointGeneration = store.Statistics.CheckpointGeneration;
            }

            _ = await SegmentCoreStore.RestoreBackupAsync(backup, restored, Options()).ConfigureAwait(false);
            await using (var reopened = await SegmentCoreStore.OpenAsync(restored, Options()).ConfigureAwait(false))
            {
                RequirePayload(await reopened.GetAsync("baseline-key").ConfigureAwait(false), BaselinePayload, "baseline-key");
                RequirePayload(await reopened.GetAsync("current-key").ConfigureAwait(false), CurrentPayload, "current-key");
                var integrity = await reopened.VerifyIntegrityAsync().ConfigureAwait(false);
                if (!integrity.IsValid || reopened.Count != 2)
                {
                    throw new InvalidDataException(
                        "Upgraded backup/restore failed integrity: " + string.Join("; ", integrity.Failures));
                }

                report.RestoredEntries = reopened.Count;
                report.RestoredCheckpointGeneration = reopened.Statistics.CheckpointGeneration;
            }

            report.Passed = true;
        }
        catch (Exception exception)
        {
            report.Failure = $"{exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            stopwatch.Stop();
            report.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            report.CompletedUtc = DateTimeOffset.UtcNow;
            await WriteReportAsync(output, report).ConfigureAwait(false);
        }

        Console.WriteLine(report.Passed
            ? $"SegmentCore package upgrade {baselineVersion} -> current source: PASS"
            : $"SegmentCore package upgrade {baselineVersion} -> current source: FAIL; {report.Failure}");
        Console.WriteLine($"Report: {Path.GetFullPath(output)}");
        if (!report.Passed)
        {
            Environment.ExitCode = 1;
        }
    }

    private static SegmentCoreOptions Options() => new()
    {
        TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
        CheckpointGenerationsToKeep = 2,
        PrimaryIndexOptions = new SegmentCorePrimaryIndexOptions
        {
            Mode = SegmentCorePrimaryIndexMode.Paged
        },
        LogOptions = new StripedObjectLogProbeOptions
        {
            StripeCount = 2,
            DurabilityStrategy = ObjectLogDurabilityStrategy.ExplicitFlush
        }
    };

    private static void RequirePayload(byte[]? actual, ReadOnlySpan<byte> expected, string key)
    {
        if (actual is null || !actual.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidDataException($"Upgrade qualification did not preserve '{key}'.");
        }
    }

    private static string Required(string[] args, string name) =>
        PhaseTwoOptions.ParseString(args, name)
        ?? throw new ArgumentException($"{name} is required.");

    private static async Task WriteReportAsync(string output, UpgradeQualificationReport report)
    {
        var path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }

    private sealed record UpgradeQualificationReport
    {
        public DateTimeOffset StartedUtc { get; init; }
        public DateTimeOffset CompletedUtc { get; set; }
        public required string Runtime { get; init; }
        public required string OperatingSystem { get; init; }
        public required string BaselineVersion { get; init; }
        public required string StoreRoot { get; init; }
        public bool Passed { get; set; }
        public int SourceEntries { get; set; }
        public long SourceCheckpointGeneration { get; set; }
        public int RestoredEntries { get; set; }
        public long RestoredCheckpointGeneration { get; set; }
        public double ElapsedMilliseconds { get; set; }
        public string? Failure { get; set; }
    }
}
