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
using Idb.Libraries.Abstraction.Services.Cache;
using Idb.Libraries.Implementation.Cache;
using Idb.Libraries.Abstraction.Services;
using Idb.Libraries.Abstraction.Services.Serialization;
using Idb.Libraries.Implementation.Extentions;
using Idb.Libraries.Implementation.Indexes;
using Idb.Libraries.Implementation.Repositories;
using Idb.Libraries.Implementation.Serialization;
using IdbDemoStorage.Options;
using MessagePack;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using System.Threading;
using PerformanceCommon;
using System.Data;
using ContractPerson = MigrationTool.Contracts.Person.v1_0_0;

PersonStorageBenchmark.EnsureConfigurationInitialized();
BenchmarkRunner.Run<PersonStorageBenchmark>();

[MemoryDiagnoser]
public class PersonStorageBenchmark : IDisposable
{
    private const int BenchmarkPersonCount = 100000;
    private const int FileSystemBenchmarkConcurrency = 16;
    private const int FileSystemBenchmarkBulkBatchSize = 4096;
    private const string MaxConcurrencyEnvironmentVariable = "INTERFACEDB_BENCH_MAX_CONCURRENCY";
    private const string BulkBatchSizeEnvironmentVariable = "INTERFACEDB_BENCH_BULK_BATCH_SIZE";
    private const string UseSingleWriteBufferEnvironmentVariable = "INTERFACEDB_BENCH_SINGLE_WRITE_BUFFER";
    private readonly List<ContractPerson.IPerson> _seedPersons = PersonDataFactory.CreatePersons(BenchmarkPersonCount);

    private ServiceProvider? _fileProvider;
    private string? _fileDataRoot;
    private SqlConnection? _sqlConnection;
    private SqlConnection? _dapperConnection;
    private DbContextOptions<StorageBenchmarks.Ef.BenchDbContext>? _efOptions;
    private static int _efLoggedException;
    private List<ContractPerson.IPerson>? _iterationPersons;
    private IRepository<ContractPerson.IPerson> repository = null!;
    private StoragePolicy policy = StoragePolicy.Default;

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
        var config = _fileProvider.GetRequiredService<IDbContractConfigDto<ContractPerson.IPerson>>();
        policy = config.StoragePolicy ?? StoragePolicy.Default;
    }

    [Benchmark(Description = "File system repository")]
    public async Task FileSystem_Store100()
    {
        var persons = _iterationPersons ?? throw new InvalidOperationException("Benchmark iteration has not been prepared.");
        await repository.SaveManyAsync(persons).ConfigureAwait(false);
    }

    [GlobalSetup(Target = nameof(SqlServer_Store100))]
    public void SetupSqlServer()
    {
        _sqlConnection = CreateOpenConnection();
        EnsureSqlSchema(_sqlConnection);
        // Ensure a shared policy is available for all backends
        policy ??= StoragePolicy.Default with
        {
            // File path saturates disk at much lower DOP than DB pool
            MaxConcurrency = Math.Min(16, Environment.ProcessorCount),
            FileWriteBufferSize = 16 * 1024,
            HydrateOnInitialize = false,
            RaiseNotifications = false,
            UseSingleWriteBuffer = true,
            UseWriterSemaphore = false
        };
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

        var options = CreateParallelOptionsFromPolicy(policy, persons.Count);

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
        // Ensure a shared policy is available for all backends
        policy ??= StoragePolicy.Default with
        {
            MaxConcurrency = Math.Min(16, Environment.ProcessorCount),
            FileWriteBufferSize = 16 * 1024,
            HydrateOnInitialize = false,
            RaiseNotifications = false,
            UseSingleWriteBuffer = true,
            UseWriterSemaphore = false
        };
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
        var options = CreateParallelOptionsFromPolicy(policy, persons.Count);

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

    [GlobalSetup(Target = nameof(EFCore_Store100))]
    public void SetupEfCore()
    {
        try
        {
            SqlConnection.ClearAllPools();
            _efOptions = StorageBenchmarks.Ef.Ef.CreateOptions(ResolveSqlConnectionString());
            using (var conn = CreateOpenConnection())
            {
                EnsureSqlSchema(conn);
            }
            // Ensure shared policy
            policy ??= StoragePolicy.Default with
            {
                MaxConcurrency = Math.Min(Environment.ProcessorCount * 2, 64),
                FileWriteBufferSize = 8 * 1024,
                HydrateOnInitialize = false,
                RaiseNotifications = false
            };
        }
        catch (Exception ex)
        {
            TryLogEfError("SetupEfCore", ex);
            throw;
        }
    }

    [IterationSetup(Target = nameof(EFCore_Store100))]
    public void PrepareEfCoreIteration()
    {
        try
        {
            _iterationPersons = PersonDataFactory.ClonePersons(_seedPersons);
            using (var conn = CreateOpenConnection())
            {
                EnsureSqlSchema(conn);
            }
        }
        catch (Exception ex)
        {
            TryLogEfError("PrepareEfCoreIteration", ex);
            throw;
        }
    }

    [Benchmark(Description = "EF Core")]
    public void EFCore_Store100()
    {
        var persons = _iterationPersons ?? throw new InvalidOperationException("Benchmark iteration has not been prepared.");
        _iterationPersons = null;

        var dop = Math.Min((policy?.MaxConcurrency ?? Environment.ProcessorCount), 16);
        var po = new ParallelOptions { MaxDegreeOfParallelism = dop };
        Parallel.ForEach(
            persons,
            po,
            () => new StorageBenchmarks.Ef.BenchDbContext(_efOptions!),
            (person, _, ctx) =>
            {
                var entity = new StorageBenchmarks.Ef.PersonEntity
                {
                    Id = person.Id,
                    Name = person.Name,
                    Age = person.Age,
                    Role = person.Role,
                    Surname = string.Empty
                };
                try
                {
                    ctx.ChangeTracker.AutoDetectChangesEnabled = false;
                    ctx.Persons.Add(entity);
                    ctx.SaveChanges();
                }
                catch (Exception ex)
                {
                    if (Interlocked.CompareExchange(ref _efLoggedException, 1, 0) == 0)
                    {
                        Console.WriteLine("EF Core benchmark error: " + ex.ToString());
                        TryLogEfError("EFCore_Store100", ex);
                    }
                    throw;
                }
                finally
                {
                    ctx.ChangeTracker.AutoDetectChangesEnabled = true;
                }
                return ctx;
            },
            ctx => ctx.Dispose());
    }

    private static void TryLogEfError(string stage, Exception ex)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "ef-bench-error.txt");
            File.AppendAllText(path, $"[{DateTime.UtcNow:O}] {stage}: {ex}\n\n");
        }
        catch
        {
            // ignore logging failures
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
            ImplementationsPath = Path.Combine(Path.GetTempPath(), "interface-db-impl"),
            ContractPath = string.Empty,
            MigrationsPath = string.Empty,
            ActiveVersion = "1.0.0",
            Indexes = Array.Empty<IIndex<ContractPerson.IPerson>>(),
            IndexProvisioningMode = IndexProvisioningModeType.None,
            RepositoryType = ContractRepositoryType.FileSystem,
            StoragePolicy = StoragePolicy.Default with
            {
                HydrateOnInitialize = false,
                // Cap concurrency to avoid overwhelming disk while keeping the current fastest baseline.
                MaxConcurrency = Math.Min(maxConcurrency, Environment.ProcessorCount),
                BulkWriteBatchSize = bulkBatchSize,
                FileWriteBufferSize = 16 * 1024,
                RaiseNotifications = false,
                KeepObjectInstanceInCacheOnWrite = false,
                UseSingleWriteBuffer = useSingleWriteBuffer,
                UseWriterSemaphore = false
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
            throw new InvalidOperationException("Configure Benchmarks:SqlServer:ConnectionString in StorageBenchmarks/appsettings.json or environment variables.");
        }

        // Ensure reasonable pool/ssl settings for heavy parallel runs
        if (!connectionString.Contains("Max Pool Size", StringComparison.OrdinalIgnoreCase))
        {
            connectionString += ";Max Pool Size=200";
        }
        if (!connectionString.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase))
        {
            connectionString += ";TrustServerCertificate=True";
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

    private static ParallelOptions CreateParallelOptionsFromPolicy(StoragePolicy p, int workloadSize)
    {
        var dop = p?.MaxConcurrency ?? StoragePolicy.Default.MaxConcurrency;
        if (workloadSize > 0)
        {
            dop = Math.Min(dop, workloadSize);
        }
        if (dop <= 0) dop = 1;
        return new ParallelOptions { MaxDegreeOfParallelism = dop };
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
