using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Dapper;
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

    if (args.Length == 0)
    {
        Console.WriteLine("InterfaceDB transaction benchmark runner");
        Console.WriteLine();
        Console.WriteLine("Run from a terminal so BenchmarkDotNet can report failures clearly:");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions");
        Console.WriteLine();
        Console.WriteLine("Quick SQL configuration check:");
        Console.WriteLine("  dotnet run --project StorageBenchmarks.Transactions -- --smoke-sql");
        Console.WriteLine();
        Console.WriteLine("Pass BenchmarkDotNet arguments after --, for example:");
        Console.WriteLine("  dotnet run -c Release --project StorageBenchmarks.Transactions -- --filter *FileSystem*");
        return;
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
    private const int BenchmarkPersonCount = 50_000;
    private const int QueryRepeatCount = 100;
    private const int FileSystemBenchmarkConcurrency = 16;
    private const int FileSystemBenchmarkBulkBatchSize = 4096;
    private const string MaxConcurrencyEnvironmentVariable = "INTERFACEDB_BENCH_MAX_CONCURRENCY";
    private const string BulkBatchSizeEnvironmentVariable = "INTERFACEDB_BENCH_BULK_BATCH_SIZE";
    private const string UseSingleWriteBufferEnvironmentVariable = "INTERFACEDB_BENCH_SINGLE_WRITE_BUFFER";
    private readonly List<ContractPerson.IPerson> _seedPersons = PersonDataFactory.CreatePersons(BenchmarkPersonCount);
    private static readonly string[] QueryRoles = { "Student", "Employee" };

    private ServiceProvider? _fileProvider;
    private string? _fileDataRoot;
    private SqlConnection? _sqlConnection;
    private SqlConnection? _sqlBulkConnection;
    private SqlConnection? _sqlQueryConnection;
    private SqlConnection? _dapperConnection;
    private List<ContractPerson.IPerson>? _iterationPersons;
    private IRepository<ContractPerson.IPerson>? _queryRepository;

    internal static IConfiguration? Configuration { get; set; }

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

    [GlobalSetup(Target = nameof(FileSystem_StoreTransaction))]
    public void SetupFileSystemAsync()
    {
        _fileDataRoot = Path.Combine(@"G:\PerformanceTest", "interface-db-transactions", Guid.NewGuid().ToString("N"));
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

    [Benchmark(Description = "Interface DB (transaction baseline) - store 50k persons")]
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

    [Benchmark(Description = "SQL Server (transaction) - store 50k persons")]
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

    [Benchmark(Description = "Dapper (transaction) - store 50k persons")]
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

    [Benchmark(Description = "SQL Server (SqlBulkCopy) - store 50k persons")]
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
        _fileProvider = BuildFileSystemProvider(_fileDataRoot, keepObjectsOnWrite: true);
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

    [Benchmark(Description = "Interface DB - 100 predicate scans over 50k persons")]
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

    [GlobalSetup(Target = nameof(SqlServer_QueryBatch))]
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
        using var command = _sqlQueryConnection.CreateCommand();
        command.CommandText = "SELECT COUNT_BIG(*) FROM dbo.Persons WHERE Role = N'Student' AND Age >= 20";
        var validationCount = Convert.ToInt64(command.ExecuteScalar());
        if (validationCount <= 0)
        {
            throw new InvalidOperationException("SQL Server query benchmark setup produced no queryable records.");
        }
    }

    [Benchmark(Description = "SQL Server - 100 indexed count queries over 50k persons")]
    public int SqlServer_QueryBatch()
    {
        if (_sqlQueryConnection is null)
        {
            throw new InvalidOperationException("SQL Server query connection not initialized.");
        }

        var total = 0;
        using var command = _sqlQueryConnection.CreateCommand();
        command.CommandText = "SELECT COUNT_BIG(*) FROM dbo.Persons WHERE Role = @role AND Age >= @minimumAge";
        command.Parameters.Add("@role", SqlDbType.NVarChar, 100);
        command.Parameters.Add("@minimumAge", SqlDbType.Int);

        for (var i = 0; i < QueryRepeatCount; i++)
        {
            command.Parameters["@role"].Value = QueryRoles[i % QueryRoles.Length];
            command.Parameters["@minimumAge"].Value = 20 + (i % 30);
            total += Convert.ToInt32(command.ExecuteScalar());
        }

        return total;
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _fileProvider?.Dispose();
        _fileProvider = null;

        if (_fileDataRoot is not null && Directory.Exists(_fileDataRoot))
        {
            Directory.Delete(_fileDataRoot, recursive: true);
        }

        _sqlConnection?.Dispose();
        _sqlConnection = null;

        _sqlBulkConnection?.Dispose();
        _sqlBulkConnection = null;

        _sqlQueryConnection?.Dispose();
        _sqlQueryConnection = null;

        _dapperConnection?.Dispose();
        _dapperConnection = null;

        _iterationPersons = null;
        _queryRepository = null;
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

    private sealed class DapperPersonEnvelope
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
        public string Role { get; set; } = string.Empty;
        public string Surname { get; set; } = string.Empty;
    }
}
