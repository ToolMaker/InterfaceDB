using System.Linq;
using System.Reflection;
using System.Linq.Expressions;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extentions;
using Idb.Libraries.Implementation.Services.Caching;
using Idb.Libraries.Implementation.Services.Indexes;
using Idb.Libraries.Implementation.Services.Repository;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdbDemoStorage.ConsoleHost;

internal static class Program
{
    private const string PersonContractName = "Person";

    private static async Task<int> Main(string[] args)
    {
        var paths = DemoPaths.Discover();
        var configuration = BuildConfiguration();
        var contractSettings = StorageContractSettings.FromConfiguration(configuration);

        System.Console.WriteLine("IDB Demo Storage Console");
        System.Console.WriteLine($"Contracts root:        {paths.ContractsRoot}");
        System.Console.WriteLine($"Implementations root:  {paths.ImplementationsRoot}");
        System.Console.WriteLine($"Migrations root:       {paths.MigrationsRoot}");
        System.Console.WriteLine($"Data root:             {paths.DataRoot}");
        System.Console.WriteLine();

        PrintAvailableVersions(paths.ContractsRoot, PersonContractName, contractSettings.Person.RunningVersion);
        System.Console.WriteLine();
        PrintAvailableVersions(paths.ImplementationsRoot, PersonContractName, contractSettings.Person.RunningVersion);
        System.Console.WriteLine();

        var personContext = ResolveContractVersion(PersonContractName, contractSettings.Person.RunningVersion, paths.ContractsRoot, paths.ImplementationsRoot);
        System.Console.WriteLine($"Active {PersonContractName} version: {personContext.Version}");
        System.Console.WriteLine();

        using var provider = BuildServiceProvider(paths, personContext, contractSettings);
        var repository = provider.GetRequiredService<CachingRepositoryNode>();

        var indexes = repository.GetIndexConfigurations(personContext.ContractType);
        System.Console.WriteLine($"Configured indexes for {personContext.ContractType.FullName}: {indexes.Count}");

        await Task.CompletedTask.ConfigureAwait(false);
        return 0;
    }

    private static IConfiguration BuildConfiguration()
        => new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

    private static ServiceProvider BuildServiceProvider(
        DemoPaths paths,
        ContractVersionContext personContext,
        StorageContractSettings contractSettings)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
        });

        services.AddLocalScopeStorage(builder =>
        {
            builder.RootDirectory = new DirectoryInfo(paths.DataRoot);
            builder.AssembliesToScan = new[]
            {
                personContext.ContractType.Assembly,
                personContext.ImplementationType.Assembly
            };
        });

        services.AddDefaultObjectWriterAndReader();

        services.AddSingleton(sp =>
        {
            var writer = sp.GetRequiredService<IObjectWriter>();
            var reader = sp.GetRequiredService<IObjectReader>();
            var inner = new FileSystemRepository(writer, reader);
            var cache = new CachingRepositoryNode(inner);

            ConfigureLastNameIndex(cache, personContext);

            return cache;
        });

        return services.BuildServiceProvider();
    }

    private static void ConfigureLastNameIndex(CachingRepositoryNode cache, ContractVersionContext context)
    {
        var contractType = context.ContractType;
        var property = contractType.GetProperty("LastName");
        if (property is null)
        {
            System.Console.WriteLine($"Warning: {contractType.FullName} does not expose a LastName property. Skipping ByLastName index.");
            return;
        }

        var parameter = Expression.Parameter(contractType, "p");
        Expression body = Expression.Property(parameter, property);
        if (body.Type != typeof(string))
        {
            body = Expression.Convert(body, typeof(string));
        }

        var lambda = Expression.Lambda(body, parameter);
        var delegateInstance = lambda.Compile();

        var indexType = typeof(SortedListIndexConfiguration<,>).MakeGenericType(contractType, typeof(string));
        var indexInstance = Activator.CreateInstance(indexType, "ByLastName", delegateInstance, null, null);

        var configureIndexMethod = typeof(CachingRepositoryNode).GetMethod(nameof(CachingRepositoryNode.ConfigureIndex))
            ?? throw new InvalidOperationException("Unable to locate ConfigureIndex method.");

        configureIndexMethod.Invoke(cache, new[] { indexInstance });
    }

    private static ContractVersionContext ResolveContractVersion(
        string contractName,
        string requestedVersion,
        string contractsRoot,
        string implementationsRoot)
    {
        var availableContracts = EnumerateVersions(contractsRoot, contractName).ToList();
        if (availableContracts.Count == 0)
        {
            throw new InvalidOperationException($"No versions found for contract {contractName} under {contractsRoot}.");
        }

        var availableImplementations = EnumerateVersions(implementationsRoot, contractName).ToDictionary(v => v.Segment, v => v.Version);

        if (!TryParseVersion(requestedVersion, out var desiredVersion) || !availableContracts.Any(v => v.Version == desiredVersion))
        {
            desiredVersion = availableContracts.OrderByDescending(v => v.Version).First().Version;
            System.Console.WriteLine($"Requested version '{requestedVersion}' not found. Falling back to highest available {desiredVersion}.");
        }

        var desiredSegment = ToSegment(desiredVersion);
        if (!availableImplementations.ContainsKey(desiredSegment))
        {
            throw new InvalidOperationException($"Implementation for {contractName} {desiredSegment} was not found under {implementationsRoot}.");
        }

        var contractAssembly = Assembly.Load("IdbDemoStorage");
        var contractTypeName = $"MigrationTool.Contracts.{contractName}.{desiredSegment}.I{contractName}";
        var contractType = contractAssembly.GetType(contractTypeName)
            ?? throw new InvalidOperationException($"Unable to locate contract type {contractTypeName}.");

        var implementationTypeName = $"IdbDemoStorage.Implementations.{contractName}.{desiredSegment}.{contractName}";
        var implementationType = contractAssembly.GetType(implementationTypeName)
            ?? throw new InvalidOperationException($"Unable to locate implementation type {implementationTypeName}.");

        return new ContractVersionContext(desiredVersion, desiredSegment, contractType, implementationType);
    }

    private static IEnumerable<(Version Version, string Segment)> EnumerateVersions(string root, string contractName)
    {
        var contractRoot = Path.Combine(root, contractName);
        if (!Directory.Exists(contractRoot))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(contractRoot))
        {
            var segment = Path.GetFileName(directory);
            if (TryParseSegment(segment, out var version))
            {
                yield return (version, segment);
            }
        }
    }

    private static bool TryParseSegment(string segment, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(segment) || segment[0] != 'v')
        {
            return false;
        }

        var parts = segment[1..].Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var patch = parts.Length > 2 ? parts[2] : "0";
        if (int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor) && int.TryParse(patch, out var build))
        {
            version = new Version(major, minor, build);
            return true;
        }

        return false;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        if (Version.TryParse(value, out var parsed))
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    private static string ToSegment(Version version)
        => $"v{version.Major}_{version.Minor}_{Math.Max(version.Build, 0)}";

    private static void PrintAvailableVersions(string root, string contractName, string requestedVersion)
    {
        var activeSegment = TryParseVersion(requestedVersion, out var parsed) ? ToSegment(parsed) : string.Empty;
        var versions = EnumerateVersions(root, contractName).OrderBy(v => v.Version).ToList();
        if (versions.Count == 0)
        {
            System.Console.WriteLine($"No versions found for {contractName} under {root}");
            return;
        }

        System.Console.WriteLine($"Available {contractName} versions in {root}:");
        foreach (var (version, segment) in versions)
        {
            var marker = string.Equals(segment, activeSegment, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
            System.Console.WriteLine($" {marker} {segment} ({version})");
        }
    }

    private sealed record ContractVersionContext(
        Version Version,
        string Segment,
        Type ContractType,
        Type ImplementationType);

    private sealed record StorageContractSettings(StorageContractSettings.ContractSetting Person)
    {
        public static StorageContractSettings FromConfiguration(IConfiguration configuration)
        {
            var section = configuration.GetSection("StorageContracts");
            var person = section.GetSection(PersonContractName).Get<ContractSetting>() ?? new ContractSetting();
            return new StorageContractSettings(person.Effective());
        }

        public sealed record ContractSetting
        {
            public string RunningVersion { get; init; } = "1.0.0";

            public ContractSetting Effective()
                => string.IsNullOrWhiteSpace(RunningVersion)
                    ? new ContractSetting()
                    : this;
        }
    }

    private sealed record DemoPaths(
        string SolutionRoot,
        string DemoRoot,
        string ContractsRoot,
        string ImplementationsRoot,
        string MigrationsRoot,
        string DataRoot)
    {
        public static DemoPaths Discover()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null && !current.GetDirectories("IdbDemoStorage").Any())
            {
                current = current.Parent;
            }

            if (current is null)
            {
                throw new InvalidOperationException("Unable to locate IdbDemoStorage project directory.");
            }

            var demoRoot = Path.Combine(current.FullName, "IdbDemoStorage");
            var contractsRoot = Path.Combine(demoRoot, "Abstractions");
            var implementationsRoot = Path.Combine(demoRoot, "Implementations");
            var migrationsRoot = Path.Combine(demoRoot, "Migrations");
            var dataRoot = Path.Combine(demoRoot, "Data");
            Directory.CreateDirectory(dataRoot);

            return new DemoPaths(
                current.FullName,
                demoRoot,
                contractsRoot,
                implementationsRoot,
                migrationsRoot,
                dataRoot);
        }
    }
}

