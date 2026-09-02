using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Dapper;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;
using Idb.Libraries.Implementation.Indexes;
using Idb.Libraries.Implementation.Repositories;
using Idb.Libraries.Implementation.Serialization;
using IdbDemoStorage.Options;
using MessagePack;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PerformanceCommon;
using System.Data;
using ContractPerson = MigrationTool.Contracts.Person.v1_0_0;
using PersonStorage = IdbDemoStorage.Storage.Person.v1_0_0;

try
{
    if (args.Any(argument => string.Equals(argument, "--segment-core-hash-routing", StringComparison.OrdinalIgnoreCase)))
    {
        SegmentCoreHashRoutingBenchmarkRunner.RunAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--segment-core-read-worker", StringComparison.OrdinalIgnoreCase)))
    {
        SegmentCoreReadProcessQualificationRunner.RunWorkerAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--segment-core-startup-worker", StringComparison.OrdinalIgnoreCase)))
    {
        SegmentCoreStartupQualificationRunner.RunWorkerAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--segment-core-startup", StringComparison.OrdinalIgnoreCase)))
    {
        SegmentCoreStartupQualificationRunner.RunAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--segment-core-read-process", StringComparison.OrdinalIgnoreCase)))
    {
        SegmentCoreReadProcessQualificationRunner.RunAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--segment-core-qualification", StringComparison.OrdinalIgnoreCase)))
    {
        SegmentCoreQualificationRunner.RunAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--segment-core-upgrade-validate", StringComparison.OrdinalIgnoreCase)))
    {
        SegmentCoreUpgradeQualificationRunner.RunAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--segment-core-production-pilot", StringComparison.OrdinalIgnoreCase)))
    {
        SegmentCoreProductionPilotRunner.RunAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--segment-core-power-loss-writer", StringComparison.OrdinalIgnoreCase)))
    {
        SegmentCorePowerLossQualificationRunner.RunWriterAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--segment-core-power-loss-validate", StringComparison.OrdinalIgnoreCase)))
    {
        SegmentCorePowerLossQualificationRunner.RunValidatorAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--segment-core-scale", StringComparison.OrdinalIgnoreCase)))
    {
        SegmentCoreScaleGateRunner.RunAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--segment-core-crash-writer", StringComparison.OrdinalIgnoreCase)))
    {
        SegmentCoreTransactionsRunner.RunCrashWriterAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--segment-core-transactions", StringComparison.OrdinalIgnoreCase)))
    {
        SegmentCoreTransactionsRunner.RunAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--segment-core-limits", StringComparison.OrdinalIgnoreCase)))
    {
        SegmentCoreLimitsRunner.RunAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--phase-two-crash-writer", StringComparison.OrdinalIgnoreCase)))
    {
        PhaseTwoRunner.RunCrashWriterAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--phase-two", StringComparison.OrdinalIgnoreCase)))
    {
        PhaseTwoRunner.RunAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--phase-one-crash-writer", StringComparison.OrdinalIgnoreCase)))
    {
        PhaseOneRunner.RunCrashWriterAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--phase-one", StringComparison.OrdinalIgnoreCase)))
    {
        PhaseOneRunner.RunAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--phase-zero-crash-writer", StringComparison.OrdinalIgnoreCase)))
    {
        PhaseZeroRunner.RunCrashWriterAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--phase-zero", StringComparison.OrdinalIgnoreCase)))
    {
        PhaseZeroRunner.RunAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--file-protocol-probe", StringComparison.OrdinalIgnoreCase)))
    {
        FileProtocolProbe.RunAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--production-gate-crash-writer", StringComparison.OrdinalIgnoreCase)))
    {
        ProductionGateRunner.RunCrashWriterAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--production-gate", StringComparison.OrdinalIgnoreCase)))
    {
        ProductionGateRunner.RunAsync(args).GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--materialization-self-test", StringComparison.OrdinalIgnoreCase)))
    {
        BenchmarkMaterializationSelfTest.RunAsync().GetAwaiter().GetResult();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--planner-validate", StringComparison.OrdinalIgnoreCase)))
    {
        QueryPlannerBenchmarks.Validate();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--planner-quick", StringComparison.OrdinalIgnoreCase)))
    {
        QueryPlannerBenchmarks.RunQuickComparison();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--planner-benchmark", StringComparison.OrdinalIgnoreCase)))
    {
        args = args
            .Where(argument => !string.Equals(argument, "--planner-benchmark", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        BenchmarkRunner.Run<QueryPlannerBenchmarks>(args: args);
        return;
    }

    TransactionBenchmarks.EnsureConfigurationInitialized();
    if (args.Any(argument => string.Equals(argument, "--smoke-sql", StringComparison.OrdinalIgnoreCase)))
    {
        TransactionBenchmarks.SmokeTestSqlConnection();
        return;
    }

    if (TransactionBenchmarks.TryGetProfileTarget(args, out var profileTarget))
    {
        TransactionBenchmarks.RunProfileTarget(profileTarget);
        return;
    }

    if (args.Length == 0)
    {
        TransactionBenchmarks.PrintUsage();
        return;
    }

    if (args.Any(argument => string.Equals(argument, "--run-all", StringComparison.OrdinalIgnoreCase)))
    {
        args = args
            .Where(argument => !string.Equals(argument, "--run-all", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    BenchmarkRunner.Run<TransactionBenchmarks>(args: args);
}
catch (Exception ex)
{
    Console.Error.WriteLine("StorageBenchmarks.Transactions failed.");
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}

public readonly record struct BenchmarkQueryDigest(int Count, ulong HashSum, ulong HashXor)
{
    public BenchmarkQueryDigest Combine(BenchmarkQueryDigest other, int queryOrdinal)
        => new(
            checked(Count + other.Count),
            unchecked(
                (HashSum * 1099511628211UL)
                ^ other.HashSum
                ^ ((ulong)(uint)(queryOrdinal + 1) * 11400714819323198485UL)),
            unchecked(
                ((HashXor << 17) | (HashXor >> 47))
                ^ other.HashXor
                ^ ((ulong)(uint)other.Count << 32)
                ^ (uint)(queryOrdinal + 1)));
}

[MemoryDiagnoser]
public class TransactionBenchmarks : IDisposable
{
    private const int DefaultBenchmarkPersonCount = 25_000;
    private const int QueryRepeatCount = 100;
    private const int FileSystemBenchmarkConcurrency = 16;
    private const int FileSystemBenchmarkBulkBatchSize = 4096;
    private const string PersonCountEnvironmentVariable = "INTERFACEDB_BENCH_PERSON_COUNT";
    private const string MaxConcurrencyEnvironmentVariable = "INTERFACEDB_BENCH_MAX_CONCURRENCY";
    private const string BulkBatchSizeEnvironmentVariable = "INTERFACEDB_BENCH_BULK_BATCH_SIZE";
    private const string UseSingleWriteBufferEnvironmentVariable = "INTERFACEDB_BENCH_SINGLE_WRITE_BUFFER";
    private readonly int _benchmarkPersonCount = ResolvePositiveInt(
        PersonCountEnvironmentVariable,
        DefaultBenchmarkPersonCount);
    private readonly List<ContractPerson.IPerson> _seedPersons;
    private readonly List<IIndexedBenchmarkPerson> _seedIndexedPersons;
    private readonly BenchmarkQueryDigest _expectedQueryBatch;
    private readonly BenchmarkQueryDigest _expectedSelectiveQueryBatch;
    private static readonly string[] QueryRoles = { "Student", "Employee" };
    private static readonly string[] BenchmarkFirstNames = { "Alex", "Jordan", "Taylor", "Casey", "Morgan", "Riley", "Sydney", "Robin" };
    private static readonly string[] BenchmarkDepartments = { "Engineering", "Finance", "Marketing", "Operations", "Research" };
    private static readonly string[] BenchmarkCourses = { "Mathematics", "Physics", "Computer Science", "History", "Economics" };

    private ServiceProvider? _fileProvider;
    private ServiceProvider? _indexedFileProvider;
    private string? _fileDataRoot;
    private string? _indexedFileDataRoot;
    private SqlConnection? _sqlConnection;
    private SqlConnection? _sqlIndexedConnection;
    private SqlConnection? _sqlBulkConnection;
    private SqlConnection? _sqlQueryConnection;
    private SqlConnection? _dapperConnection;
    private SqlConnection? _dapperIndexedConnection;
    private SqlConnection? _dapperQueryConnection;
    private DbContextOptions<TransactionBenchDbContext>? _efOptions;
    private DbContextOptions<TransactionBenchDbContext>? _efIndexedOptions;
    private DbContextOptions<TransactionBenchDbContext>? _efQueryOptions;
    private List<ContractPerson.IPerson>? _iterationPersons;
    private List<IIndexedBenchmarkPerson>? _iterationIndexedPersons;
    private IRepository<ContractPerson.IPerson>? _queryRepository;
    private IRepository<IIndexedBenchmarkPerson>? _indexedQueryRepository;

    internal static IConfiguration? Configuration { get; set; }

    public TransactionBenchmarks()
    {
        _seedPersons = CreateDeterministicPersons(_benchmarkPersonCount);
        _seedIndexedPersons = CloneIndexedPersons(_seedPersons);
        EnsureQueryDigest(
            "indexed seed projection",
            DigestPersons(_seedPersons),
            DigestIndexedPersons(_seedIndexedPersons));
        _expectedQueryBatch = ExpectedQueryBatch();
        _expectedSelectiveQueryBatch = ExpectedSelectiveQueryBatch();
    }

    public static void PrintUsage()
    {
        Console.WriteLine("InterfaceDB transaction benchmark runner");
        Console.WriteLine();
        Console.WriteLine("Run a filtered benchmark:");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --filter *FileSystem*");
        Console.WriteLine();
        Console.WriteLine("Run the full benchmark suite explicitly:");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --run-all");
        Console.WriteLine();
        Console.WriteLine("Current workload defaults:");
        Console.WriteLine("  INTERFACEDB_BENCH_PERSON_COUNT=25000");
        Console.WriteLine("  INTERFACEDB_BENCH_MAX_CONCURRENCY=16");
        Console.WriteLine("  INTERFACEDB_BENCH_BULK_BATCH_SIZE=16384");
        Console.WriteLine("  INTERFACEDB_BENCH_SINGLE_WRITE_BUFFER=true");
        Console.WriteLine();
        Console.WriteLine("Quick SQL configuration check:");
        Console.WriteLine("  dotnet run --project StorageBenchmarks.Transactions -- --smoke-sql");
        Console.WriteLine();
        Console.WriteLine("Run a direct profiling workload for dotnet-trace:");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --profile filesystem-store");
        Console.WriteLine();
        Console.WriteLine("Validate or benchmark the query planner:");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --planner-validate");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --planner-quick");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --planner-benchmark");
        Console.WriteLine();
        Console.WriteLine("Run the production benchmark, stress, correctness, and crash-recovery gate:");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --production-gate --quick");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --production-gate --records=10000,100000 --duration=00:30:00 --concurrency=1,4,16 --cache=strong,weak,metadata --durability=performance,durable");
        Console.WriteLine("  Add --include-sqlserver to include the configured SQL Server alongside InterfaceDB and SQLite.");
        Console.WriteLine("  Add --include-write-ablations to isolate search-index and sidecar-lock write costs.");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --materialization-self-test");
        Console.WriteLine();
        Console.WriteLine("Run the low-level durable file-protocol probe:");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --file-protocol-probe --probe-records=200 --probe-payload-bytes=262");
        Console.WriteLine();
        Console.WriteLine("Run the Phase 0 object-log correctness and admission gate:");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --phase-zero --quick");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --phase-zero --records=500 --payload-bytes=512 --iterations=9 --crash-cycles=20");
        Console.WriteLine();
        Console.WriteLine("Run the NVMe-striped append-log prototype:");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --phase-one --quick");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --phase-one --records=2000 --iterations=3 --payload-bytes=512,4096,65536 --concurrency=1,4,16,64 --stripes=1,2,4,8,16");
        Console.WriteLine();
        Console.WriteLine("Run the checkpointed primary-index/direct-read prototype:");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --phase-two --quick");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --phase-two --records=500 --iterations=3 --payload-bytes=512,4096,65536 --concurrency=1,16,64 --stripes=8,16");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --segment-core-transactions --records=5000 --iterations=3 --batch-sizes=1,10,100,1000");
        Console.WriteLine();
        Console.WriteLine("Run the SegmentCore scale and physical power-loss qualification gates:");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --segment-core-hash-routing --routing-keys=250000 --storage-records=20000 --iterations=3");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --segment-core-scale --records=1000000 --durability=write-through --primary-index=paged --output=segment-core-scale-1m-paged.json");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --segment-core-qualification --duration-seconds=60 --records=100000 --payload-bytes=1024 --output=segment-core-qualification.json");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --segment-core-read-process --records=100000 --processes=4 --reads-per-process=25000 --output=segment-core-read-process.json");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --segment-core-startup --records=100000 --iterations=5 --worker-timeout-seconds=300 --output=segment-core-startup.json [--capture-open-stages]");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --segment-core-production-pilot --quick --output=segment-core-production-pilot.json");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --segment-core-power-loss-writer --root=E:\\InterfaceDbPowerLoss");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --segment-core-power-loss-validate --root=E:\\InterfaceDbPowerLoss");
    }

    public static void SmokeTestSqlConnection()
    {
        using var connection = CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT @@SERVERNAME AS ServerName, DB_NAME() AS DatabaseName";
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            Console.WriteLine($"Connected to SQL Server '{reader.GetString(0)}', database '{reader.GetString(1)}'.");
        }
    }

    public static bool TryGetProfileTarget(string[] args, out string target)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
            {
                target = argument["--profile=".Length..];
                return !string.IsNullOrWhiteSpace(target);
            }

            if (string.Equals(argument, "--profile", StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length
                && !string.IsNullOrWhiteSpace(args[index + 1]))
            {
                target = args[index + 1];
                return true;
            }
        }

        target = string.Empty;
        return false;
    }

    public static void RunProfileTarget(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        using var benchmark = new TransactionBenchmarks();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"Running InterfaceDB profiling target '{target}'.");
        try
        {
            switch (target.Trim().ToLowerInvariant())
            {
                case "filesystem-store":
                case "file-store":
                    benchmark.SetupFileSystemAsync();
                    benchmark.PrepareFileSystemIterationAsync();
                    benchmark.FileSystem_StoreTransaction().GetAwaiter().GetResult();
                    break;
                case "filesystem-indexed-store":
                case "file-indexed-store":
                    benchmark.SetupIndexedFileSystemAsync();
                    benchmark.PrepareIndexedFileSystemIterationAsync();
                    benchmark.FileSystem_IndexedStoreTransaction().GetAwaiter().GetResult();
                    break;
                case "filesystem-indexed-query":
                case "file-indexed-query":
                    benchmark.SetupIndexedFileSystemQueryStrongObjectCache();
                    _ = benchmark.FileSystem_SearchIndexQueryBatch_StrongObjectCache().GetAwaiter().GetResult();
                    break;
                case "filesystem-indexed-query-hot":
                case "file-indexed-query-hot":
                    benchmark.SetupIndexedFileSystemQueryStrongObjectCache();
                    for (var iteration = 0; iteration < 100; iteration++)
                    {
                        _ = benchmark.FileSystem_SearchIndexQueryBatch_StrongObjectCache().GetAwaiter().GetResult();
                    }

                    break;
                case "filesystem-selective-indexed-query":
                case "file-selective-indexed-query":
                    benchmark.SetupIndexedFileSystemQueryStrongObjectCache();
                    _ = benchmark.FileSystem_SearchIndexSelectiveQueryBatch_StrongObjectCache().GetAwaiter().GetResult();
                    break;
                case "filesystem-selective-indexed-query-hot":
                case "file-selective-indexed-query-hot":
                    benchmark.SetupIndexedFileSystemQueryStrongObjectCache();
                    for (var iteration = 0; iteration < 100; iteration++)
                    {
                        _ = benchmark.FileSystem_SearchIndexSelectiveQueryBatch_StrongObjectCache().GetAwaiter().GetResult();
                    }

                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown profile target '{target}'. Supported targets: filesystem-store, filesystem-indexed-store, filesystem-indexed-query, filesystem-indexed-query-hot, filesystem-selective-indexed-query, filesystem-selective-indexed-query-hot.");
            }

            stopwatch.Stop();
            Console.WriteLine($"Completed profiling target '{target}' in {stopwatch.Elapsed}.");
        }
        finally
        {
            benchmark.Cleanup();
        }
    }

    [GlobalSetup(Target = nameof(FileSystem_StoreTransaction))]
    public void SetupFileSystemAsync()
    {
        _fileDataRoot = Path.Combine(ResolveBenchmarkRoot(), "interface-db-transactions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fileDataRoot);
        _fileProvider = BuildFileSystemProvider(_fileDataRoot);
        var repository = _fileProvider.GetRequiredService<IRepository<ContractPerson.IPerson>>();
        repository.InitializeAsync().GetAwaiter().GetResult();
    }

    [IterationSetup(Target = nameof(FileSystem_StoreTransaction))]
    public void PrepareFileSystemIterationAsync()
    {
        if (_fileDataRoot is null)
        {
            return;
        }

        if (Directory.Exists(_fileDataRoot))
        {
            Directory.Delete(_fileDataRoot, recursive: true);
        }

        Directory.CreateDirectory(_fileDataRoot);

        _fileProvider?.Dispose();
        _fileProvider = BuildFileSystemProvider(_fileDataRoot);
        var repository = _fileProvider.GetRequiredService<IRepository<ContractPerson.IPerson>>();
        repository.InitializeAsync().GetAwaiter().GetResult();
        var warmupPerson = PersonDataFactory.ClonePersons(new[] { _seedPersons[0] })[0];
        repository.SaveAsync(warmupPerson).GetAwaiter().GetResult();
        _iterationPersons = PersonDataFactory.ClonePersons(_seedPersons);
    }

    [Benchmark(Description = "InterfaceDB FileEnvelopeRepository + PassthroughIndexManager - store configured persons")]
    public async Task FileSystem_StoreTransaction()
    {
        if (_fileProvider is null)
        {
            throw new InvalidOperationException("File-system provider not initialized.");
        }

        var persons = _iterationPersons ?? throw new InvalidOperationException("Benchmark iteration has not been prepared.");
        _iterationPersons = null;

        var repository = _fileProvider.GetRequiredService<IRepository<ContractPerson.IPerson>>();
        await repository.SaveManyAsync(persons).ConfigureAwait(false);
    }

    [GlobalSetup(Target = nameof(FileSystem_IndexedStoreTransaction))]
    public void SetupIndexedFileSystemAsync()
    {
        _indexedFileDataRoot = Path.Combine(ResolveBenchmarkRoot(), "interface-db-indexed-transactions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_indexedFileDataRoot);
        _indexedFileProvider = BuildIndexedFileSystemProvider(_indexedFileDataRoot);
        var repository = _indexedFileProvider.GetRequiredService<IRepository<IIndexedBenchmarkPerson>>();
        repository.InitializeAsync().GetAwaiter().GetResult();
    }

    [IterationSetup(Target = nameof(FileSystem_IndexedStoreTransaction))]
    public void PrepareIndexedFileSystemIterationAsync()
    {
        if (_indexedFileDataRoot is null)
        {
            return;
        }

        if (Directory.Exists(_indexedFileDataRoot))
        {
            Directory.Delete(_indexedFileDataRoot, recursive: true);
        }

        Directory.CreateDirectory(_indexedFileDataRoot);

        _indexedFileProvider?.Dispose();
        _indexedFileProvider = BuildIndexedFileSystemProvider(_indexedFileDataRoot);
        var repository = _indexedFileProvider.GetRequiredService<IRepository<IIndexedBenchmarkPerson>>();
        repository.InitializeAsync().GetAwaiter().GetResult();
        var warmupPerson = CloneIndexedPersons(new[] { _seedIndexedPersons[0] })[0];
        repository.SaveAsync(warmupPerson).GetAwaiter().GetResult();
        _iterationIndexedPersons = CloneIndexedPersons(_seedIndexedPersons);
    }

    [Benchmark(Description = "InterfaceDB FileEnvelopeRepository + SearchIndexManager - store configured persons")]
    public async Task FileSystem_IndexedStoreTransaction()
    {
        if (_indexedFileProvider is null)
        {
            throw new InvalidOperationException("Indexed file-system provider not initialized.");
        }

        var persons = _iterationIndexedPersons ?? throw new InvalidOperationException("Benchmark iteration has not been prepared.");
        _iterationIndexedPersons = null;

        var repository = _indexedFileProvider.GetRequiredService<IRepository<IIndexedBenchmarkPerson>>();
        await repository.SaveManyAsync(persons).ConfigureAwait(false);
    }

    [GlobalSetup(Target = nameof(SqlServer_StoreTransaction))]
    public void SetupSqlServer()
    {
        _sqlConnection = CreateOpenConnection();
        EnsureSqlSchema(_sqlConnection);
    }

    [IterationSetup(Target = nameof(SqlServer_StoreTransaction))]
    public void PrepareSqlServerIteration()
    {
        if (_sqlConnection is null)
        {
            throw new InvalidOperationException("SQL Server connection not initialized.");
        }

        EnsureSqlSchema(_sqlConnection);
        _iterationPersons = PersonDataFactory.ClonePersons(_seedPersons);
    }

    [Benchmark(Description = "SQL Server (object transaction) - store configured persons")]
    public void SqlServer_StoreTransaction()
    {
        if (_sqlConnection is null)
        {
            throw new InvalidOperationException("SQL Server connection not initialized.");
        }

        var persons = _iterationPersons ?? throw new InvalidOperationException("Benchmark iteration has not been prepared.");
        _iterationPersons = null;

        using var transaction = _sqlConnection.BeginTransaction();
        using var insert = _sqlConnection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO dbo.Persons(Id, Name, Age, Role, Surname) VALUES (@id, @name, @age, @role, @surname)";
        insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier);
        insert.Parameters.Add("@name", SqlDbType.NVarChar, 200);
        insert.Parameters.Add("@age", SqlDbType.Int);
        insert.Parameters.Add("@role", SqlDbType.NVarChar, 100);
        insert.Parameters.Add("@surname", SqlDbType.NVarChar, 100);

        foreach (var person in persons)
        {
            insert.Parameters["@id"].Value = person.Id;
            insert.Parameters["@name"].Value = person.Name;
            insert.Parameters["@age"].Value = person.Age;
            insert.Parameters["@role"].Value = person.Role;
            insert.Parameters["@surname"].Value = string.Empty;
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    [GlobalSetup(Target = nameof(SqlServer_IndexedStoreTransaction))]
    public void SetupIndexedSqlServer()
    {
        _sqlIndexedConnection = CreateOpenConnection();
        EnsureSqlSchema(_sqlIndexedConnection);
        CreateSqlQueryIndexes(_sqlIndexedConnection);
    }

    [IterationSetup(Target = nameof(SqlServer_IndexedStoreTransaction))]
    public void PrepareIndexedSqlServerIteration()
    {
        if (_sqlIndexedConnection is null)
        {
            throw new InvalidOperationException("Indexed SQL Server connection not initialized.");
        }

        EnsureSqlSchema(_sqlIndexedConnection);
        CreateSqlQueryIndexes(_sqlIndexedConnection);
        _iterationPersons = PersonDataFactory.ClonePersons(_seedPersons);
    }

    [Benchmark(Description = "SQL Server (indexed object transaction) - store configured persons")]
    public void SqlServer_IndexedStoreTransaction()
    {
        if (_sqlIndexedConnection is null)
        {
            throw new InvalidOperationException("Indexed SQL Server connection not initialized.");
        }

        var persons = _iterationPersons ?? throw new InvalidOperationException("Benchmark iteration has not been prepared.");
        _iterationPersons = null;

        using var transaction = _sqlIndexedConnection.BeginTransaction();
        using var insert = _sqlIndexedConnection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO dbo.Persons(Id, Name, Age, Role, Surname) VALUES (@id, @name, @age, @role, @surname)";
        insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier);
        insert.Parameters.Add("@name", SqlDbType.NVarChar, 200);
        insert.Parameters.Add("@age", SqlDbType.Int);
        insert.Parameters.Add("@role", SqlDbType.NVarChar, 100);
        insert.Parameters.Add("@surname", SqlDbType.NVarChar, 100);

        foreach (var person in persons)
        {
            insert.Parameters["@id"].Value = person.Id;
            insert.Parameters["@name"].Value = person.Name;
            insert.Parameters["@age"].Value = person.Age;
            insert.Parameters["@role"].Value = person.Role;
            insert.Parameters["@surname"].Value = string.Empty;
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    [GlobalSetup(Target = nameof(Dapper_StoreTransaction))]
    public void SetupDapper()
    {
        _dapperConnection = CreateOpenConnection();
        EnsureSqlSchema(_dapperConnection);
    }

    [IterationSetup(Target = nameof(Dapper_StoreTransaction))]
    public void PrepareDapperIteration()
    {
        if (_dapperConnection is null)
        {
            throw new InvalidOperationException("Dapper connection not initialized.");
        }

        EnsureSqlSchema(_dapperConnection);
        _iterationPersons = PersonDataFactory.ClonePersons(_seedPersons);
    }

    [Benchmark(Description = "Dapper (object transaction) - store configured persons")]
    public void Dapper_StoreTransaction()
    {
        if (_dapperConnection is null)
        {
            throw new InvalidOperationException("Dapper connection not initialized.");
        }

        var persons = _iterationPersons ?? throw new InvalidOperationException("Benchmark iteration has not been prepared.");
        _iterationPersons = null;

        const string insertSql = "INSERT INTO dbo.Persons(Id, Name, Age, Role, Surname) VALUES (@Id, @Name, @Age, @Role, @Surname)";
        using var transaction = _dapperConnection.BeginTransaction();
        var envelope = new DapperPersonEnvelope();

        foreach (var person in persons)
        {
            envelope.Id = person.Id;
            envelope.Name = person.Name;
            envelope.Age = person.Age;
            envelope.Role = person.Role;
            envelope.Surname = string.Empty;
            _dapperConnection.Execute(insertSql, envelope, transaction);
        }

        transaction.Commit();
    }

    [GlobalSetup(Target = nameof(Dapper_IndexedStoreTransaction))]
    public void SetupIndexedDapper()
    {
        _dapperIndexedConnection = CreateOpenConnection();
        EnsureSqlSchema(_dapperIndexedConnection);
        CreateSqlQueryIndexes(_dapperIndexedConnection);
    }

    [IterationSetup(Target = nameof(Dapper_IndexedStoreTransaction))]
    public void PrepareIndexedDapperIteration()
    {
        if (_dapperIndexedConnection is null)
        {
            throw new InvalidOperationException("Indexed Dapper connection not initialized.");
        }

        EnsureSqlSchema(_dapperIndexedConnection);
        CreateSqlQueryIndexes(_dapperIndexedConnection);
        _iterationPersons = PersonDataFactory.ClonePersons(_seedPersons);
    }

    [Benchmark(Description = "Dapper (indexed object transaction) - store configured persons")]
    public void Dapper_IndexedStoreTransaction()
    {
        if (_dapperIndexedConnection is null)
        {
            throw new InvalidOperationException("Indexed Dapper connection not initialized.");
        }

        var persons = _iterationPersons ?? throw new InvalidOperationException("Benchmark iteration has not been prepared.");
        _iterationPersons = null;

        const string insertSql = "INSERT INTO dbo.Persons(Id, Name, Age, Role, Surname) VALUES (@Id, @Name, @Age, @Role, @Surname)";
        using var transaction = _dapperIndexedConnection.BeginTransaction();
        var envelope = new DapperPersonEnvelope();

        foreach (var person in persons)
        {
            envelope.Id = person.Id;
            envelope.Name = person.Name;
            envelope.Age = person.Age;
            envelope.Role = person.Role;
            envelope.Surname = string.Empty;
            _dapperIndexedConnection.Execute(insertSql, envelope, transaction);
        }

        transaction.Commit();
    }

    [GlobalSetup(Target = nameof(EfCore_StoreTransaction))]
    public void SetupEfCore()
    {
        _efOptions = CreateEfOptions();
        using var connection = CreateOpenConnection();
        EnsureSqlSchema(connection);
    }

    [IterationSetup(Target = nameof(EfCore_StoreTransaction))]
    public void PrepareEfCoreIteration()
    {
        using var connection = CreateOpenConnection();
        EnsureSqlSchema(connection);
        _iterationPersons = PersonDataFactory.ClonePersons(_seedPersons);
    }

    [Benchmark(Description = "Entity Framework Core SQL Server - store configured persons")]
    public void EfCore_StoreTransaction()
    {
        var persons = _iterationPersons ?? throw new InvalidOperationException("Benchmark iteration has not been prepared.");
        _iterationPersons = null;

        using var context = new TransactionBenchDbContext(_efOptions ?? throw new InvalidOperationException("EF Core options not initialized."));
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        using var transaction = context.Database.BeginTransaction();
        context.Persons.AddRange(persons.Select(static person => new TransactionPersonEntity
        {
            Id = person.Id,
            Name = person.Name,
            Age = person.Age,
            Role = person.Role,
            Surname = string.Empty
        }));
        context.SaveChanges();
        transaction.Commit();
    }

    [GlobalSetup(Target = nameof(EfCore_IndexedStoreTransaction))]
    public void SetupIndexedEfCore()
    {
        _efIndexedOptions = CreateEfOptions();
        using var connection = CreateOpenConnection();
        EnsureSqlSchema(connection);
        CreateSqlQueryIndexes(connection);
    }

    [IterationSetup(Target = nameof(EfCore_IndexedStoreTransaction))]
    public void PrepareIndexedEfCoreIteration()
    {
        using var connection = CreateOpenConnection();
        EnsureSqlSchema(connection);
        CreateSqlQueryIndexes(connection);
        _iterationPersons = PersonDataFactory.ClonePersons(_seedPersons);
    }

    [Benchmark(Description = "Entity Framework Core SQL Server + SQL indexes - store configured persons")]
    public void EfCore_IndexedStoreTransaction()
    {
        var persons = _iterationPersons ?? throw new InvalidOperationException("Benchmark iteration has not been prepared.");
        _iterationPersons = null;

        using var context = new TransactionBenchDbContext(_efIndexedOptions ?? throw new InvalidOperationException("Indexed EF Core options not initialized."));
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        using var transaction = context.Database.BeginTransaction();
        context.Persons.AddRange(persons.Select(static person => new TransactionPersonEntity
        {
            Id = person.Id,
            Name = person.Name,
            Age = person.Age,
            Role = person.Role,
            Surname = string.Empty
        }));
        context.SaveChanges();
        transaction.Commit();
    }

    [GlobalSetup(Target = nameof(SqlServer_BulkCopy))]
    public void SetupSqlBulkCopy()
    {
        _sqlBulkConnection = CreateOpenConnection();
        EnsureSqlSchema(_sqlBulkConnection);
    }

    [IterationSetup(Target = nameof(SqlServer_BulkCopy))]
    public void PrepareSqlBulkCopyIteration()
    {
        if (_sqlBulkConnection is null)
        {
            throw new InvalidOperationException("SQL Server bulk connection not initialized.");
        }

        EnsureSqlSchema(_sqlBulkConnection);
        _iterationPersons = PersonDataFactory.ClonePersons(_seedPersons);
    }

    [Benchmark(Description = "SQL Server (SqlBulkCopy) - store configured persons")]
    public void SqlServer_BulkCopy()
    {
        if (_sqlBulkConnection is null)
        {
            throw new InvalidOperationException("SQL Server bulk connection not initialized.");
        }

        var persons = _iterationPersons ?? throw new InvalidOperationException("Benchmark iteration has not been prepared.");
        _iterationPersons = null;

        using var bulkCopy = new SqlBulkCopy(
            _sqlBulkConnection,
            SqlBulkCopyOptions.TableLock,
            externalTransaction: null)
        {
            DestinationTableName = "dbo.Persons",
            BatchSize = 5_000,
            BulkCopyTimeout = 0
        };
        bulkCopy.ColumnMappings.Add("Id", "Id");
        bulkCopy.ColumnMappings.Add("Name", "Name");
        bulkCopy.ColumnMappings.Add("Age", "Age");
        bulkCopy.ColumnMappings.Add("Role", "Role");
        bulkCopy.ColumnMappings.Add("Surname", "Surname");
        bulkCopy.WriteToServer(CreatePersonDataTable(persons));
    }

    [GlobalSetup(Target = nameof(FileSystem_QueryBatch_StrongObjectCache))]
    public void SetupFileSystemQueryStrongObjectCache()
        => SetupFileSystemQuery(StorageObjectCacheMode.StrongObject);

    [GlobalSetup(Target = nameof(FileSystem_QueryBatch_MetadataOnly))]
    public void SetupFileSystemQueryMetadataOnly()
        => SetupFileSystemQuery(StorageObjectCacheMode.MetadataOnly);

    [GlobalSetup(Target = nameof(FileSystem_QueryBatch_WeakObjectCache))]
    public void SetupFileSystemQueryWeakObjectCache()
        => SetupFileSystemQuery(StorageObjectCacheMode.WeakObject);

    private void SetupFileSystemQuery(StorageObjectCacheMode objectCacheMode)
    {
        _fileDataRoot = Path.Combine(ResolveBenchmarkRoot(), "interface-db-query", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fileDataRoot);
        _fileProvider = BuildFileSystemProvider(_fileDataRoot, objectCacheMode: objectCacheMode);
        _queryRepository = _fileProvider.GetRequiredService<IRepository<ContractPerson.IPerson>>();
        _queryRepository.InitializeAsync().GetAwaiter().GetResult();
        _queryRepository.SaveManyAsync(PersonDataFactory.ClonePersons(_seedPersons)).GetAwaiter().GetResult();
        EnsureQueryDigest(
            "InterfaceDB predicate query schedule",
            _expectedQueryBatch,
            ExecuteFileSystemQueryBatchAsync().GetAwaiter().GetResult());
    }

    [GlobalSetup(Targets = new[]
    {
        nameof(FileSystem_SearchIndexQueryBatch_StrongObjectCache),
        nameof(FileSystem_SearchIndexSelectiveQueryBatch_StrongObjectCache)
    })]
    public void SetupIndexedFileSystemQueryStrongObjectCache()
        => SetupIndexedFileSystemQuery(StorageObjectCacheMode.StrongObject);

    [GlobalSetup(Targets = new[]
    {
        nameof(FileSystem_SearchIndexQueryBatch_MetadataOnly),
        nameof(FileSystem_SearchIndexSelectiveQueryBatch_MetadataOnly)
    })]
    public void SetupIndexedFileSystemQueryMetadataOnly()
        => SetupIndexedFileSystemQuery(StorageObjectCacheMode.MetadataOnly);

    [GlobalSetup(Targets = new[]
    {
        nameof(FileSystem_SearchIndexQueryBatch_WeakObjectCache),
        nameof(FileSystem_SearchIndexSelectiveQueryBatch_WeakObjectCache)
    })]
    public void SetupIndexedFileSystemQueryWeakObjectCache()
        => SetupIndexedFileSystemQuery(StorageObjectCacheMode.WeakObject);

    private void SetupIndexedFileSystemQuery(StorageObjectCacheMode objectCacheMode)
    {
        _indexedFileDataRoot = Path.Combine(ResolveBenchmarkRoot(), "interface-db-indexed-query", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_indexedFileDataRoot);
        _indexedFileProvider = BuildIndexedFileSystemProvider(_indexedFileDataRoot, objectCacheMode: objectCacheMode);
        _indexedQueryRepository = _indexedFileProvider.GetRequiredService<IRepository<IIndexedBenchmarkPerson>>();
        _indexedQueryRepository.InitializeAsync().GetAwaiter().GetResult();
        _indexedQueryRepository.SaveManyAsync(CloneIndexedPersons(_seedIndexedPersons)).GetAwaiter().GetResult();
        EnsureQueryDigest(
            "InterfaceDB indexed broad query schedule",
            _expectedQueryBatch,
            ExecuteFileSystemSearchIndexQueryBatchAsync().GetAwaiter().GetResult());
        EnsureQueryDigest(
            "InterfaceDB indexed selective query schedule",
            _expectedSelectiveQueryBatch,
            ExecuteFileSystemSearchIndexSelectiveQueryBatchAsync().GetAwaiter().GetResult());
    }

    [GlobalSetup(Targets = new[] { nameof(SqlServer_QueryBatch), nameof(SqlServer_SelectiveQueryBatch) })]
    public void SetupSqlServerQuery()
    {
        _sqlQueryConnection = CreateOpenConnection();
        EnsureSqlSchema(_sqlQueryConnection);
        using var bulkCopy = new SqlBulkCopy(
            _sqlQueryConnection,
            SqlBulkCopyOptions.TableLock,
            externalTransaction: null)
        {
            DestinationTableName = "dbo.Persons",
            BatchSize = 5_000,
            BulkCopyTimeout = 0
        };
        bulkCopy.ColumnMappings.Add("Id", "Id");
        bulkCopy.ColumnMappings.Add("Name", "Name");
        bulkCopy.ColumnMappings.Add("Age", "Age");
        bulkCopy.ColumnMappings.Add("Role", "Role");
        bulkCopy.ColumnMappings.Add("Surname", "Surname");
        bulkCopy.WriteToServer(CreatePersonDataTable(_seedPersons));
        CreateSqlQueryIndexes(_sqlQueryConnection);
        EnsureQueryDigest(
            "SQL Server ADO.NET broad query schedule",
            _expectedQueryBatch,
            ExecuteSqlServerQueryBatch());
        EnsureQueryDigest(
            "SQL Server ADO.NET selective query schedule",
            _expectedSelectiveQueryBatch,
            ExecuteSqlServerSelectiveQueryBatch());
    }

    [GlobalSetup(Targets = new[] { nameof(Dapper_QueryBatch), nameof(Dapper_SelectiveQueryBatch) })]
    public void SetupDapperQuery()
    {
        _dapperQueryConnection = CreateOpenConnection();
        EnsureSqlSchema(_dapperQueryConnection);
        using var bulkCopy = new SqlBulkCopy(
            _dapperQueryConnection,
            SqlBulkCopyOptions.TableLock,
            externalTransaction: null)
        {
            DestinationTableName = "dbo.Persons",
            BatchSize = 5_000,
            BulkCopyTimeout = 0
        };
        bulkCopy.ColumnMappings.Add("Id", "Id");
        bulkCopy.ColumnMappings.Add("Name", "Name");
        bulkCopy.ColumnMappings.Add("Age", "Age");
        bulkCopy.ColumnMappings.Add("Role", "Role");
        bulkCopy.ColumnMappings.Add("Surname", "Surname");
        bulkCopy.WriteToServer(CreatePersonDataTable(_seedPersons));
        CreateSqlQueryIndexes(_dapperQueryConnection);

        EnsureQueryDigest(
            "Dapper broad query schedule",
            _expectedQueryBatch,
            ExecuteDapperQueryBatch());
        EnsureQueryDigest(
            "Dapper selective query schedule",
            _expectedSelectiveQueryBatch,
            ExecuteDapperSelectiveQueryBatch());
    }

    [GlobalSetup(Targets = new[] { nameof(EfCore_QueryBatch), nameof(EfCore_SelectiveQueryBatch) })]
    public void SetupEfCoreQuery()
    {
        _efQueryOptions = CreateEfOptions();
        using var connection = CreateOpenConnection();
        EnsureSqlSchema(connection);
        using var bulkCopy = new SqlBulkCopy(
            connection,
            SqlBulkCopyOptions.TableLock,
            externalTransaction: null)
        {
            DestinationTableName = "dbo.Persons",
            BatchSize = 5_000,
            BulkCopyTimeout = 0
        };
        bulkCopy.ColumnMappings.Add("Id", "Id");
        bulkCopy.ColumnMappings.Add("Name", "Name");
        bulkCopy.ColumnMappings.Add("Age", "Age");
        bulkCopy.ColumnMappings.Add("Role", "Role");
        bulkCopy.ColumnMappings.Add("Surname", "Surname");
        bulkCopy.WriteToServer(CreatePersonDataTable(_seedPersons));
        CreateSqlQueryIndexes(connection);

        EnsureQueryDigest(
            "EF Core broad query schedule",
            _expectedQueryBatch,
            ExecuteEfCoreQueryBatch());
        EnsureQueryDigest(
            "EF Core selective query schedule",
            _expectedSelectiveQueryBatch,
            ExecuteEfCoreSelectiveQueryBatch());
    }

    [Benchmark(Description = "InterfaceDB FileEnvelopeRepository + PassthroughIndexManager + strong object cache - 100 predicate scans over configured persons")]
    public async Task<BenchmarkQueryDigest> FileSystem_QueryBatch_StrongObjectCache()
        => await ExecuteFileSystemQueryBatchAsync().ConfigureAwait(false);

    [Benchmark(Description = "InterfaceDB FileEnvelopeRepository + PassthroughIndexManager + metadata-only cache - 100 predicate scans over configured persons")]
    public async Task<BenchmarkQueryDigest> FileSystem_QueryBatch_MetadataOnly()
        => await ExecuteFileSystemQueryBatchAsync().ConfigureAwait(false);

    [Benchmark(Description = "InterfaceDB FileEnvelopeRepository + PassthroughIndexManager + weak object cache - 100 predicate scans over configured persons")]
    public async Task<BenchmarkQueryDigest> FileSystem_QueryBatch_WeakObjectCache()
        => await ExecuteFileSystemQueryBatchAsync().ConfigureAwait(false);

    private async Task<BenchmarkQueryDigest> ExecuteFileSystemQueryBatchAsync()
    {
        var repository = _queryRepository ?? throw new InvalidOperationException("File-system query repository not initialized.");
        var total = default(BenchmarkQueryDigest);

        for (var i = 0; i < QueryRepeatCount; i++)
        {
            var role = QueryRoles[i % QueryRoles.Length];
            var minimumAge = 20 + (i % 30);
            var materialized = new List<ContractPerson.IPerson>();
            await foreach (var person in repository.QueryManyAsync(person => person.Role == role && person.Age >= minimumAge))
            {
                materialized.Add(person);
            }

            total = total.Combine(DigestPersons(materialized), i);
            GC.KeepAlive(materialized);
        }

        return total;
    }

    [Benchmark(Description = "SQL Server ADO.NET + SQL indexes - 100 materialized queries over configured persons")]
    public BenchmarkQueryDigest SqlServer_QueryBatch()
        => ExecuteSqlServerQueryBatch();

    private BenchmarkQueryDigest ExecuteSqlServerQueryBatch()
    {
        if (_sqlQueryConnection is null)
        {
            throw new InvalidOperationException("SQL Server query connection not initialized.");
        }

        var total = default(BenchmarkQueryDigest);
        for (var i = 0; i < QueryRepeatCount; i++)
        {
            var role = QueryRoles[i % QueryRoles.Length];
            var minimumAge = 20 + (i % 30);
            total = total.Combine(DigestSqlPersons(MaterializeSqlPersons(_sqlQueryConnection, role, minimumAge)), i);
        }

        return total;
    }

    [Benchmark(Description = "Dapper SQL Server + SQL indexes - 100 materialized queries over configured persons")]
    public BenchmarkQueryDigest Dapper_QueryBatch()
        => ExecuteDapperQueryBatch();

    private BenchmarkQueryDigest ExecuteDapperQueryBatch()
    {
        if (_dapperQueryConnection is null)
        {
            throw new InvalidOperationException("Dapper query connection not initialized.");
        }

        var total = default(BenchmarkQueryDigest);
        for (var i = 0; i < QueryRepeatCount; i++)
        {
            var role = QueryRoles[i % QueryRoles.Length];
            var minimumAge = 20 + (i % 30);
            total = total.Combine(DigestSqlPersons(QueryDapperPersons(_dapperQueryConnection, role, minimumAge)), i);
        }

        return total;
    }

    [Benchmark(Description = "Entity Framework Core SQL Server + SQL indexes - 100 materialized queries over configured persons")]
    public BenchmarkQueryDigest EfCore_QueryBatch()
        => ExecuteEfCoreQueryBatch();

    private BenchmarkQueryDigest ExecuteEfCoreQueryBatch()
    {
        var options = _efQueryOptions ?? throw new InvalidOperationException("EF Core query options not initialized.");
        var total = default(BenchmarkQueryDigest);
        for (var i = 0; i < QueryRepeatCount; i++)
        {
            var role = QueryRoles[i % QueryRoles.Length];
            var minimumAge = 20 + (i % 30);
            total = total.Combine(DigestSqlPersons(QueryEfCorePersons(options, role, minimumAge)), i);
        }

        return total;
    }

    [Benchmark(Description = "InterfaceDB FileEnvelopeRepository + SearchIndexManager + strong object cache - 100 indexed searches over configured persons")]
    public async Task<BenchmarkQueryDigest> FileSystem_SearchIndexQueryBatch_StrongObjectCache()
        => await ExecuteFileSystemSearchIndexQueryBatchAsync().ConfigureAwait(false);

    [Benchmark(Description = "InterfaceDB FileEnvelopeRepository + SearchIndexManager + metadata-only cache - 100 indexed searches over configured persons")]
    public async Task<BenchmarkQueryDigest> FileSystem_SearchIndexQueryBatch_MetadataOnly()
        => await ExecuteFileSystemSearchIndexQueryBatchAsync().ConfigureAwait(false);

    [Benchmark(Description = "InterfaceDB FileEnvelopeRepository + SearchIndexManager + weak object cache - 100 indexed searches over configured persons")]
    public async Task<BenchmarkQueryDigest> FileSystem_SearchIndexQueryBatch_WeakObjectCache()
        => await ExecuteFileSystemSearchIndexQueryBatchAsync().ConfigureAwait(false);

    private async Task<BenchmarkQueryDigest> ExecuteFileSystemSearchIndexQueryBatchAsync()
    {
        var repository = _indexedQueryRepository ?? throw new InvalidOperationException("Indexed file-system query repository not initialized.");
        var total = default(BenchmarkQueryDigest);

        for (var i = 0; i < QueryRepeatCount; i++)
        {
            var role = QueryRoles[i % QueryRoles.Length];
            var minimumAge = 20 + (i % 30);
            var materialized = new List<IIndexedBenchmarkPerson>();
            await foreach (var person in repository.SearchManyAsync(person => person.Role == role && person.Age >= minimumAge))
            {
                materialized.Add(person);
            }

            total = total.Combine(DigestIndexedPersons(materialized), i);
            GC.KeepAlive(materialized);
        }

        return total;
    }

    [Benchmark(Description = "InterfaceDB FileEnvelopeRepository + SearchIndexManager + strong object cache - 100 selective indexed searches over configured persons")]
    public async Task<BenchmarkQueryDigest> FileSystem_SearchIndexSelectiveQueryBatch_StrongObjectCache()
        => await ExecuteFileSystemSearchIndexSelectiveQueryBatchAsync().ConfigureAwait(false);

    [Benchmark(Description = "InterfaceDB FileEnvelopeRepository + SearchIndexManager + metadata-only cache - 100 selective indexed searches over configured persons")]
    public async Task<BenchmarkQueryDigest> FileSystem_SearchIndexSelectiveQueryBatch_MetadataOnly()
        => await ExecuteFileSystemSearchIndexSelectiveQueryBatchAsync().ConfigureAwait(false);

    [Benchmark(Description = "InterfaceDB FileEnvelopeRepository + SearchIndexManager + weak object cache - 100 selective indexed searches over configured persons")]
    public async Task<BenchmarkQueryDigest> FileSystem_SearchIndexSelectiveQueryBatch_WeakObjectCache()
        => await ExecuteFileSystemSearchIndexSelectiveQueryBatchAsync().ConfigureAwait(false);

    private async Task<BenchmarkQueryDigest> ExecuteFileSystemSearchIndexSelectiveQueryBatchAsync()
    {
        var repository = _indexedQueryRepository ?? throw new InvalidOperationException("Indexed file-system query repository not initialized.");
        var total = default(BenchmarkQueryDigest);

        for (var i = 0; i < QueryRepeatCount; i++)
        {
            var materialized = new List<IIndexedBenchmarkPerson>();
            if ((i & 1) == 0)
            {
                var id = _seedIndexedPersons[i % _seedIndexedPersons.Count].Id;
                await foreach (var person in repository.SearchManyAsync(person => person.Id == id))
                {
                    materialized.Add(person);
                }
            }
            else
            {
                var role = QueryRoles[i % QueryRoles.Length];
                await foreach (var person in repository.SearchManyAsync(person => person.Role == role && person.Age >= 58))
                {
                    materialized.Add(person);
                }
            }

            total = total.Combine(DigestIndexedPersons(materialized), i);
            GC.KeepAlive(materialized);
        }

        return total;
    }

    [Benchmark(Description = "SQL Server ADO.NET + SQL indexes - 100 selective materialized queries over configured persons")]
    public BenchmarkQueryDigest SqlServer_SelectiveQueryBatch()
        => ExecuteSqlServerSelectiveQueryBatch();

    private BenchmarkQueryDigest ExecuteSqlServerSelectiveQueryBatch()
    {
        if (_sqlQueryConnection is null)
        {
            throw new InvalidOperationException("SQL Server query connection not initialized.");
        }

        var total = default(BenchmarkQueryDigest);
        for (var i = 0; i < QueryRepeatCount; i++)
        {
            if ((i & 1) == 0)
            {
                var id = _seedPersons[i % _seedPersons.Count].Id;
                total = total.Combine(DigestSqlPersons(MaterializeSqlPersonById(_sqlQueryConnection, id)), i);
            }
            else
            {
                var role = QueryRoles[i % QueryRoles.Length];
                total = total.Combine(DigestSqlPersons(MaterializeSqlPersons(_sqlQueryConnection, role, 58)), i);
            }
        }

        return total;
    }

    [Benchmark(Description = "Dapper SQL Server + SQL indexes - 100 selective materialized queries over configured persons")]
    public BenchmarkQueryDigest Dapper_SelectiveQueryBatch()
        => ExecuteDapperSelectiveQueryBatch();

    private BenchmarkQueryDigest ExecuteDapperSelectiveQueryBatch()
    {
        if (_dapperQueryConnection is null)
        {
            throw new InvalidOperationException("Dapper query connection not initialized.");
        }

        var total = default(BenchmarkQueryDigest);
        for (var i = 0; i < QueryRepeatCount; i++)
        {
            if ((i & 1) == 0)
            {
                var id = _seedPersons[i % _seedPersons.Count].Id;
                total = total.Combine(DigestSqlPersons(QueryDapperPersonById(_dapperQueryConnection, id)), i);
            }
            else
            {
                var role = QueryRoles[i % QueryRoles.Length];
                total = total.Combine(DigestSqlPersons(QueryDapperPersons(_dapperQueryConnection, role, 58)), i);
            }
        }

        return total;
    }

    [Benchmark(Description = "Entity Framework Core SQL Server + SQL indexes - 100 selective materialized queries over configured persons")]
    public BenchmarkQueryDigest EfCore_SelectiveQueryBatch()
        => ExecuteEfCoreSelectiveQueryBatch();

    private BenchmarkQueryDigest ExecuteEfCoreSelectiveQueryBatch()
    {
        var options = _efQueryOptions ?? throw new InvalidOperationException("EF Core query options not initialized.");
        var total = default(BenchmarkQueryDigest);
        for (var i = 0; i < QueryRepeatCount; i++)
        {
            if ((i & 1) == 0)
            {
                var id = _seedPersons[i % _seedPersons.Count].Id;
                total = total.Combine(DigestSqlPersons(QueryEfCorePersonById(options, id)), i);
            }
            else
            {
                var role = QueryRoles[i % QueryRoles.Length];
                total = total.Combine(DigestSqlPersons(QueryEfCorePersons(options, role, 58)), i);
            }
        }

        return total;
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _fileProvider?.Dispose();
        _fileProvider = null;

        _indexedFileProvider?.Dispose();
        _indexedFileProvider = null;

        if (_fileDataRoot is not null && Directory.Exists(_fileDataRoot))
        {
            Directory.Delete(_fileDataRoot, recursive: true);
        }

        if (_indexedFileDataRoot is not null && Directory.Exists(_indexedFileDataRoot))
        {
            Directory.Delete(_indexedFileDataRoot, recursive: true);
        }

        _sqlConnection?.Dispose();
        _sqlConnection = null;

        _sqlIndexedConnection?.Dispose();
        _sqlIndexedConnection = null;

        _sqlBulkConnection?.Dispose();
        _sqlBulkConnection = null;

        _sqlQueryConnection?.Dispose();
        _sqlQueryConnection = null;

        _dapperConnection?.Dispose();
        _dapperConnection = null;

        _dapperIndexedConnection?.Dispose();
        _dapperIndexedConnection = null;

        _dapperQueryConnection?.Dispose();
        _dapperQueryConnection = null;

        _iterationPersons = null;
        _iterationIndexedPersons = null;
        _queryRepository = null;
        _indexedQueryRepository = null;
    }

    private static ServiceProvider BuildFileSystemProvider(
        string dataRoot,
        StorageObjectCacheMode objectCacheMode = StorageObjectCacheMode.MetadataOnly)
    {
        var maxConcurrency = ResolvePositiveInt(
            MaxConcurrencyEnvironmentVariable,
            FileSystemBenchmarkConcurrency);
        var bulkBatchSize = ResolvePositiveInt(
            BulkBatchSizeEnvironmentVariable,
            FileSystemBenchmarkBulkBatchSize);
        var useSingleWriteBuffer = ResolveBool(
            UseSingleWriteBufferEnvironmentVariable,
            defaultValue: true);

        var options = new PersonOptions
        {
            DataRoot = dataRoot,
            ImplementationsPath = Path.Combine(Path.GetTempPath(), "interface-db-transactions-impl"),
            ContractPath = string.Empty,
            MigrationsPath = string.Empty,
            ActiveVersion = "1.0.0",
            RepositoryType = ContractRepositoryType.FileSystem,
            StoragePolicy = StoragePolicy.Default with
            {
                HydrateOnInitialize = false,
                MaxConcurrency = Math.Min(maxConcurrency, Environment.ProcessorCount),
                BulkWriteBatchSize = bulkBatchSize,
                FileWriteBufferSize = 16 * 1024,
                RaiseNotifications = false,
                ObjectCacheMode = objectCacheMode,
                UseSingleWriteBuffer = useSingleWriteBuffer,
                UseWriterSemaphore = false,
                WatchStorageChanges = false
            }
        };

        var services = new ServiceCollection();
        services.AddRepository<ContractPerson.IPerson>(options);

        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildIndexedFileSystemProvider(
        string dataRoot,
        StorageObjectCacheMode objectCacheMode = StorageObjectCacheMode.MetadataOnly)
    {
        var maxConcurrency = ResolvePositiveInt(
            MaxConcurrencyEnvironmentVariable,
            FileSystemBenchmarkConcurrency);
        var bulkBatchSize = ResolvePositiveInt(
            BulkBatchSizeEnvironmentVariable,
            FileSystemBenchmarkBulkBatchSize);
        var useSingleWriteBuffer = ResolveBool(
            UseSingleWriteBufferEnvironmentVariable,
            defaultValue: true);

        var options = new IndexedBenchmarkPersonOptions
        {
            DataRoot = dataRoot,
            ActiveVersion = "1.0.0",
            RepositoryType = ContractRepositoryType.FileSystem,
            StoragePolicy = StoragePolicy.Default with
            {
                HydrateOnInitialize = false,
                MaxConcurrency = Math.Min(maxConcurrency, Environment.ProcessorCount),
                BulkWriteBatchSize = bulkBatchSize,
                FileWriteBufferSize = 16 * 1024,
                RaiseNotifications = false,
                ObjectCacheMode = objectCacheMode,
                UseSingleWriteBuffer = useSingleWriteBuffer,
                UseWriterSemaphore = false,
                WatchStorageChanges = false
            }
        };

        var services = new ServiceCollection();
        services.AddRepository<IIndexedBenchmarkPerson>(options);

        return services.BuildServiceProvider();
    }

    private static int ResolvePositiveInt(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : defaultValue;
    }

    private static bool ResolveBool(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    internal static IConfiguration EnsureConfigurationInitialized()
    {
        if (Configuration is not null)
        {
            return Configuration;
        }

        Configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        return Configuration;
    }

    private static string ResolveSqlConnectionString()
    {
        var config = EnsureConfigurationInitialized();
        var connectionString = config["Benchmarks:SqlServer:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("InterfaceDbBench", StringComparison.OrdinalIgnoreCase) && connectionString.Contains("localhost", StringComparison.OrdinalIgnoreCase) && connectionString.EndsWith("TrustServerCertificate=true", StringComparison.OrdinalIgnoreCase))
        {
            // Allow default placeholder but warn user if database isn't available, they can adjust connection string.
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Configure Benchmarks:SqlServer:ConnectionString in StorageBenchmarks.Transactions/appsettings.json or environment variables.");
        }

        return connectionString;
    }

    private static string ResolveBenchmarkRoot()
    {
        const string preferredRoot = @"G:\PerformanceTest";
        return Directory.Exists(Path.GetPathRoot(preferredRoot))
            ? preferredRoot
            : Path.Combine(Path.GetTempPath(), "InterfaceDbPerformanceTest");
    }

    private static SqlConnection CreateOpenConnection()
    {
        var connectionString = ResolveSqlConnectionString();
        var connection = new SqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static DbContextOptions<TransactionBenchDbContext> CreateEfOptions()
    {
        var builder = new DbContextOptionsBuilder<TransactionBenchDbContext>();
        builder.UseSqlServer(ResolveSqlConnectionString(), sql =>
        {
            sql.CommandTimeout(300);
        });
        return builder.Options;
    }

    private static void EnsureSqlSchema(SqlConnection connection)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID('dbo.Persons', 'U') IS NOT NULL
                DROP TABLE dbo.Persons;
            CREATE TABLE dbo.Persons(
                Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                Name NVARCHAR(200) NOT NULL,
                Age INT NOT NULL,
                Role NVARCHAR(100) NOT NULL,
                Surname NVARCHAR(100) NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void CreateSqlQueryIndexes(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE INDEX IX_Persons_Role_Age ON dbo.Persons(Role, Age);";
        command.ExecuteNonQuery();
    }

    private BenchmarkQueryDigest ExpectedQueryBatch()
    {
        var total = default(BenchmarkQueryDigest);
        for (var i = 0; i < QueryRepeatCount; i++)
        {
            var role = QueryRoles[i % QueryRoles.Length];
            var minimumAge = 20 + (i % 30);
            var materialized = _seedPersons
                .Where(person => person.Role == role && person.Age >= minimumAge)
                .ToList();
            total = total.Combine(DigestPersons(materialized), i);
            GC.KeepAlive(materialized);
        }

        return total;
    }

    private BenchmarkQueryDigest ExpectedSelectiveQueryBatch()
    {
        var total = default(BenchmarkQueryDigest);
        for (var i = 0; i < QueryRepeatCount; i++)
        {
            List<ContractPerson.IPerson> materialized;
            if ((i & 1) == 0)
            {
                var id = _seedPersons[i % _seedPersons.Count].Id;
                materialized = _seedPersons.Where(person => person.Id == id).ToList();
            }
            else
            {
                var role = QueryRoles[i % QueryRoles.Length];
                materialized = _seedPersons
                    .Where(person => person.Role == role && person.Age >= 58)
                    .ToList();
            }

            total = total.Combine(DigestPersons(materialized), i);
            GC.KeepAlive(materialized);
        }

        return total;
    }

    private static void EnsureQueryDigest(
        string scenario,
        BenchmarkQueryDigest expected,
        BenchmarkQueryDigest actual)
    {
        if (expected.Count <= 0)
        {
            throw new InvalidOperationException($"{scenario} has an empty expected query schedule.");
        }

        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{scenario} materialized {actual.Count} rows with digest {actual.HashSum:X16}/{actual.HashXor:X16}; "
                + $"expected {expected.Count} rows with digest {expected.HashSum:X16}/{expected.HashXor:X16}.");
        }
    }

    private static BenchmarkQueryDigest DigestPersons(IReadOnlyCollection<ContractPerson.IPerson> persons)
    {
        var digest = DigestPersons(
            persons,
            static person => person.Id,
            static person => person.Name,
            static person => person.Age,
            static person => person.Role);
        GC.KeepAlive(persons);
        return digest;
    }

    private static BenchmarkQueryDigest DigestIndexedPersons(IReadOnlyCollection<IIndexedBenchmarkPerson> persons)
    {
        var digest = DigestPersons(
            persons,
            static person => person.Id,
            static person => person.Name,
            static person => person.Age,
            static person => person.Role);
        GC.KeepAlive(persons);
        return digest;
    }

    private static BenchmarkQueryDigest DigestSqlPersons(IReadOnlyCollection<DapperPersonEnvelope> persons)
    {
        var digest = DigestPersons(
            persons,
            static person => person.Id,
            static person => person.Name,
            static person => person.Age,
            static person => person.Role);
        GC.KeepAlive(persons);
        return digest;
    }

    private static BenchmarkQueryDigest DigestPersons<T>(
        IReadOnlyCollection<T> persons,
        Func<T, Guid> id,
        Func<T, string> name,
        Func<T, int> age,
        Func<T, string> role)
    {
        ulong hashSum = 0;
        ulong hashXor = 0;
        foreach (var person in persons)
        {
            var personHash = HashPerson(id(person), name(person), age(person), role(person));
            hashSum = unchecked(hashSum + personHash);
            hashXor ^= personHash;
        }

        return new BenchmarkQueryDigest(persons.Count, hashSum, hashXor);
    }

    private static ulong HashPerson(Guid id, string name, int age, string role)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        var hash = offsetBasis;
        Span<byte> idBytes = stackalloc byte[16];
        id.TryWriteBytes(idBytes);
        foreach (var value in idBytes)
        {
            MixHashByte(ref hash, value);
        }

        MixHashString(ref hash, name);
        MixHashInt32(ref hash, age);
        MixHashString(ref hash, role);
        return hash;
    }

    private static void MixHashString(ref ulong hash, string value)
    {
        MixHashInt32(ref hash, value.Length);
        foreach (var character in value)
        {
            MixHashByte(ref hash, unchecked((byte)character));
            MixHashByte(ref hash, unchecked((byte)(character >> 8)));
        }
    }

    private static void MixHashInt32(ref ulong hash, int value)
    {
        MixHashByte(ref hash, unchecked((byte)value));
        MixHashByte(ref hash, unchecked((byte)(value >> 8)));
        MixHashByte(ref hash, unchecked((byte)(value >> 16)));
        MixHashByte(ref hash, unchecked((byte)(value >> 24)));
    }

    private static void MixHashByte(ref ulong hash, byte value)
    {
        const ulong prime = 1099511628211UL;
        hash = unchecked((hash ^ value) * prime);
    }

    private static List<DapperPersonEnvelope> MaterializeSqlPersons(
        SqlConnection connection,
        string role,
        int minimumAge)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Age, Role
            FROM dbo.Persons
            WHERE Role = @role AND Age >= @minimumAge
            """;
        command.Parameters.Add("@role", SqlDbType.NVarChar, 100).Value = role;
        command.Parameters.Add("@minimumAge", SqlDbType.Int).Value = minimumAge;

        var results = new List<DapperPersonEnvelope>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new DapperPersonEnvelope
            {
                Id = reader.GetGuid(0),
                Name = reader.GetString(1),
                Age = reader.GetInt32(2),
                Role = reader.GetString(3)
            });
        }

        return results;
    }

    private static List<DapperPersonEnvelope> MaterializeSqlPersonById(
        SqlConnection connection,
        Guid id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Age, Role
            FROM dbo.Persons
            WHERE Id = @id
            """;
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;

        var results = new List<DapperPersonEnvelope>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new DapperPersonEnvelope
            {
                Id = reader.GetGuid(0),
                Name = reader.GetString(1),
                Age = reader.GetInt32(2),
                Role = reader.GetString(3)
            });
        }

        return results;
    }

    private static List<DapperPersonEnvelope> QueryDapperPersons(
        SqlConnection connection,
        string role,
        int minimumAge)
    {
        const string sql = """
            SELECT Id, Name, Age, Role
            FROM dbo.Persons
            WHERE Role = @Role AND Age >= @MinimumAge
            """;
        return connection
            .Query<DapperPersonEnvelope>(
                sql,
                new { Role = role, MinimumAge = minimumAge })
            .AsList();
    }

    private static List<DapperPersonEnvelope> QueryDapperPersonById(
        SqlConnection connection,
        Guid id)
    {
        const string sql = """
            SELECT Id, Name, Age, Role
            FROM dbo.Persons
            WHERE Id = @Id
            """;
        return connection
            .Query<DapperPersonEnvelope>(
                sql,
                new { Id = id })
            .AsList();
    }

    private static List<DapperPersonEnvelope> QueryEfCorePersons(
        DbContextOptions<TransactionBenchDbContext> options,
        string role,
        int minimumAge)
    {
        using var context = new TransactionBenchDbContext(options);
        return context.Persons
            .AsNoTracking()
            .Where(person => person.Role == role && person.Age >= minimumAge)
            .Select(person => new DapperPersonEnvelope
            {
                Id = person.Id,
                Name = person.Name,
                Age = person.Age,
                Role = person.Role
            })
            .ToList();
    }

    private static List<DapperPersonEnvelope> QueryEfCorePersonById(
        DbContextOptions<TransactionBenchDbContext> options,
        Guid id)
    {
        using var context = new TransactionBenchDbContext(options);
        return context.Persons
            .AsNoTracking()
            .Where(person => person.Id == id)
            .Select(person => new DapperPersonEnvelope
            {
                Id = person.Id,
                Name = person.Name,
                Age = person.Age,
                Role = person.Role
            })
            .ToList();
    }

    private static DataTable CreatePersonDataTable(IEnumerable<ContractPerson.IPerson> persons)
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(Guid));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Age", typeof(int));
        table.Columns.Add("Role", typeof(string));
        table.Columns.Add("Surname", typeof(string));

        foreach (var person in persons)
        {
            table.Rows.Add(person.Id, person.Name, person.Age, person.Role, string.Empty);
        }

        return table;
    }

    private static List<ContractPerson.IPerson> CreateDeterministicPersons(int count)
    {
        var persons = new List<ContractPerson.IPerson>(count);
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var sample = MixDeterministic(unchecked((ulong)(uint)ordinal + 0xA0761D6478BD642FUL));
            var id = CreateDeterministicGuid(ordinal);
            var name = $"{BenchmarkFirstNames[sample % (uint)BenchmarkFirstNames.Length]} "
                + BenchmarkFirstNames[(sample >> 8) % (uint)BenchmarkFirstNames.Length];
            var age = 18 + (int)((sample >> 16) % 42);
            persons.Add((ordinal & 1) == 0
                ? new PersonStorage.Student(id, name, age, BenchmarkCourses[(sample >> 24) % (uint)BenchmarkCourses.Length])
                : new PersonStorage.Employee(id, name, age, BenchmarkDepartments[(sample >> 24) % (uint)BenchmarkDepartments.Length]));
        }

        return persons;
    }

    private static Guid CreateDeterministicGuid(int ordinal)
    {
        var first = MixDeterministic(unchecked((ulong)(uint)ordinal + 0xE7037ED1A0B428DBUL));
        var second = MixDeterministic(first);
        return new Guid(
            unchecked((int)first),
            unchecked((short)(first >> 32)),
            unchecked((short)(first >> 48)),
            unchecked((byte)second),
            unchecked((byte)(second >> 8)),
            unchecked((byte)(second >> 16)),
            unchecked((byte)(second >> 24)),
            unchecked((byte)(second >> 32)),
            unchecked((byte)(second >> 40)),
            unchecked((byte)(second >> 48)),
            unchecked((byte)(second >> 56)));
    }

    private static ulong MixDeterministic(ulong value)
    {
        value = unchecked(value + 0x9E3779B97F4A7C15UL);
        value = unchecked((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL);
        value = unchecked((value ^ (value >> 27)) * 0x94D049BB133111EBUL);
        return value ^ (value >> 31);
    }

    private static List<IIndexedBenchmarkPerson> CloneIndexedPersons(IEnumerable<ContractPerson.IPerson> source)
    {
        return source
            .Select(person => person.Role == "Student"
                ? (IIndexedBenchmarkPerson)new IndexedBenchmarkStudent(person.Id, person.Name, person.Age, person.Role)
                : new IndexedBenchmarkEmployee(person.Id, person.Name, person.Age, person.Role))
            .ToList();
    }

    private static List<IIndexedBenchmarkPerson> CloneIndexedPersons(IEnumerable<IIndexedBenchmarkPerson> source)
    {
        return source
            .Select(person => person.Role == "Student"
                ? (IIndexedBenchmarkPerson)new IndexedBenchmarkStudent(person.Id, person.Name, person.Age, person.Role)
                : new IndexedBenchmarkEmployee(person.Id, person.Name, person.Age, person.Role))
            .ToList();
    }

    private sealed class DapperPersonEnvelope
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
        public string Role { get; set; } = string.Empty;
        public string Surname { get; set; } = string.Empty;
    }
}

[StorageScope(VersionTag = "1.0.0")]
[SearchIndex]
public interface IIndexedBenchmarkPerson : IStoredContract<IIndexedBenchmarkPerson>
{
    Guid Id { get; }
    string Name { get; }
    int Age { get; }
    string Role { get; }
}

[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class IndexedBenchmarkStudent : IIndexedBenchmarkPerson
{
    public IndexedBenchmarkStudent(Guid id, string name, int age, string role)
    {
        Id = id;
        Name = name;
        Age = age;
        Role = role;
    }

    public Guid Id { get; }
    public string Name { get; }
    public int Age { get; }
    public string Role { get; }

    [IgnoreMember]
    public IObjectEnvelope<IIndexedBenchmarkPerson>? Envelope { get; private set; }
}

[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class IndexedBenchmarkEmployee : IIndexedBenchmarkPerson
{
    public IndexedBenchmarkEmployee(Guid id, string name, int age, string role)
    {
        Id = id;
        Name = name;
        Age = age;
        Role = role;
    }

    public Guid Id { get; }
    public string Name { get; }
    public int Age { get; }
    public string Role { get; }

    [IgnoreMember]
    public IObjectEnvelope<IIndexedBenchmarkPerson>? Envelope { get; private set; }
}

public sealed class IndexedBenchmarkPersonOptions : IDbContractConfigDto<IIndexedBenchmarkPerson>
{
    public string EnvelopeDICacheKey { get; set; } = "IndexedBenchmarkPerson";
    public string ContractName { get; set; } = "IndexedBenchmarkPerson";
    public string? ImplementationsPath { get; set; } = null;
    public string? ContractPath { get; set; } = null;
    public string? MigrationsPath { get; set; } = null;
    public string FileExtension { get; set; } = ".mpk";
    public string? DataRoot { get; set; } = "Data";
    public string? ActiveVersion { get; set; } = "1.0.0";
    public ContractRepositoryType RepositoryType { get; set; } = ContractRepositoryType.FileSystem;
    public StorageObjectCardinality ObjectCardinality { get; set; } = StorageObjectCardinality.Multiple;
    public StoragePolicy? StoragePolicy { get; set; } =
        StoragePolicy.Default with { MaxConcurrency = Environment.ProcessorCount };
}

public sealed class TransactionBenchDbContext : DbContext
{
    public TransactionBenchDbContext(DbContextOptions<TransactionBenchDbContext> options)
        : base(options)
    {
    }

    public DbSet<TransactionPersonEntity> Persons => Set<TransactionPersonEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<TransactionPersonEntity>();
        entity.ToTable("Persons", schema: "dbo");
        entity.HasKey(person => person.Id);
        entity.Property(person => person.Name).HasMaxLength(200).IsRequired();
        entity.Property(person => person.Age).IsRequired();
        entity.Property(person => person.Role).HasMaxLength(100).IsRequired();
        entity.Property(person => person.Surname).HasMaxLength(100);
    }
}

public sealed class TransactionPersonEntity
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Age { get; set; }

    public string Role { get; set; } = string.Empty;

    public string? Surname { get; set; }
}
