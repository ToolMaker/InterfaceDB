using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Data;
using Dapper;
using ContractPerson = MigrationTool.Contracts.Person.v1_0_0;
using IdbDemoStorage.Options;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Factories;
using Idb.Libraries.Implementation.Indexes;
using Idb.Libraries.Implementation.Migrations;
using Idb.Libraries.Implementation.Repositories;
using Idb.Libraries.Implementation.Serialization;
using MessagePack;
using PerformanceCommon;

PersonStorageBenchmark.EnsureConfigurationInitialized();
BenchmarkRunner.Run<PersonStorageBenchmark>();

[MemoryDiagnoser]
public class PersonStorageBenchmark : IDisposable
{
    private const int BenchmarkPersonCount = 5_000;
    private readonly List<ContractPerson.IPerson> _seedPersons = PersonDataFactory.CreatePersons(BenchmarkPersonCount);

    private ServiceProvider? _fileProvider;
    private string? _fileDataRoot;
    private SqlConnection? _sqlConnection;
    private SqlConnection? _dapperConnection;
    private List<ContractPerson.IPerson>? _iterationPersons;

    internal static IConfiguration? Configuration { get; set; }

    [GlobalSetup(Target = nameof(FileSystem_Store100))]
    public void SetupFileSystemAsync()
    {
        _fileDataRoot = Path.Combine(@"G:\PerformanceTest", "interface-db-bench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fileDataRoot);
        _fileProvider = BuildFileSystemProvider(_fileDataRoot);
        var repository = _fileProvider.GetRequiredService<IRepository<ContractPerson.IPerson>>();
        repository.InitializeAsync().GetAwaiter().GetResult();
    }

    [IterationSetup(Target = nameof(FileSystem_Store100))]
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

    [Benchmark(Description = "File system repository")]
    public async Task FileSystem_Store100()
    {
        if (_fileProvider is null)
        {
            throw new InvalidOperationException("File-system provider not initialized.");
        }

        var persons = _iterationPersons ?? throw new InvalidOperationException("Benchmark iteration has not been prepared.");
        _iterationPersons = null;

        var repository = _fileProvider.GetRequiredService<IRepository<ContractPerson.IPerson>>();
        var config = _fileProvider.GetRequiredService<IDbContractConfigDto<ContractPerson.IPerson, FileInfo>>();
        var policy = config.StoragePolicy ?? StoragePolicy.Default;
        await repository.SaveManyAsync(persons, policy).ConfigureAwait(false);
    }

    [GlobalSetup(Target = nameof(SqlServer_Store100))]
    public void SetupSqlServer()
    {
        _sqlConnection = CreateOpenConnection();
        EnsureSqlSchema(_sqlConnection);
    }

    [IterationSetup(Target = nameof(SqlServer_Store100))]
    public void PrepareSqlServerIteration()
    {
        if (_sqlConnection is null)
        {
            throw new InvalidOperationException("SQL Server connection not initialized.");
        }

        EnsureSqlSchema(_sqlConnection);
        _iterationPersons = PersonDataFactory.ClonePersons(_seedPersons);
    }

    [Benchmark(Description = "SQL Server")]
    public void SqlServer_Store100()
    {
        if (_sqlConnection is null)
        {
            throw new InvalidOperationException("SQL Server connection not initialized.");
        }

        using var insert = _sqlConnection.CreateCommand();
        insert.CommandText = "INSERT INTO dbo.Persons(Id, Name, Age, Role, Surname) VALUES (@id, @name, @age, @role, @surname)";
        insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier);
        insert.Parameters.Add("@name", SqlDbType.NVarChar, 200);
        insert.Parameters.Add("@age", SqlDbType.Int);
        insert.Parameters.Add("@role", SqlDbType.NVarChar, 100);
        insert.Parameters.Add("@surname", SqlDbType.NVarChar, 100);

        var persons = _iterationPersons ?? throw new InvalidOperationException("Benchmark iteration has not been prepared.");
        _iterationPersons = null;

        foreach (var person in persons)
        {
            insert.Parameters["@id"].Value = person.Id;
            insert.Parameters["@name"].Value = person.Name;
            insert.Parameters["@age"].Value = person.Age;
            insert.Parameters["@role"].Value = person.Role;
            insert.Parameters["@surname"].Value = string.Empty;
            insert.ExecuteNonQuery();
        }
    }

    [GlobalSetup(Target = nameof(Dapper_Store100))]
    public void SetupDapper()
    {
        _dapperConnection = CreateOpenConnection();
        EnsureSqlSchema(_dapperConnection);
    }

    [IterationSetup(Target = nameof(Dapper_Store100))]
    public void PrepareDapperIteration()
    {
        if (_dapperConnection is null)
        {
            throw new InvalidOperationException("Dapper connection not initialized.");
        }

        EnsureSqlSchema(_dapperConnection);
        _iterationPersons = PersonDataFactory.ClonePersons(_seedPersons);
    }

    [Benchmark(Description = "Dapper")]
    public async Task Dapper_Store100()
    {
        if (_dapperConnection is null)
        {
            throw new InvalidOperationException("Dapper connection not initialized.");
        }

        var persons = _iterationPersons ?? throw new InvalidOperationException("Benchmark iteration has not been prepared.");
        _iterationPersons = null;

        const string insertSql = "INSERT INTO dbo.Persons(Id, Name, Age, Role, Surname) VALUES (@Id, @Name, @Age, @Role, @Surname)";
        foreach (var person in persons)
        {
            await _dapperConnection.ExecuteAsync(
                insertSql,
                new
                {
                    person.Id,
                    person.Name,
                    person.Age,
                    person.Role,
                    Surname = string.Empty
                }).ConfigureAwait(false);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Dispose();
    }

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
            ImplementationsPath = Path.Combine(Path.GetTempPath(), "interface-db-impl"),
            ContractPath = string.Empty,
            MigrationsPath = string.Empty,
            ActiveVersion = "1.0.0",
            Indexes = Array.Empty<IIndex<ContractPerson.IPerson>>(),
            IndexProvisioningMode = IndexProvisioningModeType.None,
            RepositoryType = ContractRepositoryType.FileSystem,
        };

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton<IDbContractConfigDto<ContractPerson.IPerson, FileInfo>>(options);
        services.AddSingleton<IFileNameFactory<ContractPerson.IPerson, FileInfo>, DefaultFileNameFactory<ContractPerson.IPerson>>();

        var serializerOptions = MessagePackSerializerOptions.Standard.WithResolver(MessagePack.Resolvers.TypelessContractlessStandardResolver.Instance);
        services.AddSingleton<IObjectSerializer<ContractPerson.IPerson>>(new BinaryObjectSerializer<ContractPerson.IPerson>(serializerOptions));

        services.AddSingleton<IEnvelopeRepository<ContractPerson.IPerson, FileInfo>, FileEnvelopeRepository<ContractPerson.IPerson>>();
        services.AddSingleton<IIndexManager<ContractPerson.IPerson>, PassthroughIndexManager<ContractPerson.IPerson>>();
        services.AddSingleton<IRepository<ContractPerson.IPerson>>(sp =>
        {
            var manager = sp.GetRequiredService<IIndexManager<ContractPerson.IPerson>>();
            return new InterfaceRepositryDb<ContractPerson.IPerson, FileInfo>(manager);
        });

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
            throw new InvalidOperationException("Configure Benchmarks:SqlServer:ConnectionString in StorageBenchmarks/appsettings.json or environment variables.");
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

}
