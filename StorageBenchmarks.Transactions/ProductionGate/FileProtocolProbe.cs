using System.Diagnostics;
using System.Globalization;
using System.Text;

internal static class FileProtocolProbe
{
    public static async Task RunAsync(string[] args)
    {
        var iterations = ParsePositiveInt(args, "--probe-records", 500);
        var payloadSize = ParsePositiveInt(args, "--probe-payload-bytes", 1_024);
        var root = Path.Combine(
            Path.GetTempPath(),
            "InterfaceDbFileProtocolProbe",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var payload = new byte[payloadSize];
        Random.Shared.NextBytes(payload);
        var metadata = Encoding.UTF8.GetBytes(
            $"ownerId={Guid.NewGuid():N}{Environment.NewLine}machine={Environment.MachineName}"
            + $"{Environment.NewLine}processId={Environment.ProcessId}{Environment.NewLine}");

        Console.WriteLine("InterfaceDB file-protocol probe");
        Console.WriteLine($"Root: {root}");
        Console.WriteLine($"Records: {iterations:N0}; payload: {payloadSize:N0} bytes");
        Console.WriteLine();

        try
        {
            var results = new List<ProbeResult>
            {
                await MeasureAsync(
                    "new canonical file, buffered",
                    iterations,
                    index => WriteCanonicalAsync(
                        root,
                        "buffered",
                        index,
                        payload,
                        writeThrough: false,
                        explicitFlush: false)),
                await MeasureAsync(
                    "new canonical file, write-through only",
                    iterations,
                    index => WriteCanonicalAsync(
                        root,
                        "write-through",
                        index,
                        payload,
                        writeThrough: true,
                        explicitFlush: false)),
                await MeasureAsync(
                    "new canonical file, explicit flush only",
                    iterations,
                    index => WriteCanonicalAsync(
                        root,
                        "explicit-flush",
                        index,
                        payload,
                        writeThrough: false,
                        explicitFlush: true)),
                await MeasureAsync(
                    "new canonical file, write-through + flush",
                    iterations,
                    index => WriteCanonicalAsync(
                        root,
                        "write-through-flush",
                        index,
                        payload,
                        writeThrough: true,
                        explicitFlush: true)),
                await MeasureReusedAppendAsync(
                    root,
                    iterations,
                    payload,
                    writeThrough: false,
                    explicitFlush: true),
                await MeasureReusedAppendAsync(
                    root,
                    iterations,
                    payload,
                    writeThrough: true,
                    explicitFlush: false),
                await MeasureReusedAppendAsync(
                    root,
                    iterations,
                    payload,
                    writeThrough: true,
                    explicitFlush: true),
                await MeasureAsync(
                    "proposal + write-through + two renames",
                    iterations,
                    index => WriteProposalAsync(
                        root,
                        "proposal-write-through",
                        index,
                        payload,
                        writeThrough: true,
                        explicitFlush: false)),
                await MeasureAsync(
                    "proposal + explicit flush + two renames",
                    iterations,
                    index => WriteProposalAsync(
                        root,
                        "proposal-explicit-flush",
                        index,
                        payload,
                        writeThrough: false,
                        explicitFlush: true)),
                await MeasureAsync(
                    "proposal + flush + two renames",
                    iterations,
                    index => WriteProposalAsync(root, "proposal", index, payload)),
                await MeasureAsync(
                    "writer lease + proposal protocol",
                    iterations,
                    index => WriteWithWriterLeaseAsync(root, index, payload)),
                await MeasureAsync(
                    "writer lease + sidecar + proposal protocol",
                    iterations,
                    index => WriteFullProtocolAsync(root, index, payload, metadata)),
                await MeasureAsync(
                    "ephemeral locks + write-through proposal",
                    iterations,
                    index => WriteCandidateProtocolAsync(
                        root,
                        "candidate-write-through",
                        index,
                        payload,
                        metadata,
                        writeThrough: true,
                        explicitFlush: false)),
                await MeasureAsync(
                    "ephemeral locks + explicitly flushed proposal",
                    iterations,
                    index => WriteCandidateProtocolAsync(
                        root,
                        "candidate-explicit-flush",
                        index,
                        payload,
                        metadata,
                        writeThrough: false,
                        explicitFlush: true))
            };

            Console.WriteLine("| Protocol | p50 ms | p95 ms | p99 ms | operations/s |");
            Console.WriteLine("|---|---:|---:|---:|---:|");
            foreach (var result in results)
            {
                Console.WriteLine(
                    $"| {result.Name} | {result.P50:N3} | {result.P95:N3} | "
                    + $"{result.P99:N3} | {result.OperationsPerSecond:N1} |");
            }
        }
        finally
        {
            TryDeleteProbeRoot(root);
        }
    }

    private static async ValueTask<ProbeResult> MeasureAsync(
        string name,
        int iterations,
        Func<int, ValueTask> operation)
    {
        const int warmupIterations = 10;
        for (var index = 0; index < warmupIterations; index++)
        {
            await operation(index).ConfigureAwait(false);
        }

        var samples = new double[iterations];
        var totalStarted = Stopwatch.GetTimestamp();
        for (var index = 0; index < iterations; index++)
        {
            var started = Stopwatch.GetTimestamp();
            await operation(index + warmupIterations).ConfigureAwait(false);
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        var elapsed = Stopwatch.GetElapsedTime(totalStarted);
        Array.Sort(samples);
        return new ProbeResult(
            name,
            ProductionStressRunner.Percentile(samples, 0.50),
            ProductionStressRunner.Percentile(samples, 0.95),
            ProductionStressRunner.Percentile(samples, 0.99),
            iterations / elapsed.TotalSeconds);
    }

    private static async ValueTask<ProbeResult> MeasureReusedAppendAsync(
        string root,
        int iterations,
        byte[] payload,
        bool writeThrough,
        bool explicitFlush)
    {
        var path = Path.Combine(
            root,
            $"reused-append-{writeThrough}-{explicitFlush}.bin");
        var options = FileOptions.Asynchronous | FileOptions.SequentialScan;
        if (writeThrough)
        {
            options |= FileOptions.WriteThrough;
        }

        using var handle = File.OpenHandle(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            options: options);
        long offset = 0;
        return await MeasureAsync(
            $"reused append file, {(writeThrough ? "write-through" : "buffered")}"
            + (explicitFlush ? " + flush" : " only"),
            iterations,
            async _ =>
            {
                await RandomAccess.WriteAsync(handle, payload, offset).ConfigureAwait(false);
                if (explicitFlush)
                {
                    RandomAccess.FlushToDisk(handle);
                }

                offset += payload.Length;
            }).ConfigureAwait(false);
    }

    private static async ValueTask WriteCanonicalAsync(
        string root,
        string group,
        int index,
        byte[] payload,
        bool writeThrough,
        bool explicitFlush)
    {
        var directory = Path.Combine(root, group);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"row-{index:D8}.bin");
        var options = FileOptions.Asynchronous | FileOptions.SequentialScan;
        if (writeThrough)
        {
            options |= FileOptions.WriteThrough;
        }

        using var handle = File.OpenHandle(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            options: options);
        await RandomAccess.WriteAsync(handle, payload, 0).ConfigureAwait(false);
        if (explicitFlush)
        {
            RandomAccess.FlushToDisk(handle);
        }
    }

    private static async ValueTask WriteProposalAsync(
        string root,
        string group,
        int index,
        byte[] payload,
        bool writeThrough = true,
        bool explicitFlush = true)
    {
        var directory = Path.Combine(root, group);
        Directory.CreateDirectory(directory);
        var payloadPath = Path.Combine(directory, $"row-{index:D8}.bin");
        _ = Directory.EnumerateFiles(
                directory,
                Path.GetFileName(payloadPath) + ".pending.*.ready",
                SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var writingPath = payloadPath + ".pending." + Guid.NewGuid().ToString("N") + ".writing";
        var options = FileOptions.Asynchronous | FileOptions.SequentialScan;
        if (writeThrough)
        {
            options |= FileOptions.WriteThrough;
        }

        using (var handle = File.OpenHandle(
            writingPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            options: options))
        {
            await RandomAccess.WriteAsync(handle, payload, 0).ConfigureAwait(false);
            if (explicitFlush)
            {
                RandomAccess.FlushToDisk(handle);
            }
        }

        var readyPath = payloadPath + ".pending."
            + DateTimeOffset.UtcNow.UtcTicks.ToString("D19", CultureInfo.InvariantCulture)
            + "." + Guid.NewGuid().ToString("N") + ".ready";
        File.Move(writingPath, readyPath);
        File.Move(readyPath, payloadPath);
    }

    private static async ValueTask WriteWithWriterLeaseAsync(
        string root,
        int index,
        byte[] payload)
    {
        var migrationLock = Path.Combine(root, ".idb-migration.lock");
        _ = File.Exists(migrationLock);
        var writerPath = Path.Combine(root, ".idb-writer." + Guid.NewGuid().ToString("N") + ".lock");
        await using (var writer = new FileStream(
            writerPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough))
        {
            await writer.FlushAsync().ConfigureAwait(false);
            _ = File.Exists(migrationLock);
            await WriteProposalAsync(root, "writer-lease", index, payload).ConfigureAwait(false);
        }

        File.Delete(writerPath);
    }

    private static async ValueTask WriteFullProtocolAsync(
        string root,
        int index,
        byte[] payload,
        byte[] metadata)
    {
        var migrationLock = Path.Combine(root, ".idb-migration.lock");
        _ = File.Exists(migrationLock);
        var writerPath = Path.Combine(root, ".idb-writer." + Guid.NewGuid().ToString("N") + ".lock");
        await using (var writer = new FileStream(
            writerPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough))
        {
            await writer.FlushAsync().ConfigureAwait(false);
            _ = File.Exists(migrationLock);
            var payloadPath = Path.Combine(root, "full", $"row-{index:D8}.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
            var sidecarPath = payloadPath + ".lock";
            await using (var sidecar = new FileStream(
                sidecarPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 256,
                FileOptions.WriteThrough))
            {
                await sidecar.WriteAsync(metadata).ConfigureAwait(false);
                await sidecar.FlushAsync().ConfigureAwait(false);
                await WriteProposalAsync(root, "full", index, payload).ConfigureAwait(false);
            }

            File.Delete(sidecarPath);
        }

        File.Delete(writerPath);
    }

    private static async ValueTask WriteCandidateProtocolAsync(
        string root,
        string group,
        int index,
        byte[] payload,
        byte[] metadata,
        bool writeThrough,
        bool explicitFlush)
    {
        var migrationLock = Path.Combine(root, ".idb-migration.lock");
        _ = File.Exists(migrationLock);
        var writerPath = Path.Combine(root, ".candidate-writer." + Guid.NewGuid().ToString("N") + ".lock");
        await using (var writer = new FileStream(
            writerPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.None))
        {
            _ = File.Exists(migrationLock);
            var directory = Path.Combine(root, group);
            Directory.CreateDirectory(directory);
            var sidecarPath = Path.Combine(directory, $"row-{index:D8}.bin.lock");
            await using (var sidecar = new FileStream(
                sidecarPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 256,
                FileOptions.Asynchronous))
            {
                await sidecar.WriteAsync(metadata).ConfigureAwait(false);
                await WriteProposalAsync(
                    root,
                    group,
                    index,
                    payload,
                    writeThrough,
                    explicitFlush).ConfigureAwait(false);
            }

            File.Delete(sidecarPath);
        }

        File.Delete(writerPath);
    }

    private static int ParsePositiveInt(string[] args, string name, int fallback)
    {
        var value = ProductionGateOptions.ParseString(args, name);
        if (value is null)
        {
            return fallback;
        }

        var parsed = int.Parse(value, CultureInfo.InvariantCulture);
        return parsed > 0 ? parsed : throw new ArgumentOutOfRangeException(name);
    }

    private static void TryDeleteProbeRoot(string root)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "InterfaceDbFileProtocolProbe"));
            if (fullRoot.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(fullRoot))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ProbeResult(
        string Name,
        double P50,
        double P95,
        double P99,
        double OperationsPerSecond);
}
