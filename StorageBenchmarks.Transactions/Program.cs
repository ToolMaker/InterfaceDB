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

TransactionBenchmarks.EnsureConfigurationInitialized();
BenchmarkRunner.Run<TransactionBenchmarks>();

[MemoryDiagnoser]
public class TransactionBenchmarks : IDisposable
{
    private const int BenchmarkPersonCount = 50_000;
    private readonly List<ContractPerson.IPerson> _seedPersons = PersonDataFactory.CreatePersons(BenchmarkPersonCount);

    private ServiceProvider? _fileProvider;
    private string? _fileDataRoot;
    private SqlConnection? _sqlConnection;
    private SqlConnection? _dapperConnection;
    private List<ContractPerson.IPerson>? _iterationPersons;

    internal static IConfiguration? Configuration { get; set; }

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
        var config = _fileProvider.GetRequiredService<IDbContractConfigDto<ContractPerson.IPerson>>();
        var policy = config.StoragePolicy ?? StoragePolicy.Default;
        await repository.SaveManyAsync(persons, policy).ConfigureAwait(false);
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

        _dapperConnection?.Dispose();
        _dapperConnection = null;

        _iterationPersons = null;
    }

    private static ServiceProvider BuildFileSystemProvider(string dataRoot)
    {
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
        };

        var services = new ServiceCollection();
        services.AddRepository<ContractPerson.IPerson>(options);

        return services.BuildServiceProvider();
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

    private sealed class DapperPersonEnvelope
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
        public string Role { get; set; } = string.Empty;
        public string Surname { get; set; } = string.Empty;
    }
}
