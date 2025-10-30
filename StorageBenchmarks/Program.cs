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
using Idb.Libraries.Abstraction.Services;
using Idb.Libraries.Abstraction.Services.Serialization;
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
    private IRepository<ContractPerson.IPerson> repository;
    private StoragePolicy policy;

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
        repository = _fileProvider.GetRequiredService<IRepository<ContractPerson.IPerson>>();
        repository.InitializeAsync().GetAwaiter().GetResult();
        var warmupPerson = PersonDataFactory.ClonePersons(new[] { _seedPersons[0] })[0];
        repository.SaveAsync(warmupPerson).GetAwaiter().GetResult();
        _iterationPersons = PersonDataFactory.ClonePersons(_seedPersons);
        var config = _fileProvider.GetRequiredService<IDbContractConfigDto<ContractPerson.IPerson, string>>();
        StoragePolicy policy = config.StoragePolicy ?? StoragePolicy.Default;
    }

    [Benchmark(Description = "File system repository")]
    public async Task FileSystem_Store100()
    {
        await repository.SaveManyAsync(_iterationPersons, StoragePolicy.Default).ConfigureAwait(false);
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

        var persons = _iterationPersons ?? throw new InvalidOperationException("Benchmark iteration has not been prepared.");
        _iterationPersons = null;

        var options = CreateParallelOptions(persons.Count);

        Parallel.ForEach(
            persons,
            options,
            () =>
            {
                var connection = CreateOpenConnection();
                var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO dbo.Persons(Id, Name, Age, Role, Surname) VALUES (@id, @name, @age, @role, @surname)";
                command.Parameters.Add("@id", SqlDbType.UniqueIdentifier);
                command.Parameters.Add("@name", SqlDbType.NVarChar, 200);
                command.Parameters.Add("@age", SqlDbType.Int);
                command.Parameters.Add("@role", SqlDbType.NVarChar, 100);
                command.Parameters.Add("@surname", SqlDbType.NVarChar, 100);
                return (Connection: connection, Command: command);
            },
            (person, _, state) =>
            {
                state.Command.Parameters["@id"].Value = person.Id;
                state.Command.Parameters["@name"].Value = person.Name;
                state.Command.Parameters["@age"].Value = person.Age;
                state.Command.Parameters["@role"].Value = person.Role;
                state.Command.Parameters["@surname"].Value = string.Empty;
                state.Command.ExecuteNonQuery();
                return state;
            },
            state =>
            {
                state.Command.Dispose();
                state.Connection.Dispose();
            });
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
    public Task Dapper_Store100()
    {
        if (_dapperConnection is null)
        {
            throw new InvalidOperationException("Dapper connection not initialized.");
        }

        var persons = _iterationPersons ?? throw new InvalidOperationException("Benchmark iteration has not been prepared.");
        _iterationPersons = null;

        const string insertSql = "INSERT INTO dbo.Persons(Id, Name, Age, Role, Surname) VALUES (@Id, @Name, @Age, @Role, @Surname)";
        var options = CreateParallelOptions(persons.Count);

        Parallel.ForEach(
            persons,
            options,
            () => CreateOpenConnection(),
            (person, _, connection) =>
            {
                connection.Execute(
                    insertSql,
                    new
                    {
                        person.Id,
                        person.Name,
                        person.Age,
                        person.Role,
                        Surname = string.Empty
                    });
                return connection;
            },
            connection => connection.Dispose());

        return Task.CompletedTask;
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
        services.AddSingleton<IDbContractConfigDto<ContractPerson.IPerson, string>>(options);
        services.AddSingleton<IEnvelopeKeyAndLocationFactory<ContractPerson.IPerson, string>, DefaultFileNameFactory<ContractPerson.IPerson>>();

        var serializerOptions = MessagePackSerializerOptions.Standard.WithResolver(MessagePack.Resolvers.TypelessContractlessStandardResolver.Instance);
        services.AddSingleton<IObjectSerializer<ContractPerson.IPerson, string>>(new BinaryObjectSerializer<ContractPerson.IPerson,string>(serializerOptions));

        services.AddTransient<IObjectEnvelope<ContractPerson.IPerson, string>, FileObjectEnvelope<ContractPerson.IPerson>>();
        services.AddSingleton<IEnvelopeLifecycle<ContractPerson.IPerson, string>, EnvelopeLifecycle<ContractPerson.IPerson, string>>();
        services.AddTransient<IObjectReader<ContractPerson.IPerson, string>, FileObjectReader<ContractPerson.IPerson>>();
        services.AddTransient<IObjectWriter<ContractPerson.IPerson, string>, FileObjectWriter<ContractPerson.IPerson>>();
        services.AddTransient<IReaderAndWriter<ContractPerson.IPerson, string>, ObjectReaderAndWriter<ContractPerson.IPerson, string>>();

        services.AddSingleton<IEnvelopeRepository<ContractPerson.IPerson, string>, FileEnvelopeRepository<ContractPerson.IPerson>>();
        services.AddSingleton<IIndexManager<ContractPerson.IPerson>, PassthroughIndexManager<ContractPerson.IPerson, string>>();
        services.AddSingleton<IRepository<ContractPerson.IPerson>>(sp =>
        {
            var manager = sp.GetRequiredService<IIndexManager<ContractPerson.IPerson>>();
            return new InterfaceRepositryDb<ContractPerson.IPerson, string>(manager);
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

    private static ParallelOptions CreateParallelOptions(int workloadSize)
    {
        var logicalProcessors = Environment.ProcessorCount;
        var desiredParallelism = logicalProcessors * 4;
        var maxDegree = Math.Min(desiredParallelism, workloadSize);
        if (maxDegree <= 0)
        {
            maxDegree = Math.Max(1, desiredParallelism);
        }

        return new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegree
        };
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
