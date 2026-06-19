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
using Idb.Libraries.Implementation.Extentions;
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

try
{
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
    private static readonly string[] QueryRoles = { "Student", "Employee" };

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
        _seedPersons = PersonDataFactory.CreatePersons(_benchmarkPersonCount);
        _seedIndexedPersons = CreateIndexedPersons(_benchmarkPersonCount);
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
                    benchmark.SetupIndexedFileSystemQuery();
                    _ = benchmark.FileSystem_SearchIndexQueryBatch().GetAwaiter().GetResult();
                    break;
                case "filesystem-indexed-query-hot":
                case "file-indexed-query-hot":
                    benchmark.SetupIndexedFileSystemQuery();
                    for (var iteration = 0; iteration < 100; iteration++)
                    {
                        _ = benchmark.FileSystem_SearchIndexQueryBatch().GetAwaiter().GetResult();
                    }

                    break;
                case "filesystem-selective-indexed-query":
                case "file-selective-indexed-query":
                    benchmark.SetupIndexedFileSystemQuery();
                    _ = benchmark.FileSystem_SearchIndexSelectiveQueryBatch().GetAwaiter().GetResult();
                    break;
                case "filesystem-selective-indexed-query-hot":
                case "file-selective-indexed-query-hot":
                    benchmark.SetupIndexedFileSystemQuery();
                    for (var iteration = 0; iteration < 100; iteration++)
                    {
                        _ = benchmark.FileSystem_SearchIndexSelectiveQueryBatch().GetAwaiter().GetResult();
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

    [GlobalSetup(Target = nameof(FileSystem_QueryBatch))]
    public void SetupFileSystemQuery()
    {
        _fileDataRoot = Path.Combine(ResolveBenchmarkRoot(), "interface-db-query", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fileDataRoot);
        _fileProvider = BuildFileSystemProvider(_fileDataRoot, keepObjectsOnWrite: false);
        _queryRepository = _fileProvider.GetRequiredService<IRepository<ContractPerson.IPerson>>();
        _queryRepository.InitializeAsync().GetAwaiter().GetResult();
        _queryRepository.SaveManyAsync(PersonDataFactory.ClonePersons(_seedPersons)).GetAwaiter().GetResult();
        var validationCount = CountInterfaceDbMatchesAsync(_queryRepository, "Student", 20)
            .GetAwaiter()
            .GetResult();
        if (validationCount <= 0)
        {
            throw new InvalidOperationException("InterfaceDB query benchmark setup produced no queryable records.");
        }
    }

    [GlobalSetup(Targets = new[] { nameof(FileSystem_SearchIndexQueryBatch), nameof(FileSystem_SearchIndexSelectiveQueryBatch) })]
    public void SetupIndexedFileSystemQuery()
    {
        _indexedFileDataRoot = Path.Combine(ResolveBenchmarkRoot(), "interface-db-indexed-query", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_indexedFileDataRoot);
        _indexedFileProvider = BuildIndexedFileSystemProvider(_indexedFileDataRoot, keepObjectsOnWrite: false);
        _indexedQueryRepository = _indexedFileProvider.GetRequiredService<IRepository<IIndexedBenchmarkPerson>>();
        _indexedQueryRepository.InitializeAsync().GetAwaiter().GetResult();
        _indexedQueryRepository.SaveManyAsync(CloneIndexedPersons(_seedIndexedPersons)).GetAwaiter().GetResult();
        var validationCount = CountIndexedInterfaceDbMatchesAsync(_indexedQueryRepository, "Student", 20)
            .GetAwaiter()
            .GetResult();
        if (validationCount <= 0)
        {
            throw new InvalidOperationException("Indexed InterfaceDB query benchmark setup produced no queryable records.");
        }
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
        var validationCount = MaterializeSqlPersons(_sqlQueryConnection, "Student", 20).Count;
        if (validationCount <= 0)
        {
            throw new InvalidOperationException("SQL Server query benchmark setup produced no queryable records.");
        }
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

        var validationCount = QueryDapperPersons(_dapperQueryConnection, "Student", 20).Count;
        if (validationCount <= 0)
        {
            throw new InvalidOperationException("Dapper query benchmark setup produced no queryable records.");
        }
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

        var validationCount = QueryEfCorePersons(_efQueryOptions, "Student", 20).Count;
        if (validationCount <= 0)
        {
            throw new InvalidOperationException("EF Core query benchmark setup produced no queryable records.");
        }
    }

    [Benchmark(Description = "InterfaceDB FileEnvelopeRepository + PassthroughIndexManager - 100 predicate scans over configured persons")]
    public async Task<int> FileSystem_QueryBatch()
    {
        var repository = _queryRepository ?? throw new InvalidOperationException("File-system query repository not initialized.");
        var total = 0;

        for (var i = 0; i < QueryRepeatCount; i++)
        {
            var role = QueryRoles[i % QueryRoles.Length];
            var minimumAge = 20 + (i % 30);
            await foreach (var _ in repository.QueryManyAsync(person => person.Role == role && person.Age >= minimumAge))
            {
                total++;
            }
        }

        return total;
    }

    [Benchmark(Description = "SQL Server ADO.NET + SQL indexes - 100 materialized queries over configured persons")]
    public int SqlServer_QueryBatch()
    {
        if (_sqlQueryConnection is null)
        {
            throw new InvalidOperationException("SQL Server query connection not initialized.");
        }

        var total = 0;
        for (var i = 0; i < QueryRepeatCount; i++)
        {
            var role = QueryRoles[i % QueryRoles.Length];
            var minimumAge = 20 + (i % 30);
            total += MaterializeSqlPersons(_sqlQueryConnection, role, minimumAge).Count;
        }

        return total;
    }

    [Benchmark(Description = "Dapper SQL Server + SQL indexes - 100 materialized queries over configured persons")]
    public int Dapper_QueryBatch()
    {
        if (_dapperQueryConnection is null)
        {
            throw new InvalidOperationException("Dapper query connection not initialized.");
        }

        var total = 0;
        for (var i = 0; i < QueryRepeatCount; i++)
        {
            var role = QueryRoles[i % QueryRoles.Length];
            var minimumAge = 20 + (i % 30);
            total += QueryDapperPersons(_dapperQueryConnection, role, minimumAge).Count;
        }

        return total;
    }

    [Benchmark(Description = "Entity Framework Core SQL Server + SQL indexes - 100 materialized queries over configured persons")]
    public int EfCore_QueryBatch()
    {
        var options = _efQueryOptions ?? throw new InvalidOperationException("EF Core query options not initialized.");
        var total = 0;
        for (var i = 0; i < QueryRepeatCount; i++)
        {
            var role = QueryRoles[i % QueryRoles.Length];
            var minimumAge = 20 + (i % 30);
            total += QueryEfCorePersons(options, role, minimumAge).Count;
        }

        return total;
    }

    [Benchmark(Description = "InterfaceDB FileEnvelopeRepository + SearchIndexManager - 100 indexed searches over configured persons")]
    public async Task<int> FileSystem_SearchIndexQueryBatch()
    {
        var repository = _indexedQueryRepository ?? throw new InvalidOperationException("Indexed file-system query repository not initialized.");
        var total = 0;

        for (var i = 0; i < QueryRepeatCount; i++)
        {
            var role = QueryRoles[i % QueryRoles.Length];
            var minimumAge = 20 + (i % 30);
            await foreach (var _ in repository.SearchManyAsync(person => person.Role == role && person.Age >= minimumAge))
            {
                total++;
            }
        }

        return total;
    }

    [Benchmark(Description = "InterfaceDB FileEnvelopeRepository + SearchIndexManager - 100 selective indexed searches over configured persons")]
    public async Task<int> FileSystem_SearchIndexSelectiveQueryBatch()
    {
        var repository = _indexedQueryRepository ?? throw new InvalidOperationException("Indexed file-system query repository not initialized.");
        var total = 0;

        for (var i = 0; i < QueryRepeatCount; i++)
        {
            if ((i & 1) == 0)
            {
                var id = _seedIndexedPersons[i % _seedIndexedPersons.Count].Id;
                await foreach (var _ in repository.SearchManyAsync(person => person.Id == id))
                {
                    total++;
                }
            }
            else
            {
                var role = QueryRoles[i % QueryRoles.Length];
                await foreach (var _ in repository.SearchManyAsync(person => person.Role == role && person.Age >= 58))
                {
                    total++;
                }
            }
        }

        return total;
    }

    [Benchmark(Description = "SQL Server ADO.NET + SQL indexes - 100 selective materialized queries over configured persons")]
    public int SqlServer_SelectiveQueryBatch()
    {
        if (_sqlQueryConnection is null)
        {
            throw new InvalidOperationException("SQL Server query connection not initialized.");
        }

        var total = 0;
        for (var i = 0; i < QueryRepeatCount; i++)
        {
            if ((i & 1) == 0)
            {
                var id = _seedPersons[i % _seedPersons.Count].Id;
                total += MaterializeSqlPersonById(_sqlQueryConnection, id).Count;
            }
            else
            {
                var role = QueryRoles[i % QueryRoles.Length];
                total += MaterializeSqlPersons(_sqlQueryConnection, role, 58).Count;
            }
        }

        return total;
    }

    [Benchmark(Description = "Dapper SQL Server + SQL indexes - 100 selective materialized queries over configured persons")]
    public int Dapper_SelectiveQueryBatch()
    {
        if (_dapperQueryConnection is null)
        {
            throw new InvalidOperationException("Dapper query connection not initialized.");
        }

        var total = 0;
        for (var i = 0; i < QueryRepeatCount; i++)
        {
            if ((i & 1) == 0)
            {
                var id = _seedPersons[i % _seedPersons.Count].Id;
                total += QueryDapperPersonById(_dapperQueryConnection, id).Count;
            }
            else
            {
                var role = QueryRoles[i % QueryRoles.Length];
                total += QueryDapperPersons(_dapperQueryConnection, role, 58).Count;
            }
        }

        return total;
    }

    [Benchmark(Description = "Entity Framework Core SQL Server + SQL indexes - 100 selective materialized queries over configured persons")]
    public int EfCore_SelectiveQueryBatch()
    {
        var options = _efQueryOptions ?? throw new InvalidOperationException("EF Core query options not initialized.");
        var total = 0;
        for (var i = 0; i < QueryRepeatCount; i++)
        {
            if ((i & 1) == 0)
            {
                var id = _seedPersons[i % _seedPersons.Count].Id;
                total += QueryEfCorePersonById(options, id).Count;
            }
            else
            {
                var role = QueryRoles[i % QueryRoles.Length];
                total += QueryEfCorePersons(options, role, 58).Count;
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
        bool keepObjectsOnWrite = false)
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
            Indexes = Array.Empty<IIndex<ContractPerson.IPerson>>(),
            IndexProvisioningMode = IndexProvisioningModeType.None,
            RepositoryType = ContractRepositoryType.FileSystem,
            StoragePolicy = StoragePolicy.Default with
            {
                HydrateOnInitialize = false,
                MaxConcurrency = Math.Min(maxConcurrency, Environment.ProcessorCount),
                BulkWriteBatchSize = bulkBatchSize,
                FileWriteBufferSize = 16 * 1024,
                RaiseNotifications = false,
                KeepObjectInstanceInCacheOnWrite = keepObjectsOnWrite,
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
        bool keepObjectsOnWrite = false)
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
            Indexes = Array.Empty<IIndex<IIndexedBenchmarkPerson>>(),
            IndexProvisioningMode = IndexProvisioningModeType.None,
            RepositoryType = ContractRepositoryType.FileSystem,
            StoragePolicy = StoragePolicy.Default with
            {
                HydrateOnInitialize = false,
                MaxConcurrency = Math.Min(maxConcurrency, Environment.ProcessorCount),
                BulkWriteBatchSize = bulkBatchSize,
                FileWriteBufferSize = 16 * 1024,
                RaiseNotifications = false,
                KeepObjectInstanceInCacheOnWrite = keepObjectsOnWrite,
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

    private static async Task<int> CountInterfaceDbMatchesAsync(
        IRepository<ContractPerson.IPerson> repository,
        string role,
        int minimumAge)
    {
        var count = 0;
        await foreach (var _ in repository.QueryManyAsync(person => person.Role == role && person.Age >= minimumAge))
        {
            count++;
        }

        return count;
    }

    private static async Task<int> CountIndexedInterfaceDbMatchesAsync(
        IRepository<IIndexedBenchmarkPerson> repository,
        string role,
        int minimumAge)
    {
        var count = 0;
        await foreach (var _ in repository.SearchManyAsync(person => person.Role == role && person.Age >= minimumAge))
        {
            count++;
        }

        return count;
    }

    private static List<DapperPersonEnvelope> MaterializeSqlPersons(
        SqlConnection connection,
        string role,
        int minimumAge)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Age, Role, Surname
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
                Role = reader.GetString(3),
                Surname = reader.IsDBNull(4) ? string.Empty : reader.GetString(4)
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
            SELECT Id, Name, Age, Role, Surname
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
                Role = reader.GetString(3),
                Surname = reader.IsDBNull(4) ? string.Empty : reader.GetString(4)
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
            SELECT Id, Name, Age, Role, Surname
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
            SELECT Id, Name, Age, Role, Surname
            FROM dbo.Persons
            WHERE Id = @Id
            """;
        return connection
            .Query<DapperPersonEnvelope>(
                sql,
                new { Id = id })
            .AsList();
    }

    private static List<TransactionPersonEntity> QueryEfCorePersons(
        DbContextOptions<TransactionBenchDbContext> options,
        string role,
        int minimumAge)
    {
        using var context = new TransactionBenchDbContext(options);
        return context.Persons
            .AsNoTracking()
            .Where(person => person.Role == role && person.Age >= minimumAge)
            .ToList();
    }

    private static List<TransactionPersonEntity> QueryEfCorePersonById(
        DbContextOptions<TransactionBenchDbContext> options,
        Guid id)
    {
        using var context = new TransactionBenchDbContext(options);
        return context.Persons
            .AsNoTracking()
            .Where(person => person.Id == id)
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

    private static List<IIndexedBenchmarkPerson> CreateIndexedPersons(int count)
    {
        var source = PersonDataFactory.CreatePersons(count);
        return CloneIndexedPersons(source);
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
    public IIndex<IIndexedBenchmarkPerson>[]? Indexes { get; set; } = Array.Empty<IIndex<IIndexedBenchmarkPerson>>();
    public IndexProvisioningModeType IndexProvisioningMode { get; set; } = IndexProvisioningModeType.Dynamic;
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
