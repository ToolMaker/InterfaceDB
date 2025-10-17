using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstraction.Enums;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extentions;
using Idb.Libraries.Implementation.Services.Caching;
using Idb.Libraries.Implementation.Services.Indexes;
using Idb.Libraries.Implementation.Services.Repository;
using IdbDemoStorage.Options;
using MigrationTool.Contracts.Person.v1_0_0;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Idb.Libraries.Abstraction.Dtos;

namespace IdbDemoStorage.ConsoleHost;

internal static class Program
{
    private const string PersonContractName = "Person";

    private static async Task<int> Main(string[] args)
    {
        // Step 1: Build configuration
        var configuration = BuildConfiguration();

        // Step 2: Build intermediate service provider to retrieve configured options
        var intermediateServices = new ServiceCollection();
        intermediateServices.AddSingleton<IConfiguration>(configuration);
        
        // Register and configure PersonOptions from appsettings.json
        intermediateServices.AddOptions<PersonOptions>()
            .Bind(configuration.GetSection(nameof(PersonOptions)));
        
        using var intermediateProvider = intermediateServices.BuildServiceProvider();
        
        // Step 3: Retrieve the configured PersonOptions
        var personOptions = intermediateProvider.GetRequiredService<IOptions<PersonOptions>>().Value;
        
        // Step 4: Normalize and resolve paths from the configured options
        var contractPath = NormalizePath(personOptions.ContractPath);
        var implementationPath = NormalizePath(personOptions.ImplementationsPath);
        var migrationPath = NormalizePath(personOptions.MigrationsPath);
        
        var contractsRoot = GetRootPath(contractPath);
        var implementationsRoot = GetRootPath(implementationPath);
        var migrationsRoot = GetRootPath(migrationPath);
        
        // Ensure all contract implementations are visible to DI consumers.
        intermediateServices.RegisterContractTransient(personOptions);
        var dataRoot = ResolveDataRoot(personOptions.DataRoot);
        Directory.CreateDirectory(dataRoot);

        // Single source of truth for the requested version
        var requestedVersion = string.IsNullOrWhiteSpace(personOptions.ActiveVersion) ? "1.0.0" : personOptions.ActiveVersion;

        System.Console.WriteLine("IDB Demo Storage Console");
        System.Console.WriteLine($"Contract path:         {contractPath}");
        System.Console.WriteLine($"Contracts root:        {contractsRoot}");
        System.Console.WriteLine($"Implementation path:   {implementationPath}");
        System.Console.WriteLine($"Implementations root:  {implementationsRoot}");
        System.Console.WriteLine($"Migration path:        {migrationPath}");
        System.Console.WriteLine($"Migrations root:       {migrationsRoot}");
        System.Console.WriteLine($"Data root:             {dataRoot}");
        System.Console.WriteLine($"Active version:        {requestedVersion}");
        System.Console.WriteLine();

        PrintAvailableVersions(contractsRoot, PersonContractName, requestedVersion);
        System.Console.WriteLine();
        PrintAvailableVersions(implementationsRoot, PersonContractName, requestedVersion);
        System.Console.WriteLine();

        var personContext = ResolveContractVersion(
            PersonContractName, 
            requestedVersion, 
            contractsRoot, 
            implementationsRoot);
        
        System.Console.WriteLine($"Active {PersonContractName} version: {personContext.Version}");
        System.Console.WriteLine();

        // Step 5: Build the final service provider with all registrations
        var contractOptions = new List<IDbContractConfigDto> { personOptions };
        using var provider = BuildServiceProvider(configuration, dataRoot, personContext, contractOptions);
        var repository = provider.GetRequiredService<IRepositoryNode>();
        await repository.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        var loadHandlers = provider.GetRequiredService<IEnumerable<IRepositoryLoadStrategyHandler>>();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        var loadLogger = loggerFactory.CreateLogger("RepositoryLoad");
        await RepositoryLoadStrategyExecutor.ExecuteAsync(
            loadHandlers,
            personOptions,
            personContext,
            repository,
            provider,
            loadLogger,
            CancellationToken.None).ConfigureAwait(false);

        var people = new[]
        {
            new Person("Ada", "Lovelace"),
            new Person("Alan", "Turing"),
            new Person("Grace", "Hopper")
        };

        System.Console.WriteLine();
        System.Console.WriteLine("Saving sample persons...");
        foreach (var person in people)
        {
            await repository
                .SaveAsync<IPerson>(person, cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);

            System.Console.WriteLine($"  Saved {person.FirstName} {person.LastName}");
        }

        System.Console.WriteLine();
        System.Console.WriteLine("Retrieving Ada via dynamic migration-aware load...");
        var adaResult = await repository
            .LoadAsync<IPerson>(p => p.LastName == "Lovelace", CancellationToken.None)
            .ConfigureAwait(false);
        System.Console.WriteLine($"  Loaded {adaResult.Value.FirstName} {adaResult.Value.LastName}");

        System.Console.WriteLine();
        System.Console.WriteLine("Checking existence of stored people...");
        foreach (var person in people)
        {
            var exists = await repository.ExistsAsync<IPerson>(person, CancellationToken.None).ConfigureAwait(false);
            System.Console.WriteLine($"  {person.FirstName} {person.LastName}: {(exists ? "present" : "missing")}");
        }

        return 0;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Contract options must define all paths.");
        }

        return Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path));
    }

    private static string GetRootPath(string path)
        => Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? path;

    private static IConfiguration BuildConfiguration()
        => new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

    private static ServiceProvider BuildServiceProvider(
        IConfiguration configuration,
        string dataRoot,
        ContractVersionContext personContext,
        IReadOnlyList<IDbContractConfigDto> contractOptions)
    {
        var services = new ServiceCollection();

        // Register configuration
        services.AddSingleton(configuration);

        // Register PersonOptions using IOptions pattern
        services.AddOptions<PersonOptions>()
            .Bind(configuration.GetSection("PersonOptions"));

        // Register contract options
        services.AddContractOptions(contractOptions);
        foreach (var option in contractOptions)
        {
            services.RegisterContractTransient(option);
        }

        var contractOption = FindContractOption(contractOptions, PersonContractName);

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
            builder.RootDirectory = new DirectoryInfo(dataRoot);
            builder.AssembliesToScan = new[]
            {
                personContext.ContractType.Assembly,
                personContext.ImplementationType.Assembly
            };
        });

        services.AddDefaultObjectWriterAndReader();

        services.AddSingleton<IRepositoryLoadStrategyHandler>(new NoOpRepositoryLoadStrategyHandler(RepositoryLoadStrategyType.None));
        services.AddSingleton<IRepositoryLoadStrategyHandler>(new NoOpRepositoryLoadStrategyHandler(RepositoryLoadStrategyType.Lazy));
        services.AddSingleton<IRepositoryLoadStrategyHandler, EagerWarmCacheRepositoryLoadStrategyHandler>();
        services.AddSingleton<IRepositoryLoadStrategyHandler>(sp => new LoggingOnlyRepositoryLoadStrategyHandler(
            RepositoryLoadStrategyType.IndexWarmup,
            "Repository load strategy {Strategy} for contract {Contract} initialises indexes on demand. No eager action performed."));
        services.AddSingleton<IRepositoryLoadStrategyHandler>(sp => new LoggingOnlyRepositoryLoadStrategyHandler(
            RepositoryLoadStrategyType.PartialPrefetch,
            "Repository load strategy {Strategy} for contract {Contract} is not yet implemented. Defaulting to lazy behaviour."));
        services.AddSingleton<IRepositoryLoadStrategyHandler>(sp => new LoggingOnlyRepositoryLoadStrategyHandler(
            RepositoryLoadStrategyType.BackgroundWarmup,
            "Repository load strategy {Strategy} for contract {Contract} is not yet implemented. Defaulting to lazy behaviour."));
        services.AddSingleton<IRepositoryLoadStrategyHandler>(sp => new LoggingOnlyRepositoryLoadStrategyHandler(
            RepositoryLoadStrategyType.AdaptiveCaching,
            "Repository load strategy {Strategy} for contract {Contract} is not yet implemented. Defaulting to lazy behaviour."));
        services.AddSingleton<IRepositoryLoadStrategyHandler>(sp => new LoggingOnlyRepositoryLoadStrategyHandler(
            RepositoryLoadStrategyType.SnapshotImport,
            "Repository load strategy {Strategy} for contract {Contract} is not yet implemented. Defaulting to lazy behaviour."));
        services.AddSingleton<IRepositoryLoadStrategyHandler>(sp => new LoggingOnlyRepositoryLoadStrategyHandler(
            RepositoryLoadStrategyType.MetadataOnly,
            "Repository load strategy {Strategy} for contract {Contract} only loads metadata. No payloads warmed."));

        services.AddSingleton<CachingRepositoryNode>(sp =>
        {
            var writer = sp.GetRequiredService<IObjectWriter>();
            var reader = sp.GetRequiredService<IObjectReader>();
            var inner = new FileSystemRepository(writer, reader);
            return new CachingRepositoryNode(inner);
        });

        services.AddSingleton<ProvisionedRepositoryNode>(sp =>
        {
            var cache = sp.GetRequiredService<CachingRepositoryNode>();
            return new ProvisionedRepositoryNode(cache, contractOption, personContext.ContractType);
        });

        services.AddSingleton<IRepositoryNode>(sp => sp.GetRequiredService<ProvisionedRepositoryNode>());
        services.AddSingleton<IRepository>(sp => sp.GetRequiredService<IRepositoryNode>());

        return services.BuildServiceProvider();
    }

    private static void ConfigureLastNameIndex(
        IRepositoryNode repository,
        ContractVersionContext context,
        IDbContractConfigDto contractOption)
    {
        var mode = contractOption.IndexProvisioningMode;
        var allowsDynamic = mode is ContractIndexProvisioningModeType.Dynamic
                                 or ContractIndexProvisioningModeType.Mixed
                                 or ContractIndexProvisioningModeType.None;

        var contractType = context.ContractType;
        var existing = repository
            .GetIndexConfigurations(contractType)
            .Any(c => string.Equals(c.Name, "ByLastName", StringComparison.OrdinalIgnoreCase));

        if (existing)
        {
            System.Console.WriteLine("ByLastName index already configured.");
            return;
        }

        if (!allowsDynamic)
        {
            System.Console.WriteLine($"Provisioning mode {mode} prevents registering the ByLastName index at runtime.");
            return;
        }

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

        var delegateType = typeof(Func<,>).MakeGenericType(contractType, typeof(string));
        var lambda = Expression.Lambda(delegateType, body, parameter);

        var indexType = typeof(SortedListIndexConfiguration<,>).MakeGenericType(contractType, typeof(string));
        if (Activator.CreateInstance(indexType, "ByLastName", lambda, null, null) is not IRepositoryIndexConfiguration configuration)
        {
            throw new InvalidOperationException("Failed to construct sorted-list index configuration.");
        }

        repository.ConfigureIndex(configuration);
        System.Console.WriteLine("Registered ByLastName sorted index at runtime.");
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

        var implementationType = contractAssembly
            .GetTypes()
            .FirstOrDefault(t =>
                t.IsClass &&
                !t.IsAbstract &&
                contractType.IsAssignableFrom(t) &&
                (t.Namespace?.Contains(desiredSegment, StringComparison.OrdinalIgnoreCase) ?? false));

        if (implementationType is null)
        {
            implementationType = contractAssembly
                .GetTypes()
                .FirstOrDefault(t => t.IsClass && !t.IsAbstract && contractType.IsAssignableFrom(t));
        }

        if (implementationType is null)
        {
            throw new InvalidOperationException($"Unable to locate implementation type for contract {contractType.FullName} (segment {desiredSegment}).");
        }

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

    private static string ResolveDataRoot(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.Combine(AppContext.BaseDirectory, "Data");
        }

        return Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath));
    }

    internal sealed record ContractVersionContext(
        Version Version,
        string Segment,
        Type ContractType,
        Type ImplementationType);

    private static IDbContractConfigDto FindContractOption(
        IReadOnlyList<IDbContractConfigDto> options,
        string contractName)
    {
        var option = options.FirstOrDefault(o =>
            string.Equals(o.ContractName, contractName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(o.Key, contractName, StringComparison.OrdinalIgnoreCase));

        return option ?? throw new InvalidOperationException($"No contract options were registered for '{contractName}'.");
    }

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
}

