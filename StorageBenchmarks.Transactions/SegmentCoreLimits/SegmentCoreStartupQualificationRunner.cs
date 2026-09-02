using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Implementation.Serialization;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;

internal static class SegmentCoreStartupQualificationRunner
{
    private const string RawMode = "raw-store";
    private const string ColdRepositoryMode = "repository-cold-missing-catalog";
    private const string WarmRepositoryMode = "repository-valid-warm-catalog";
    private const string CatalogPreparationMode = "catalog-preparation";
    private const string SegmentCoreMeterName = "InterfaceDB.Storage.SegmentCore";
    private const string OpenStageInstrumentName = "interfacedb.segmentcore.open.stage.duration";
    private const string MaterializationContract = "GateRecord/FNV1a32-v2/all-six-fields";
    private const string Version = "1.0.0";

    public static async Task RunAsync(string[] args)
    {
        var options = StartupOptions.Parse(args);
        var ownedParent = Path.Combine(Path.GetTempPath(), "InterfaceDbSegmentCoreStartupQualification");
        var runRoot = Path.Combine(ownedParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        var report = new StartupReport
        {
            StartedUtc = DateTimeOffset.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            ProcessorCount = Environment.ProcessorCount,
            Options = options,
            MeasurementDefinitions = new StartupMeasurementDefinitions()
        };

        try
        {
            var seededRoot = Path.Combine(runRoot, "post-write-seed");
            var repositoryOptions = CreateRepositoryOptions(options.HardwareProfile);
            await SeedCanonicalStoreAsync(seededRoot, repositoryOptions, options).ConfigureAwait(false);
            report.PostWriteSeedCatalogState = await CaptureCatalogStateAsync(
                seededRoot,
                "catalog left by normal initialize-write-checkpoint-provider-dispose seeding").ConfigureAwait(false);

            var coldCanonicalRoot = Path.Combine(runRoot, "canonical-cold-missing-catalog");
            CopyDirectory(seededRoot, coldCanonicalRoot);
            RemoveCatalogFiles(runRoot, coldCanonicalRoot);
            report.ColdCatalogState = await CaptureCatalogStateAsync(
                coldCanonicalRoot,
                "catalog deliberately absent; initialization must rebuild indexes from authoritative payloads")
                .ConfigureAwait(false);

            var warmCanonicalRoot = Path.Combine(runRoot, "canonical-valid-warm-catalog");
            CopyDirectory(seededRoot, warmCanonicalRoot);
            report.WarmCatalogPreparation = await RunChildAsync(
                runRoot,
                CatalogPreparationMode,
                warmCanonicalRoot,
                0,
                options).ConfigureAwait(false);
            if (!report.WarmCatalogPreparation.Passed || report.WarmCatalogPreparation.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "The untimed warm-catalog preparation worker failed: "
                    + (report.WarmCatalogPreparation.Error ?? $"exit {report.WarmCatalogPreparation.ExitCode}"));
            }

            if (report.WarmCatalogPreparation.ActivatedEntries != options.Records)
            {
                throw new InvalidDataException(
                    "The untimed warm-catalog preparation worker activated "
                    + $"{report.WarmCatalogPreparation.ActivatedEntries:N0}/{options.Records:N0} entries.");
            }

            report.PostWriteSeedCatalogAcceptedWithoutRebuild =
                report.WarmCatalogPreparation.InitializationDeserializedObjects == 0;
            report.PostWriteSeedCatalogState.VerifiedAcceptedWithoutPayloadDeserialization =
                report.PostWriteSeedCatalogAcceptedWithoutRebuild;
            if (report.WarmCatalogPreparation.InitializationDeserializedObjects is not 0
                && report.WarmCatalogPreparation.InitializationDeserializedObjects != options.Records)
            {
                throw new InvalidDataException(
                    "Post-write seed catalog verification deserialized an unexpected "
                    + $"{report.WarmCatalogPreparation.InitializationDeserializedObjects:N0}/{options.Records:N0} objects.");
            }

            report.PostWriteSeedCatalogInterpretation = report.PostWriteSeedCatalogAcceptedWithoutRebuild
                ? "The normal post-write provider disposal left a catalog accepted without payload reconstruction."
                : "The normal post-write provider disposal did not leave a current catalog; the verification "
                    + $"initialization reconstructed all {options.Records:N0} payload objects before writing a valid catalog.";
            report.ValidWarmCatalogState = await CaptureCatalogStateAsync(
                warmCanonicalRoot,
                "catalog produced or verified by one untimed full repository initialization")
                .ConfigureAwait(false);

            report.RawAndColdCanonicalStoreFingerprint = report.ColdCatalogState.StoreFingerprint;
            report.RawAndColdCanonicalStoreFiles = Directory.EnumerateFiles(coldCanonicalRoot, "*", SearchOption.AllDirectories).Count();
            report.RawAndColdCanonicalStoreBytes = Directory.EnumerateFiles(coldCanonicalRoot, "*", SearchOption.AllDirectories)
                .Sum(static path => new FileInfo(path).Length);
            report.ExpectedFirstRecordChecksum = GateRecord.Create(0, "startup").MaterializedChecksum;

            for (var iteration = 0; iteration < options.Iterations; iteration++)
            {
                var rawRoot = Path.Combine(runRoot, $"raw-{iteration:D3}");
                var coldRepositoryRoot = Path.Combine(runRoot, $"repository-cold-{iteration:D3}");
                var warmRepositoryRoot = Path.Combine(runRoot, $"repository-warm-{iteration:D3}");
                CopyDirectory(coldCanonicalRoot, rawRoot);
                CopyDirectory(coldCanonicalRoot, coldRepositoryRoot);
                CopyDirectory(warmCanonicalRoot, warmRepositoryRoot);
                await RequireFingerprintAsync(rawRoot, report.ColdCatalogState.StoreFingerprint).ConfigureAwait(false);
                await RequireFingerprintAsync(coldRepositoryRoot, report.ColdCatalogState.StoreFingerprint).ConfigureAwait(false);
                await RequireFingerprintAsync(warmRepositoryRoot, report.ValidWarmCatalogState.StoreFingerprint).ConfigureAwait(false);

                report.Samples.Add(await RunChildAsync(
                    runRoot,
                    RawMode,
                    rawRoot,
                    iteration,
                    options).ConfigureAwait(false));
                report.Samples.Add(await RunChildAsync(
                    runRoot,
                    ColdRepositoryMode,
                    coldRepositoryRoot,
                    iteration,
                    options).ConfigureAwait(false));
                report.Samples.Add(await RunChildAsync(
                    runRoot,
                    WarmRepositoryMode,
                    warmRepositoryRoot,
                    iteration,
                    options).ConfigureAwait(false));
            }

            ValidateSamples(report);
            report.Summaries = CreateSummaries(report.Samples);
        }
        catch (Exception exception)
        {
            report.Failures.Add($"{exception.GetType().Name}: {exception.Message}");
            Console.Error.WriteLine(exception);
        }
        finally
        {
            report.CompletedUtc = DateTimeOffset.UtcNow;
            if (options.KeepData)
            {
                report.DataRoot = runRoot;
            }

            await WriteJsonAsync(options.OutputPath, report).ConfigureAwait(false);
            if (!options.KeepData)
            {
                TryDeleteOwnedRoot(ownedParent, runRoot);
            }
        }

        Console.WriteLine(report.Passed
            ? "SegmentCore startup qualification: PASS"
            : "SegmentCore startup qualification: FAIL");
        foreach (var summary in report.Summaries)
        {
            Console.WriteLine(
                $"  {summary.Metric}: p50 {FormatMilliseconds(summary.P50Milliseconds)} ms; "
                + $"min {FormatMilliseconds(summary.MinimumMilliseconds)} ms; "
                + $"max {FormatMilliseconds(summary.MaximumMilliseconds)} ms; "
                + $"n={summary.Samples}");
        }

        foreach (var failure in report.Failures)
        {
            Console.WriteLine($"  FAIL: {failure}");
        }

        Console.WriteLine($"Report: {Path.GetFullPath(options.OutputPath)}");
        if (!report.Passed)
        {
            Environment.ExitCode = 1;
        }
    }

    public static async Task RunWorkerAsync(string[] args)
    {
        var mode = Required(args, "--mode");
        var dataRoot = Path.GetFullPath(Required(args, "--data-root"));
        var output = Path.GetFullPath(Required(args, "--output"));
        var iteration = ParseInt(args, "--iteration", 0, 0, 10_000);
        var records = ParseInt(args, "--records", 100_000, 1, 10_000_000);
        var profile = ParseProfile(PhaseTwoOptions.ParseString(args, "--profile") ?? "HighThroughput");
        var captureStages = args.Contains("--capture-open-stages", StringComparer.OrdinalIgnoreCase);
        var result = new StartupSample
        {
            Mode = mode,
            Iteration = iteration,
            ProcessId = Environment.ProcessId,
            Records = records,
            MaterializationContract = IsRepositoryMode(mode) ? MaterializationContract : null,
            CatalogCondition = mode
        };

        try
        {
            if (string.Equals(mode, RawMode, StringComparison.Ordinal))
            {
                await MeasureRawStoreAsync(dataRoot, profile, captureStages, result).ConfigureAwait(false);
            }
            else if (IsRepositoryMode(mode))
            {
                await MeasureRepositoryAsync(dataRoot, profile, captureStages, result).ConfigureAwait(false);
            }
            else
            {
                throw new ArgumentOutOfRangeException(
                    "--mode",
                    mode,
                    $"Expected {RawMode}, {ColdRepositoryMode}, {WarmRepositoryMode}, or {CatalogPreparationMode}.");
            }

            result.Passed = true;
        }
        catch (Exception exception)
        {
            result.Error = $"{exception.GetType().Name}: {exception.Message}";
            Console.Error.WriteLine(exception);
            Environment.ExitCode = 1;
        }
        finally
        {
            await WriteJsonAsync(output, result).ConfigureAwait(false);
        }
    }

    private static async Task SeedCanonicalStoreAsync(
        string dataRoot,
        SegmentCoreRepositoryOptions repositoryOptions,
        StartupOptions options)
    {
        await using var store = new SegmentCorePilotStore(dataRoot, repositoryOptions);
        await store.InitializeAsync().ConfigureAwait(false);
        var records = Enumerable.Range(0, options.Records)
            .Select(static ordinal => GateRecord.Create(ordinal, "startup"));
        await store.SaveManyAsync(records, options.BatchSize).ConfigureAwait(false);
        _ = await store.CheckpointAsync().ConfigureAwait(false);
        var integrity = await store.VerifyIntegrityAsync().ConfigureAwait(false);
        if (!integrity.IsValid || integrity.EntriesChecked != options.Records)
        {
            throw new InvalidDataException(
                $"Canonical store integrity checked {integrity.EntriesChecked:N0}/{options.Records:N0} records: "
                + string.Join("; ", integrity.Failures));
        }
    }

    private static async Task MeasureRawStoreAsync(
        string dataRoot,
        Idb.Libraries.Abstractions.Enums.StorageHardwareProfile profile,
        bool captureStages,
        StartupSample result)
    {
        var repositoryOptions = CreateRepositoryOptions(profile);
        var physicalRoot = Path.Combine(dataRoot, repositoryOptions.DirectoryName, Version);
        using var stages = captureStages ? new OpenStageCollector() : null;
        ForceGc();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        await using var store = await SegmentCoreStore.OpenAsync(
            physicalRoot,
            repositoryOptions.StoreOptions).ConfigureAwait(false);
        result.RawStoreActivationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        result.RawStoreActivationAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        result.OpenStagesMilliseconds = stages?.Snapshot();
        result.ActivatedEntries = store.Count;
        if (store.Count != result.Records)
        {
            throw new InvalidDataException(
                $"Raw store activated {store.Count:N0}/{result.Records:N0} entries.");
        }
    }

    private static async Task MeasureRepositoryAsync(
        string dataRoot,
        Idb.Libraries.Abstractions.Enums.StorageHardwareProfile profile,
        bool captureStages,
        StartupSample result)
    {
        var repositoryOptions = CreateRepositoryOptions(profile);
        var contractOptions = new SegmentCorePilotContractOptions(dataRoot, profile);
        var expected = GateRecord.Create(0, "startup");
        var validationWarmup = new ProductionGateRecord(
            expected.Id,
            expected.Name,
            expected.Age,
            expected.Role,
            expected.Content,
            expected.Revision);
        ValidateRecord(validationWarmup, expected);
        var expectedChecksum = GateRecordMaterialization.Checksum(
            validationWarmup.Id,
            validationWarmup.Name,
            validationWarmup.Age,
            validationWarmup.Role,
            validationWarmup.Content,
            validationWarmup.Revision);
        CountingStartupSerializer? serializer = null;
        using var stages = captureStages ? new OpenStageCollector() : null;
        ForceGc();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var fullStarted = Stopwatch.GetTimestamp();

        var registrationStarted = Stopwatch.GetTimestamp();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IObjectSerializer<IProductionGateRecord>>(
            contractOptions.EnvelopeDICacheKey,
            (_, _) => serializer = new CountingStartupSerializer());
        services.AddSegmentCoreRepository<IProductionGateRecord>(
            contractOptions,
            repositoryOptions,
            contractOptions.StoragePolicy,
            new SegmentCorePilotIdentityFactory());
        var registrationCompleted = Stopwatch.GetTimestamp();

        var resolutionStarted = Stopwatch.GetTimestamp();
        await using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<IRepository<IProductionGateRecord>>();
        var maintenance = provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IProductionGateRecord>>();
        var resolutionCompleted = Stopwatch.GetTimestamp();

        var initializationStarted = Stopwatch.GetTimestamp();
        await repository.InitializeAsync().ConfigureAwait(false);
        var initializationCompleted = Stopwatch.GetTimestamp();
        var initializationAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var countingSerializer = serializer
            ?? throw new InvalidOperationException("DI did not construct the configured startup serializer.");
        var initializationDeserializedObjects = countingSerializer.DeserializeCount;

        var firstReadStarted = Stopwatch.GetTimestamp();
        var record = await repository.QueryFirstByStorageKeyAsync(expected.Id).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Repository did not materialize '{expected.Id}'.");
        ValidateRecord(record, expected);
        var actualChecksum = GateRecordMaterialization.Checksum(
            record.Id,
            record.Name,
            record.Age,
            record.Role,
            record.Content,
            record.Revision);
        var firstObjectCompleted = Stopwatch.GetTimestamp();
        var firstObjectAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        if (actualChecksum != expectedChecksum)
        {
            throw new InvalidDataException(
                $"First materialized object checksum {actualChecksum} did not match {expectedChecksum}.");
        }

        result.ServiceRegistrationMilliseconds =
            Stopwatch.GetElapsedTime(registrationStarted, registrationCompleted).TotalMilliseconds;
        result.ProviderBuildAndRepositoryResolutionMilliseconds =
            Stopwatch.GetElapsedTime(resolutionStarted, resolutionCompleted).TotalMilliseconds;
        result.RepositoryInitializeMilliseconds =
            Stopwatch.GetElapsedTime(initializationStarted, initializationCompleted).TotalMilliseconds;
        result.FullRepositoryInitializationMilliseconds =
            Stopwatch.GetElapsedTime(fullStarted, initializationCompleted).TotalMilliseconds;
        result.FullRepositoryInitializationAllocatedBytes = initializationAllocatedBytes;
        result.OpenStagesMilliseconds = stages?.Snapshot();
        result.InitializationDeserializedObjects = initializationDeserializedObjects;
        result.DeserializedObjectsAfterFirstMaterializedObject = countingSerializer.DeserializeCount;
        result.FirstMaterializedObjectChecksum = actualChecksum;
        result.FirstMaterializedObjectMilliseconds =
            Stopwatch.GetElapsedTime(firstReadStarted, firstObjectCompleted).TotalMilliseconds;
        result.TimeToFirstMaterializedObjectMilliseconds =
            Stopwatch.GetElapsedTime(fullStarted, firstObjectCompleted).TotalMilliseconds;
        result.TimeToFirstMaterializedObjectAllocatedBytes = firstObjectAllocatedBytes;
        result.ActivatedEntries = maintenance.Status.StoreStatistics?.LiveEntries ?? 0;
        GC.KeepAlive(record);

        var repeatDeserializeCountBefore = countingSerializer.DeserializeCount;
        var repeatStatusBefore = maintenance.Status;
        var repeatAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var repeatStarted = Stopwatch.GetTimestamp();
        await repository.InitializeAsync().ConfigureAwait(false);
        var repeatCompleted = Stopwatch.GetTimestamp();
        var repeatAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - repeatAllocatedBefore;
        var repeatDeserializeCountAfter = countingSerializer.DeserializeCount;
        var repeatStatusAfter = maintenance.Status;
        result.SameLifetimeRepeatedInitializeMilliseconds =
            Stopwatch.GetElapsedTime(repeatStarted, repeatCompleted).TotalMilliseconds;
        result.SameLifetimeRepeatedInitializeAllocatedBytes = repeatAllocatedBytes;
        result.SameLifetimeRepeatedInitializeDeserializedObjects =
            repeatDeserializeCountAfter - repeatDeserializeCountBefore;
        result.SameLifetimeRepeatedInitializeRemainedInitialized =
            repeatStatusBefore.IsInitialized && repeatStatusAfter.IsInitialized;
        result.SameLifetimeRepeatedInitializeStatusUnchanged = repeatStatusBefore.Equals(repeatStatusAfter);
        if (result.SameLifetimeRepeatedInitializeDeserializedObjects != 0
            || !result.SameLifetimeRepeatedInitializeRemainedInitialized
            || !result.SameLifetimeRepeatedInitializeStatusUnchanged)
        {
            throw new InvalidDataException(
                "Repeated IRepository.InitializeAsync was not a state-preserving, zero-deserialization fast path.");
        }
    }

    private static void ValidateRecord(IProductionGateRecord actual, GateRecord expected)
    {
        if (!string.Equals(actual.Id, expected.Id, StringComparison.Ordinal)
            || !string.Equals(actual.Name, expected.Name, StringComparison.Ordinal)
            || actual.Age != expected.Age
            || !string.Equals(actual.Role, expected.Role, StringComparison.Ordinal)
            || !string.Equals(actual.Content, expected.Content, StringComparison.Ordinal)
            || actual.Revision != expected.Revision)
        {
            throw new InvalidDataException($"First materialized object '{expected.Id}' did not preserve all six fields.");
        }
    }

    private static SegmentCoreRepositoryOptions CreateRepositoryOptions(
        Idb.Libraries.Abstractions.Enums.StorageHardwareProfile profile) =>
        SegmentCoreRepositoryOptions.ForHardwareProfile(profile) with
        {
            EnableBackgroundMaintenance = false,
            CheckpointOnDispose = true
        };

    private static async Task<StartupSample> RunChildAsync(
        string runRoot,
        string mode,
        string dataRoot,
        int iteration,
        StartupOptions options)
    {
        var outputRoot = Path.Combine(runRoot, "worker-results");
        Directory.CreateDirectory(outputRoot);
        var output = Path.Combine(outputRoot, $"{mode}-{iteration:D3}.json");
        var assembly = Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException("Cannot resolve the benchmark entry assembly.");
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--segment-core-startup-worker");
        start.ArgumentList.Add($"--mode={mode}");
        start.ArgumentList.Add($"--data-root={dataRoot}");
        start.ArgumentList.Add($"--iteration={iteration.ToString(CultureInfo.InvariantCulture)}");
        start.ArgumentList.Add($"--records={options.Records.ToString(CultureInfo.InvariantCulture)}");
        start.ArgumentList.Add($"--profile={options.HardwareProfile}");
        start.ArgumentList.Add($"--output={output}");
        if (options.CaptureOpenStages)
        {
            start.ArgumentList.Add("--capture-open-stages");
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start a SegmentCore startup worker.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var timedOut = false;
        string? terminationDiagnostic = null;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.WorkerTimeoutSeconds)))
        {
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                timedOut = true;
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                        // The exact worker exited between the HasExited check and termination request.
                    }
                    catch (Exception exception)
                    {
                        terminationDiagnostic =
                            $"Exact worker process-tree termination failed: {exception.GetType().Name}: {exception.Message}";
                    }
                }

                if (!process.HasExited)
                {
                    using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try
                    {
                        await process.WaitForExitAsync(killTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (killTimeout.IsCancellationRequested)
                    {
                        terminationDiagnostic = string.Join(
                            " ",
                            new[]
                            {
                                terminationDiagnostic,
                                "Exact worker did not exit within 10 seconds of the termination request."
                            }.Where(static text => !string.IsNullOrWhiteSpace(text)));
                    }
                }
            }
        }

        if (!process.HasExited)
        {
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
        }

        var outputText = await CaptureRedirectedOutputAsync(stdout, "stdout").ConfigureAwait(false);
        var errorText = await CaptureRedirectedOutputAsync(stderr, "stderr").ConfigureAwait(false);
        if (timedOut)
        {
            return new StartupSample
            {
                Mode = mode,
                CatalogCondition = mode,
                Iteration = iteration,
                ProcessId = process.Id,
                Records = options.Records,
                ExitCode = process.HasExited ? process.ExitCode : -1,
                TimedOut = true,
                Error = $"Worker process {process.Id} exceeded the bounded "
                    + $"{options.WorkerTimeoutSeconds}-second timeout. "
                    + (process.HasExited ? "The exact worker process tree was terminated." : "The exact worker remains unconfirmed as exited.")
                    + (terminationDiagnostic is null ? string.Empty : $" {terminationDiagnostic}"),
                ProcessOutput = string.Join(Environment.NewLine, new[] { outputText, errorText, terminationDiagnostic }
                    .Where(static text => !string.IsNullOrWhiteSpace(text)))
            };
        }

        if (!File.Exists(output))
        {
            return new StartupSample
            {
                Mode = mode,
                CatalogCondition = mode,
                Iteration = iteration,
                ProcessId = process.Id,
                Records = options.Records,
                ExitCode = process.ExitCode,
                Error = "Worker exited without writing a result. "
                    + string.Join(Environment.NewLine, new[] { outputText, errorText }
                        .Where(static text => !string.IsNullOrWhiteSpace(text)))
            };
        }

        var result = JsonSerializer.Deserialize<StartupSample>(
            await File.ReadAllTextAsync(output).ConfigureAwait(false))
            ?? throw new InvalidDataException($"Worker '{output}' produced an empty result.");
        result.ExitCode = process.ExitCode;
        result.ProcessOutput = string.Join(Environment.NewLine, new[] { outputText, errorText }
            .Where(static text => !string.IsNullOrWhiteSpace(text)));
        return result;
    }

    private static async Task<string> CaptureRedirectedOutputAsync(Task<string> reader, string streamName)
    {
        try
        {
            return await reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return $"<{streamName} unavailable: {exception.GetType().Name}: {exception.Message}>";
        }
    }

    private static void ValidateSamples(StartupReport report)
    {
        if (report.ColdCatalogState.CatalogFileCount != 0)
        {
            report.Failures.Add(
                $"Cold catalog state retained {report.ColdCatalogState.CatalogFileCount:N0} catalog files.");
        }

        if (report.ValidWarmCatalogState.CatalogFileCount == 0
            || report.ValidWarmCatalogState.CatalogBytes == 0)
        {
            report.Failures.Add("Valid warm catalog preparation did not produce a persisted catalog artifact.");
        }

        var expectedSamples = checked(report.Options.Iterations * 3);
        if (report.Samples.Count != expectedSamples)
        {
            report.Failures.Add($"Workers produced {report.Samples.Count:N0}/{expectedSamples:N0} samples.");
        }

        var expectedModes = new[] { RawMode, ColdRepositoryMode, WarmRepositoryMode };
        for (var iteration = 0; iteration < report.Options.Iterations; iteration++)
        {
            foreach (var mode in expectedModes)
            {
                var matches = report.Samples.Count(sample =>
                    sample.Iteration == iteration
                    && string.Equals(sample.Mode, mode, StringComparison.Ordinal));
                if (matches != 1)
                {
                    report.Failures.Add(
                        $"Expected exactly one {mode} sample for iteration {iteration}, found {matches:N0}.");
                }
            }
        }

        foreach (var sample in report.Samples)
        {
            if (!expectedModes.Contains(sample.Mode, StringComparer.Ordinal)
                || sample.Iteration < 0
                || sample.Iteration >= report.Options.Iterations)
            {
                report.Failures.Add(
                    $"Unexpected sample coordinate {sample.Mode} iteration {sample.Iteration}.");
            }

            if (sample.Records != report.Options.Records)
            {
                report.Failures.Add(
                    $"{sample.Mode} iteration {sample.Iteration} reported "
                    + $"{sample.Records:N0}/{report.Options.Records:N0} configured records.");
            }

            if (!sample.Passed || sample.ExitCode != 0)
            {
                report.Failures.Add(
                    $"{sample.Mode} iteration {sample.Iteration} failed: {sample.Error ?? $"exit {sample.ExitCode}"}");
                continue;
            }

            if (sample.ActivatedEntries != report.Options.Records)
            {
                report.Failures.Add(
                    $"{sample.Mode} iteration {sample.Iteration} activated "
                    + $"{sample.ActivatedEntries:N0}/{report.Options.Records:N0} entries.");
            }

            if (IsRepositoryMode(sample.Mode)
                && (sample.FirstMaterializedObjectChecksum != report.ExpectedFirstRecordChecksum
                    || !string.Equals(sample.MaterializationContract, MaterializationContract, StringComparison.Ordinal)))
            {
                report.Failures.Add(
                    $"Repository iteration {sample.Iteration} did not satisfy {MaterializationContract}.");
            }

            if (IsRepositoryMode(sample.Mode)
                && sample.DeserializedObjectsAfterFirstMaterializedObject
                    != sample.InitializationDeserializedObjects + 1)
            {
                report.Failures.Add(
                    $"{sample.Mode} iteration {sample.Iteration} did not deserialize exactly one object "
                    + "for the first materialized read after initialization.");
            }

            if (IsRepositoryMode(sample.Mode)
                && (sample.SameLifetimeRepeatedInitializeDeserializedObjects != 0
                    || !sample.SameLifetimeRepeatedInitializeRemainedInitialized
                    || !sample.SameLifetimeRepeatedInitializeStatusUnchanged))
            {
                report.Failures.Add(
                    $"{sample.Mode} iteration {sample.Iteration} repeated initialization was not "
                    + "a state-preserving, zero-deserialization fast path.");
            }

            if (string.Equals(sample.Mode, ColdRepositoryMode, StringComparison.Ordinal)
                && sample.InitializationDeserializedObjects != report.Options.Records)
            {
                report.Failures.Add(
                    $"Cold/missing-catalog iteration {sample.Iteration} deserialized "
                    + $"{sample.InitializationDeserializedObjects:N0}/{report.Options.Records:N0} objects while rebuilding indexes.");
            }

            if (string.Equals(sample.Mode, WarmRepositoryMode, StringComparison.Ordinal)
                && sample.InitializationDeserializedObjects != 0)
            {
                report.Failures.Add(
                    $"Valid-warm-catalog iteration {sample.Iteration} unexpectedly deserialized "
                    + $"{sample.InitializationDeserializedObjects:N0} payload objects during initialization.");
            }
        }

        var warmSamples = report.Samples
            .Where(static sample => sample.Mode == WarmRepositoryMode)
            .ToArray();
        report.ValidWarmCatalogState.VerifiedAcceptedWithoutPayloadDeserialization =
            warmSamples.Length == report.Options.Iterations
            && Enumerable.Range(0, report.Options.Iterations).All(iteration =>
                warmSamples.Count(sample =>
                    sample.Iteration == iteration
                    && sample.Passed
                    && sample.ExitCode == 0
                    && sample.InitializationDeserializedObjects == 0) == 1);
    }

    private static List<StartupMetricSummary> CreateSummaries(IReadOnlyList<StartupSample> samples)
    {
        var passed = samples.Where(static sample => sample.Passed && sample.ExitCode == 0).ToArray();
        var summaries = new List<StartupMetricSummary>();
        AddSummary(summaries, "raw-store activation", passed.Where(static sample => sample.Mode == RawMode),
            static sample => sample.RawStoreActivationMilliseconds);
        AddSummary(summaries, "cold/missing-catalog full IRepository initialization", passed.Where(static sample => sample.Mode == ColdRepositoryMode),
            static sample => sample.FullRepositoryInitializationMilliseconds);
        AddSummary(summaries, "cold/missing-catalog first fully materialized object after initialization", passed.Where(static sample => sample.Mode == ColdRepositoryMode),
            static sample => sample.FirstMaterializedObjectMilliseconds);
        AddSummary(summaries, "cold/missing-catalog time to first fully materialized object", passed.Where(static sample => sample.Mode == ColdRepositoryMode),
            static sample => sample.TimeToFirstMaterializedObjectMilliseconds);
        AddSummary(summaries, "valid-warm-catalog full IRepository initialization", passed.Where(static sample => sample.Mode == WarmRepositoryMode),
            static sample => sample.FullRepositoryInitializationMilliseconds);
        AddSummary(summaries, "valid-warm-catalog first fully materialized object after initialization", passed.Where(static sample => sample.Mode == WarmRepositoryMode),
            static sample => sample.FirstMaterializedObjectMilliseconds);
        AddSummary(summaries, "valid-warm-catalog time to first fully materialized object", passed.Where(static sample => sample.Mode == WarmRepositoryMode),
            static sample => sample.TimeToFirstMaterializedObjectMilliseconds);
        AddSummary(summaries, "same-lifetime repeated IRepository.InitializeAsync", passed.Where(static sample =>
                sample.Mode == ColdRepositoryMode || sample.Mode == WarmRepositoryMode),
            static sample => sample.SameLifetimeRepeatedInitializeMilliseconds);
        return summaries;
    }

    private static void AddSummary(
        ICollection<StartupMetricSummary> summaries,
        string metric,
        IEnumerable<StartupSample> samples,
        Func<StartupSample, double> selector)
    {
        var values = samples.Select(selector).Order().ToArray();
        if (values.Length == 0)
        {
            return;
        }

        summaries.Add(new StartupMetricSummary
        {
            Metric = metric,
            Samples = values.Length,
            MinimumMilliseconds = values[0],
            MeanMilliseconds = values.Average(),
            P50Milliseconds = Percentile(values, 0.50),
            P95Milliseconds = Percentile(values, 0.95),
            MaximumMilliseconds = values[^1]
        });
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        var rank = (ordered.Count - 1) * percentile;
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return ordered[lower];
        }

        return ordered[lower] + ((ordered[upper] - ordered[lower]) * (rank - lower));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void RemoveCatalogFiles(string ownedRunRoot, string storeRoot)
    {
        var ownedPrefix = Path.GetFullPath(ownedRunRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var store = Path.GetFullPath(storeRoot);
        if (!store.StartsWith(ownedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to alter unexpected catalog root '{store}'.");
        }

        foreach (var path in Directory.EnumerateFiles(store, "*.idbsidx", SearchOption.AllDirectories))
        {
            var target = Path.GetFullPath(path);
            if (!target.StartsWith(
                    store.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Refusing to remove unexpected catalog file '{target}'.");
            }

            File.Delete(target);
        }
    }

    private static async Task<CatalogStateReport> CaptureCatalogStateAsync(
        string storeRoot,
        string condition)
    {
        var files = new List<CatalogFileReport>();
        foreach (var path in Directory.EnumerateFiles(storeRoot, "*.idbsidx", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(storeRoot, path), StringComparer.Ordinal))
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var contentHash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
            files.Add(new CatalogFileReport
            {
                RelativePath = Path.GetRelativePath(storeRoot, path).Replace(Path.DirectorySeparatorChar, '/'),
                Bytes = stream.Length,
                Sha256 = Convert.ToHexString(contentHash)
            });
        }

        var catalogDescription = string.Join(
            "\n",
            files.Select(static file => $"{file.RelativePath}|{file.Bytes}|{file.Sha256}"));
        return new CatalogStateReport
        {
            Condition = condition,
            StoreFingerprint = await FingerprintDirectoryAsync(storeRoot).ConfigureAwait(false),
            CatalogFileCount = files.Count,
            CatalogBytes = files.Sum(static file => file.Bytes),
            CatalogFingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(catalogDescription))),
            CatalogFiles = files
        };
    }

    private static async Task RequireFingerprintAsync(string root, string expected)
    {
        var actual = await FingerprintDirectoryAsync(root).ConfigureAwait(false);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Store clone '{root}' does not match the canonical persisted bytes.");
        }
    }

    private static async Task<string> FingerprintDirectoryAsync(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            hash.AppendData(BitConverter.GetBytes(new FileInfo(file).Length));
            await using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer.AsSpan(0, read));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(
                value,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new JsonStringEnumConverter() }
                })).ConfigureAwait(false);
    }

    private static void ForceGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string FormatMilliseconds(double value) =>
        value.ToString("F3", CultureInfo.InvariantCulture);

    private static void TryDeleteOwnedRoot(string ownedParent, string runRoot)
    {
        var parent = Path.GetFullPath(ownedParent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(runRoot);
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to clean unexpected startup qualification root '{target}'.");
        }

        try
        {
            Directory.Delete(target, recursive: true);
        }
        catch
        {
            // Cleanup must not replace the qualification result.
        }
    }

    private static string Required(string[] args, string name) =>
        PhaseTwoOptions.ParseString(args, name)
        ?? throw new ArgumentException($"Missing required option {name}.");

    private static int ParseInt(string[] args, string name, int fallback, int minimum, int maximum)
    {
        var text = PhaseTwoOptions.ParseString(args, name);
        var value = text is null ? fallback : int.Parse(text, CultureInfo.InvariantCulture);
        return value >= minimum && value <= maximum
            ? value
            : throw new ArgumentOutOfRangeException(name, $"Expected {minimum:N0} through {maximum:N0}.");
    }

    private static Idb.Libraries.Abstractions.Enums.StorageHardwareProfile ParseProfile(string value)
    {
        if (!Enum.TryParse<Idb.Libraries.Abstractions.Enums.StorageHardwareProfile>(
                value,
                ignoreCase: true,
                out var profile)
            || profile == Idb.Libraries.Abstractions.Enums.StorageHardwareProfile.Custom)
        {
            throw new ArgumentException("--profile must be Portable, LowMemory, HighThroughput, or WindowsNvme.");
        }

        return profile;
    }

    private static bool IsRepositoryMode(string mode) =>
        string.Equals(mode, ColdRepositoryMode, StringComparison.Ordinal)
        || string.Equals(mode, WarmRepositoryMode, StringComparison.Ordinal)
        || string.Equals(mode, CatalogPreparationMode, StringComparison.Ordinal);

    private sealed class CountingStartupSerializer : IObjectSerializer<IProductionGateRecord>
    {
        private readonly BinaryObjectSerializer<IProductionGateRecord> _inner = new();
        private int _deserializeCount;

        public int DeserializeCount => Volatile.Read(ref _deserializeCount);

        public byte[] Serialize(IObjectEnvelope<IProductionGateRecord> envalope) =>
            _inner.Serialize(envalope);

        public IObjectEnvelope<IProductionGateRecord> Deserialize(byte[] data)
        {
            Interlocked.Increment(ref _deserializeCount);
            return _inner.Deserialize(data);
        }

        public ValueTask SerializeAsync(
            IObjectEnvelope<IProductionGateRecord> envalope,
            Stream destination,
            CancellationToken cancellationToken = default) =>
            _inner.SerializeAsync(envalope, destination, cancellationToken);

        public async ValueTask<IObjectEnvelope<IProductionGateRecord>> DeserializeAsync(
            Stream source,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _deserializeCount);
            return await _inner.DeserializeAsync(source, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class OpenStageCollector : IDisposable
    {
        private readonly ConcurrentDictionary<string, double> _measurements = new(StringComparer.Ordinal);
        private readonly MeterListener _listener = new();

        public OpenStageCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, SegmentCoreMeterName, StringComparison.Ordinal)
                    && string.Equals(instrument.Name, OpenStageInstrumentName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>(OnMeasurement);
            _listener.Start();
        }

        public IReadOnlyDictionary<string, double> Snapshot() => _measurements
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

        public void Dispose() => _listener.Dispose();

        private void OnMeasurement(
            Instrument instrument,
            double measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state)
        {
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, "stage", StringComparison.Ordinal) && tag.Value is string stage)
                {
                    _measurements[stage] = measurement;
                    return;
                }
            }
        }
    }

    internal sealed record StartupOptions
    {
        public int Records { get; init; }
        public int Iterations { get; init; }
        public int BatchSize { get; init; }
        public Idb.Libraries.Abstractions.Enums.StorageHardwareProfile HardwareProfile { get; init; }
        public bool CaptureOpenStages { get; init; }
        public bool KeepData { get; init; }
        public int WorkerTimeoutSeconds { get; init; }
        public string OutputPath { get; init; } = string.Empty;

        public static StartupOptions Parse(string[] args)
        {
            var quick = args.Contains("--quick", StringComparer.OrdinalIgnoreCase);
            return new StartupOptions
            {
                Records = ParseInt(args, "--records", quick ? 1_000 : 100_000, 1, 10_000_000),
                Iterations = ParseInt(args, "--iterations", quick ? 3 : 5, 1, 100),
                BatchSize = ParseInt(args, "--batch-size", 1_000, 1, 10_000),
                HardwareProfile = ParseProfile(
                    PhaseTwoOptions.ParseString(args, "--profile") ?? "HighThroughput"),
                CaptureOpenStages = args.Contains("--capture-open-stages", StringComparer.OrdinalIgnoreCase),
                KeepData = args.Contains("--keep-data", StringComparer.OrdinalIgnoreCase),
                WorkerTimeoutSeconds = ParseInt(
                    args,
                    "--worker-timeout-seconds",
                    quick ? 60 : 300,
                    5,
                    3_600),
                OutputPath = PhaseTwoOptions.ParseString(args, "--output") ?? Path.Combine(
                    "artifacts",
                    "segment-core",
                    $"segment-core-startup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json")
            };
        }
    }

    internal sealed record StartupSample
    {
        public string Mode { get; init; } = string.Empty;
        public int Iteration { get; init; }
        public int ProcessId { get; init; }
        public int Records { get; init; }
        public string CatalogCondition { get; init; } = string.Empty;
        public int ActivatedEntries { get; set; }
        public bool Passed { get; set; }
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public string? MaterializationContract { get; init; }
        public double RawStoreActivationMilliseconds { get; set; }
        public long RawStoreActivationAllocatedBytes { get; set; }
        public double ServiceRegistrationMilliseconds { get; set; }
        public double ProviderBuildAndRepositoryResolutionMilliseconds { get; set; }
        public double RepositoryInitializeMilliseconds { get; set; }
        public double FullRepositoryInitializationMilliseconds { get; set; }
        public long FullRepositoryInitializationAllocatedBytes { get; set; }
        public int InitializationDeserializedObjects { get; set; }
        public double FirstMaterializedObjectMilliseconds { get; set; }
        public double TimeToFirstMaterializedObjectMilliseconds { get; set; }
        public long TimeToFirstMaterializedObjectAllocatedBytes { get; set; }
        public long FirstMaterializedObjectChecksum { get; set; }
        public int DeserializedObjectsAfterFirstMaterializedObject { get; set; }
        public double SameLifetimeRepeatedInitializeMilliseconds { get; set; }
        public long SameLifetimeRepeatedInitializeAllocatedBytes { get; set; }
        public int SameLifetimeRepeatedInitializeDeserializedObjects { get; set; }
        public bool SameLifetimeRepeatedInitializeRemainedInitialized { get; set; }
        public bool SameLifetimeRepeatedInitializeStatusUnchanged { get; set; }
        public IReadOnlyDictionary<string, double>? OpenStagesMilliseconds { get; set; }
        public string? Error { get; set; }
        public string? ProcessOutput { get; set; }
    }

    internal sealed record StartupMetricSummary
    {
        public string Metric { get; init; } = string.Empty;
        public int Samples { get; init; }
        public double MinimumMilliseconds { get; init; }
        public double MeanMilliseconds { get; init; }
        public double P50Milliseconds { get; init; }
        public double P95Milliseconds { get; init; }
        public double MaximumMilliseconds { get; init; }
    }

    internal sealed record StartupMeasurementDefinitions
    {
        public string RawStoreActivation { get; init; } =
            "Fresh worker process times SegmentCoreStore.OpenAsync at the physical store root. "
            + "It excludes process launch, InterfaceDB service/repository construction, and object reads.";
        public string FullRepositoryInitialization { get; init; } =
            "Fresh worker process times service registration, provider construction/resolution, and "
            + "IRepository.InitializeAsync with MetadataOnly caching and HydrateOnInitialize=false.";
        public string FirstMaterializedObject { get; init; } =
            "After initialization, QueryFirstByStorageKeyAsync creates one object and validates all six fields "
            + "inside the timed interval.";
        public string TimeToFirstMaterializedObject { get; init; } =
            "Cumulative service registration through the first fully materialized and validated object.";
        public string SameLifetimeRepeatedInitialize { get; init; } =
            "One additional IRepository.InitializeAsync call after the first object read in the same provider lifetime; "
            + "this verifies the singleton/initialized fast path performs zero deserialization and leaves provider "
            + "status unchanged. It is excluded from time-to-first-object.";
        public string CacheCondition { get; init; } =
            "Every sample uses a new process and a byte-identical clone of its cold/missing-catalog or "
            + "valid-warm-catalog canonical store. "
            + "The operating-system page cache is not evicted; process launch and CLI parsing are excluded.";
        public string CatalogConditions { get; init; } =
            "Cold samples deliberately remove the rebuildable search-index catalog and must deserialize every "
            + "payload to rebuild indexes. Valid-warm samples use a catalog produced or verified by one untimed "
            + "repository initialization and must deserialize zero payload objects during initialization.";
        public string PostWriteSeedCatalogObservation { get; init; } =
            "Before deliberate catalog removal, the report fingerprints the catalog left by normal seed-provider "
            + "disposal. The untimed preparation worker records whether that catalog was accepted without rebuilding.";
        public string Scope { get; init; } =
            "SegmentCore-only lifecycle qualification. No SQLite connection-open result is measured or compared.";
        public string WorkerTimeout { get; init; } =
            "The parent bounds each complete child-process lifetime, including process startup. On timeout it "
            + "terminates that exact worker process tree and records the process id and captured diagnostics.";
        public string OpenStageDiagnostics { get; init; } =
            "Synchronous MeterListener stage collection is disabled for headline measurements and is enabled only "
            + "with --capture-open-stages because the listener can perturb the measured startup path.";
        public string SerializerInstrumentation { get; init; } =
            "Repository samples use a DI-constructed wrapper around the normal BinaryObjectSerializer to prove the "
            + "number of materialized payloads. Each payload deserialization includes one Interlocked increment; "
            + "therefore cold rebuild timings include that small per-object instrumentation cost.";
    }

    internal sealed record StartupReport
    {
        public DateTimeOffset StartedUtc { get; init; }
        public DateTimeOffset CompletedUtc { get; set; }
        public string Runtime { get; init; } = string.Empty;
        public string OperatingSystem { get; init; } = string.Empty;
        public int ProcessorCount { get; init; }
        public StartupOptions Options { get; init; } = new();
        public StartupMeasurementDefinitions MeasurementDefinitions { get; init; } = new();
        public string RawAndColdCanonicalStoreFingerprint { get; set; } = string.Empty;
        public int RawAndColdCanonicalStoreFiles { get; set; }
        public long RawAndColdCanonicalStoreBytes { get; set; }
        public long ExpectedFirstRecordChecksum { get; set; }
        public CatalogStateReport PostWriteSeedCatalogState { get; set; } = new();
        public bool PostWriteSeedCatalogAcceptedWithoutRebuild { get; set; }
        public string PostWriteSeedCatalogInterpretation { get; set; } = string.Empty;
        public CatalogStateReport ColdCatalogState { get; set; } = new();
        public CatalogStateReport ValidWarmCatalogState { get; set; } = new();
        public StartupSample? WarmCatalogPreparation { get; set; }
        public List<StartupSample> Samples { get; init; } = [];
        public List<StartupMetricSummary> Summaries { get; set; } = [];
        public List<string> Failures { get; init; } = [];
        public string? DataRoot { get; set; }
        public bool Passed => Failures.Count == 0;
    }

    internal sealed record CatalogStateReport
    {
        public string Condition { get; init; } = string.Empty;
        public string StoreFingerprint { get; init; } = string.Empty;
        public int CatalogFileCount { get; init; }
        public long CatalogBytes { get; init; }
        public string CatalogFingerprint { get; init; } = string.Empty;
        public List<CatalogFileReport> CatalogFiles { get; init; } = [];
        public bool VerifiedAcceptedWithoutPayloadDeserialization { get; set; }
    }

    internal sealed record CatalogFileReport
    {
        public string RelativePath { get; init; } = string.Empty;
        public long Bytes { get; init; }
        public string Sha256 { get; init; } = string.Empty;
    }
}
