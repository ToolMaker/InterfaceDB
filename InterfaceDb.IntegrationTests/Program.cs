using System.Diagnostics;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extentions;
using Microsoft.Extensions.DependencyInjection;

if (args is ["--child", var dataRoot, var gatePath, var readyPath, var writerName])
{
    await RunChildWriterAsync(dataRoot, gatePath, readyPath, writerName).ConfigureAwait(false);
    return 0;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("single save cleans sidecar artifacts", SingleSaveCleansSidecarArtifactsAsync),
    ("two processes can write the same payload", TwoProcessesWriteSamePayloadAsync)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run().ConfigureAwait(false);
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex}");
        Console.WriteLine($"FAIL {test.Name}");
        Console.WriteLine(ex);
    }
}

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"{failures.Count} test(s) failed.");
    return 1;
}

Console.WriteLine("All integration tests passed.");
return 0;

static async Task SingleSaveCleansSidecarArtifactsAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var provider = BuildProvider(testRoot);
        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var record = new LockTestRecord { Id = "single", Value = "first-write" };
        await repository.SaveAsync(record).ConfigureAwait(false);

        Assert(record.Envelope is not null, "Saved record should have an envelope.");
        Assert(File.Exists(record.Envelope!.Location), "Payload file should exist after save.");
        AssertNoSidecarArtifacts(testRoot);

        var reloaded = await repository.QueryAsync(x => x.Id == "single").ConfigureAwait(false);
        reloaded = AssertNotNull(reloaded, "Saved record should be queryable.");
        Assert(reloaded.Value == "first-write", "Reloaded value should match saved value.");
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task TwoProcessesWriteSamePayloadAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        string payloadPath;
        await using (var seedProvider = BuildProvider(testRoot))
        {
            var seedRepository = seedProvider.GetRequiredService<IRepository<ILockTestRecord>>();
            await seedRepository.InitializeAsync().ConfigureAwait(false);

            var seed = new LockTestRecord { Id = "shared", Value = "seed" };
            await seedRepository.SaveAsync(seed).ConfigureAwait(false);
            payloadPath = seed.Envelope?.Location ?? throw new InvalidOperationException("Seed envelope location was not initialized.");
        }

        Assert(File.Exists(payloadPath), "Seed payload should exist before child writers start.");

        var gatePath = Path.Combine(testRoot, "start.gate");
        var readyA = Path.Combine(testRoot, "writer-a.ready");
        var readyB = Path.Combine(testRoot, "writer-b.ready");

        using var childA = StartChild(testRoot, gatePath, readyA, "writer-a");
        using var childB = StartChild(testRoot, gatePath, readyB, "writer-b");

        await WaitUntilAsync(
            () => File.Exists(readyA) && File.Exists(readyB),
            TimeSpan.FromSeconds(15),
            "Child writers did not become ready.").ConfigureAwait(false);

        await File.WriteAllTextAsync(gatePath, "go").ConfigureAwait(false);

        var outputA = await childA.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var errorA = await childA.StandardError.ReadToEndAsync().ConfigureAwait(false);
        var outputB = await childB.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var errorB = await childB.StandardError.ReadToEndAsync().ConfigureAwait(false);

        await childA.WaitForExitAsync().ConfigureAwait(false);
        await childB.WaitForExitAsync().ConfigureAwait(false);

        Assert(childA.ExitCode == 0, $"writer-a failed with exit code {childA.ExitCode}.{Environment.NewLine}{outputA}{Environment.NewLine}{errorA}");
        Assert(childB.ExitCode == 0, $"writer-b failed with exit code {childB.ExitCode}.{Environment.NewLine}{outputB}{Environment.NewLine}{errorB}");
        AssertNoSidecarArtifacts(testRoot);

        await using var verifyProvider = BuildProvider(testRoot);
        var verifyRepository = verifyProvider.GetRequiredService<IRepository<ILockTestRecord>>();
        await verifyRepository.InitializeAsync().ConfigureAwait(false);

        var reloaded = await verifyRepository.QueryAsync(x => x.Id == "shared").ConfigureAwait(false);
        reloaded = AssertNotNull(reloaded, "Shared record should still be readable after concurrent child writes.");
        Assert(
            reloaded.Value.StartsWith("writer-a:", StringComparison.Ordinal) ||
            reloaded.Value.StartsWith("writer-b:", StringComparison.Ordinal),
            $"Unexpected final value '{reloaded.Value}'.");

        var payloadFiles = Directory.GetFiles(testRoot, "*.mpk", SearchOption.AllDirectories);
        Assert(payloadFiles.Length == 1, $"Expected one payload file, found {payloadFiles.Length}.");
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task RunChildWriterAsync(
    string dataRoot,
    string gatePath,
    string readyPath,
    string writerName)
{
    await using var provider = BuildProvider(dataRoot);
    var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
    await repository.InitializeAsync().ConfigureAwait(false);

    var record = await repository.QueryAsync(x => x.Id == "shared").ConfigureAwait(false);
    record = AssertNotNull(record, $"{writerName} could not load the shared record.");

    Directory.CreateDirectory(Path.GetDirectoryName(readyPath)!);
    await File.WriteAllTextAsync(readyPath, writerName).ConfigureAwait(false);
    await WaitUntilAsync(
        () => File.Exists(gatePath),
        TimeSpan.FromSeconds(15),
        $"{writerName} timed out waiting for the start gate.").ConfigureAwait(false);

    record.Value = writerName + ":" + new string(writerName[^1], 4 * 1024 * 1024);
    await repository.SaveAsync(record).ConfigureAwait(false);
    Console.WriteLine($"{writerName} saved {record.Value.Length} bytes to {record.Envelope?.Location}");
}

static ServiceProvider BuildProvider(string dataRoot)
{
    var options = new LockTestOptions(dataRoot);
    var services = new ServiceCollection();
    services.AddRepository<ILockTestRecord>(options);
    return services.BuildServiceProvider();
}

static Process StartChild(
    string dataRoot,
    string gatePath,
    string readyPath,
    string writerName)
{
    var executable = Environment.ProcessPath
        ?? throw new InvalidOperationException("Unable to resolve current test executable path.");

    var startInfo = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.ArgumentList.Add("--child");
    startInfo.ArgumentList.Add(dataRoot);
    startInfo.ArgumentList.Add(gatePath);
    startInfo.ArgumentList.Add(readyPath);
    startInfo.ArgumentList.Add(writerName);

    return Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Failed to start child writer '{writerName}'.");
}

static string CreateTestRoot()
{
    var root = Path.Combine(Path.GetTempPath(), "InterfaceDb.IntegrationTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    return root;
}

static async Task WaitUntilAsync(
    Func<bool> condition,
    TimeSpan timeout,
    string failureMessage)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (condition())
        {
            return;
        }

        await Task.Delay(50).ConfigureAwait(false);
    }

    throw new TimeoutException(failureMessage);
}

static void AssertNoSidecarArtifacts(string root)
{
    var artifacts = Directory
        .EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path =>
        {
            var fileName = Path.GetFileName(path);
            return fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains(".tmp.", StringComparison.OrdinalIgnoreCase);
        })
        .ToArray();

    Assert(artifacts.Length == 0, "Expected no leftover sidecar artifacts, found: " + string.Join(", ", artifacts));
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static T AssertNotNull<T>(T? value, string message)
    where T : class
{
    if (value is null)
    {
        throw new InvalidOperationException(message);
    }

    return value;
}

static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch
    {
    }
}

public sealed class LockTestOptions : IDbContractConfigDto<ILockTestRecord>
{
    public LockTestOptions(string dataRoot)
    {
        DataRoot = dataRoot;
    }

    public string EnvelopeDICacheKey { get; } = "LockTest";
    public string ContractName { get; } = "LockTest";
    public string FileExtension { get; } = ".mpk";
    public string? ImplementationsPath { get; } = null;
    public string? ContractPath { get; } = null;
    public string? MigrationsPath { get; } = null;
    public string? DataRoot { get; }
    public string? ActiveVersion { get; } = "1.0.0";
    public IIndex<ILockTestRecord>[]? Indexes { get; } = Array.Empty<IIndex<ILockTestRecord>>();
    public IndexProvisioningModeType IndexProvisioningMode { get; } = IndexProvisioningModeType.None;
    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;
    public StoragePolicy? StoragePolicy { get; } = StoragePolicy.Default with
    {
        CacheEnvelopeOnWrite = true,
        HydrateOnInitialize = true,
        KeepObjectInstanceInCacheOnWrite = true,
        MaxConcurrency = 4,
        RaiseNotifications = true,
        UseSidecarWriteLocks = true,
        UseSingleWriteBuffer = true,
        UseWriterSemaphore = false,
        WatchStorageChanges = false,
        WriteLockTimeout = TimeSpan.FromSeconds(15),
        WriteLockRetryDelay = TimeSpan.FromMilliseconds(10),
        StaleWriteLockTimeout = TimeSpan.FromSeconds(30)
    };
}

[StorageScope(VersionTag = "1.0.0")]
public interface ILockTestRecord : IStoredContract<ILockTestRecord>
{
    string Id { get; set; }
    string Value { get; set; }
}

public sealed class LockTestRecord : ILockTestRecord
{
    public string Id { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public IObjectEnvelope<ILockTestRecord>? Envelope { get; private set; }
}
