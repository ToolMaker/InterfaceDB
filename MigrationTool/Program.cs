using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Implementation.Services.Baselines;
using Idb.Libraries.Implementation.Services.Migrations;

namespace MigrationTool;

internal static class Program
{
    private const string DefaultContractsRoot = "Contracts";
    private const string DefaultStorageRoot = "Storage";
    private const string DefaultMigrationsRoot = "Migrations";
    private const string DefaultContractsNamespace = "MigrationTool.Contracts";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            await RunBaselineSequenceAsync().ConfigureAwait(false);
            return 0;
        }

        return await RunCliAsync(args).ConfigureAwait(false);
    }

    private static async Task<int> RunCliAsync(string[] args)
        => args[0].ToLowerInvariant() switch
        {
            "baseline" => HandleBaselineCommand(args),
            "baseline-sequence" => await RunBaselineSequenceAsync().ConfigureAwait(false),
            "migrate" => HandleMigrateCommand(args),
            _ => PrintUsage(),
        };

    private static int HandleBaselineCommand(string[] args)
    {
        if (!ContainsContractsDirOption(args))
        {
            Console.WriteLine("The '--contracts-dir' option is required when running the baseline command.");
            return PrintUsage();
        }

        var options = BaselineOptions.Parse(
            args,
            DefaultContractsNamespace,
            DefaultContractsRoot,
            DefaultStorageRoot,
            DefaultMigrationsRoot,
            BaselineExistingBehavior.Prompt,
            BaselineIncrementBehavior.Auto);

        if (string.IsNullOrWhiteSpace(options.ContractName))
        {
            Console.WriteLine("Contract name argument is required.");
            return 1;
        }

        var projectRoot = options.ProjectRoot ?? LocateProjectRoot();
        options = ResolveImplicitPaths(options, projectRoot);

        var contractsRoot = options.GetContractsRoot(projectRoot);
        var contractDirectory = Path.Combine(contractsRoot, options.ContractName);

        var baselines = BaselineGenerator.LoadBaselines(projectRoot, options);
        var previousBaseline = baselines.LastOrDefault();
        var previousVersion = previousBaseline?.Version;
        var targetVersion = BaselineGenerator.ResolveTargetVersion(previousVersion, options);
        var targetSegment = BaselineGenerator.NormalizeVersionSegment(targetVersion);
        var targetPath = Path.Combine(contractDirectory, targetSegment);
        var targetPathFull = Path.GetFullPath(targetPath);

        if (previousBaseline is not null)
        {
            Console.WriteLine($"Detected previous baseline {previousVersion}.");
            switch (options.IncrementBehavior)
            {
                case BaselineIncrementBehavior.Skip:
                    Console.WriteLine("Increment generation skipped (increment behavior set to skip).");
                    return 0;
                case BaselineIncrementBehavior.Prompt:
                    if (!PromptForIncrement(previousVersion!, targetVersion))
                    {
                        Console.WriteLine("Baseline generation cancelled.");
                        return 0;
                    }
                    break;
                case BaselineIncrementBehavior.Auto:
                    break;
            }
            Console.WriteLine($"Incrementing to version {targetVersion}.");
        }
        else
        {
            Console.WriteLine($"Creating initial baseline version {targetVersion}.");
        }

        var storageRoot = options.GetStorageRoot(projectRoot);
        var storageRootFull = Path.GetFullPath(storageRoot);
        var storageExisted = Directory.Exists(storageRoot);
        var migrationRoot = options.GetMigrationRoot(projectRoot);
        var migrationRootFull = Path.GetFullPath(migrationRoot);

        var existingBehavior = options.ExistingBehavior;
        if (Directory.Exists(targetPath))
        {
            switch (existingBehavior)
            {
                case BaselineExistingBehavior.Prompt:
                    var decision = PromptForExisting(targetPathFull);
                    if (decision == BaselineExistingBehavior.Skip)
                    {
                        Console.WriteLine($"Baseline generation skipped. Folder '{targetPathFull}' already exists.");
                        return 0;
                    }

                    options = options with { ExistingBehavior = BaselineExistingBehavior.Force };
                    break;

                case BaselineExistingBehavior.Skip:
                    Console.WriteLine($"Baseline generation skipped. Folder '{targetPathFull}' already exists.");
                    return 0;

                case BaselineExistingBehavior.Force:
                    break;
            }
        }

        Directory.CreateDirectory(storageRoot);

        var result = BaselineGenerator.Generate(projectRoot, options);
        ScaffoldContractOptions(projectRoot, options, result);

        Console.WriteLine($"Baseline created for {options.ContractName} @ {result.NewVersion} (namespace {result.Namespace}).");
        Console.WriteLine($"Contract baseline folder: {targetPathFull}");
        var contractFiles = Directory.Exists(targetPath)
            ? Directory.GetFiles(targetPath, "*.cs", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();
        foreach (var file in contractFiles)
        {
            Console.WriteLine($"  - {Path.GetFileName(file)}");
        }

        var storageVersionFolder = Path.Combine(storageRootFull, options.ContractName, targetSegment);
        Directory.CreateDirectory(storageVersionFolder);
        Console.WriteLine($"Storage version folder: {storageVersionFolder}");

        var migrationContractFolder = Path.Combine(migrationRootFull, options.ContractName);
        Directory.CreateDirectory(migrationContractFolder);
        Console.WriteLine($"Migration folder: {migrationContractFolder}");

        if (storageExisted)
        {
            Console.WriteLine($"Storage root detected: {storageRootFull}");
        }
        else
        {
            Console.WriteLine($"Storage root initialized: {storageRootFull}");
        }

        if (!string.IsNullOrWhiteSpace(result.PreviousVersion))
        {
            Console.WriteLine($"Incremented from version {result.PreviousVersion} to {result.NewVersion}.");

            var updatedBaselines = BaselineGenerator.LoadBaselines(projectRoot, options);
            using var migrationLog = new StringWriter();
            var migrationsCreated = MigrationScaffolder.ScaffoldMigrations(projectRoot, options, updatedBaselines, migrationLog);
            var migrationOutput = migrationLog.ToString();
            if (!string.IsNullOrWhiteSpace(migrationOutput))
            {
                Console.Write(migrationOutput);
            }
            else if (migrationsCreated)
            {
                Console.WriteLine($"Migration scaffolds written under {migrationRootFull}.");
            }
        }

        Console.WriteLine("Next steps:");
        Console.WriteLine("  1. Flesh out the interface and implementations under the new versioned namespace.");
        Console.WriteLine("  2. Add or update migration classes linking the previous baseline to this version.");

        return 0;
    }

    private static int HandleMigrateCommand(string[] args)
    {
        if (args.Length < 3 || !string.Equals(args[1], "scaffold", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Usage: dotnet run --project MigrationTool -- migrate scaffold <ContractName> --contracts-dir <path> [--dbpath path] [--storage-dir path] [--migration-dir path]");
            return 1;
        }

        var contractName = args[2];

        var normalizedArgs = new List<string> { "baseline", contractName };
        if (args.Length > 3)
        {
            normalizedArgs.AddRange(args[3..]);
        }

        if (!ContainsContractsDirOption(normalizedArgs))
        {
            Console.WriteLine("The '--contracts-dir' option is required when scaffolding migrations.");
            return 1;
        }

        var baselineOptions = BaselineOptions.Parse(
            normalizedArgs.ToArray(),
            DefaultContractsNamespace,
            DefaultContractsRoot,
            DefaultStorageRoot,
            DefaultMigrationsRoot,
            BaselineExistingBehavior.Prompt,
            BaselineIncrementBehavior.Auto);

        var projectRoot = baselineOptions.ProjectRoot ?? LocateProjectRoot();
        baselineOptions = ResolveImplicitPaths(baselineOptions, projectRoot);
        var contractRoot = Path.Combine(baselineOptions.GetContractsRoot(projectRoot), contractName);

        if (!Directory.Exists(contractRoot))
        {
            Console.WriteLine($"Contract '{contractName}' has no baselines yet. Run the baseline command first.");
            return 1;
        }

        var baselines = BaselineGenerator.LoadBaselines(projectRoot, baselineOptions);

        if (baselines.Count < 2)
        {
            Console.WriteLine("At least two baselines are required to scaffold migrations.");
            return 1;
        }

        MigrationScaffolder.ScaffoldMigrations(projectRoot, baselineOptions, baselines);
        return 0;
    }

    private static void ScaffoldContractOptions(string projectRoot, BaselineOptions options, BaselineResult result)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        var contractsRoot = options.GetContractsRoot(projectRoot);
        var contractsRootFull = Path.GetFullPath(contractsRoot);
        var trimmedContracts = Path.TrimEndingDirectorySeparator(contractsRootFull);
        var optionsBase = Directory.GetParent(trimmedContracts)?.FullName ?? trimmedContracts;

        var optionsDirectory = Path.Combine(optionsBase, "Options");
        Directory.CreateDirectory(optionsDirectory);

        var className = options.ContractName + "Options";
        var filePath = Path.Combine(optionsDirectory, className + ".cs");
        if (File.Exists(filePath))
        {
            Console.WriteLine($"Contract options already exist at '{filePath}'. Skipping creation.");
            return;
        }

        var optionsBaseFull = Path.GetFullPath(optionsBase);

        static string ToVerbatimLiteral(string basePath, string targetFullPath)
        {
            var baseFull = Path.GetFullPath(basePath);
            var relative = Path.GetRelativePath(baseFull, targetFullPath);
            if (string.IsNullOrWhiteSpace(relative))
            {
                relative = ".";
            }

            return "@\"" + relative.Replace("\"", "\"\"") + "\"";
        }

        var versionSegment = BaselineGenerator.NormalizeVersionSegment(new Version(result.NewVersion));
        var storageRootFull = Path.GetFullPath(options.GetStorageRoot(projectRoot));
        var contractsRootResolved = Path.GetFullPath(options.GetContractsRoot(projectRoot));
        var migrationsRootFull = Path.GetFullPath(options.GetMigrationRoot(projectRoot));

        var implementationsPath = Path.Combine(storageRootFull, options.ContractName, versionSegment);
        var contractPath = Path.Combine(contractsRootResolved, options.ContractName, versionSegment);
        var migrationsPath = Path.Combine(migrationsRootFull, options.ContractName);

        Directory.CreateDirectory(contractPath);
        Directory.CreateDirectory(implementationsPath);
        Directory.CreateDirectory(migrationsPath);

        var namespaceOwner = new DirectoryInfo(optionsBaseFull).Name;
        var namespaceName = namespaceOwner + ".Options";

        var lines = new[]
        {
            "using System;",
            "using Idb.Libraries.Abstractions.Options;",
            "using Idb.Libraries.Abstractions.Services.Indexes;",
            string.Empty,
            $"namespace {namespaceName};",
            string.Empty,
            $"public sealed class {className} : IDbContractOptions",
            "{",
            $"    public string? ImplementationsPath {{ get; }} = {ToVerbatimLiteral(optionsBaseFull, implementationsPath)};",
            $"    public string? ContractPath {{ get; }} = {ToVerbatimLiteral(optionsBaseFull, contractPath)};",
            $"    public string? MigrationsPath {{ get; }} = {ToVerbatimLiteral(optionsBaseFull, migrationsPath)};",
            $"    public string? ActiveVersion {{ get; }} = \"{result.NewVersion}\";",
            "    public IRepositoryIndexConfiguration[]? Indexes { get; } = Array.Empty<IRepositoryIndexConfiguration>();",
            "    public ContractIndexProvisioningMode IndexProvisioningMode { get; } = ContractIndexProvisioningMode.Dynamic;",
            "    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;",
            "}",
            string.Empty,
        };

        File.WriteAllLines(filePath, lines);
        Console.WriteLine($"Scaffolded contract options at '{filePath}'.");
    }

    private static Task<int> RunBaselineSequenceAsync()
    {
        var contractName = "Person";
        var implementations = new[] { "Person" };

        DeleteExistingContract(contractName);
        DeleteExistingMigrations(contractName);

        var projectRoot = LocateProjectRoot();

        var initial = new BaselineOptions(
            contractName,
            DefaultContractsNamespace,
            DefaultContractsRoot,
            DefaultStorageRoot,
            DefaultMigrationsRoot,
            implementations,
            VersionIncrement.Patch,
            "1.0.0",
            null,
            BaselineExistingBehavior.Force,
            BaselineIncrementBehavior.Auto);
        BaselineGenerator.Generate(projectRoot, initial);

        var patch = initial with { ExplicitVersion = "1.0.1" };
        BaselineGenerator.Generate(projectRoot, patch);

        var major = initial with { ExplicitVersion = "2.0.0" };
        BaselineGenerator.Generate(projectRoot, major);

        Console.WriteLine("Generated baseline sequence:");
        Console.WriteLine(" - v1.0.0 (initial)");
        Console.WriteLine(" - v1.0.1 (patch)");
        Console.WriteLine(" - v2.0.0 (major)");
        Console.WriteLine();
        Console.WriteLine("Run 'dotnet build' to compile the generated contracts, then add migrations as required.");

        return Task.FromResult(0);
    }

    private static void DeleteExistingContract(string contractName)
    {
        var projectRoot = LocateProjectRoot();
        var contractRoot = Path.Combine(projectRoot, DefaultContractsRoot, contractName);
        if (Directory.Exists(contractRoot))
        {
            Directory.Delete(contractRoot, recursive: true);
            Console.WriteLine($"Removed existing contract directory '{contractRoot}'.");
        }
    }

    private static void DeleteExistingMigrations(string contractName)
    {
        var projectRoot = LocateProjectRoot();
        var storageRoot = Path.Combine(projectRoot, DefaultStorageRoot);
        var migrationsRoot = Path.Combine(storageRoot, "Migrations", contractName);
        if (Directory.Exists(migrationsRoot))
        {
            Directory.Delete(migrationsRoot, recursive: true);
            Console.WriteLine($"Removed existing migrations directory '{migrationsRoot}'.");
        }
    }

    private static BaselineOptions ResolveImplicitPaths(BaselineOptions options, string projectRoot)
    {
        var contractsRoot = options.GetContractsRoot(projectRoot);
        var contractsFull = Path.GetFullPath(contractsRoot);
        var trimmedContracts = Path.TrimEndingDirectorySeparator(contractsFull);
        var parent = Directory.GetParent(trimmedContracts)?.FullName;

        if (string.IsNullOrWhiteSpace(parent))
        {
            return options;
        }

        if (!Path.IsPathRooted(options.StorageFolder)
            && string.Equals(options.StorageFolder, DefaultStorageRoot, StringComparison.OrdinalIgnoreCase))
        {
            options = options with { StorageFolder = Path.Combine(parent, DefaultStorageRoot) };
        }

        if (!Path.IsPathRooted(options.MigrationFolder)
            && string.Equals(options.MigrationFolder, DefaultMigrationsRoot, StringComparison.OrdinalIgnoreCase))
        {
            options = options with { MigrationFolder = Path.Combine(parent, DefaultMigrationsRoot) };
        }

        return options;
    }

    private static bool ContainsContractsDirOption(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--contracts-dir", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int PrintUsage()
    {
        Console.WriteLine("Migration Tool");
        Console.WriteLine("Commands:");
        Console.WriteLine("  baseline <ContractName> --contracts-dir <path>");
        Console.WriteLine("      Required:");
        Console.WriteLine("        <ContractName>          Name used to create or increment the baseline.");
        Console.WriteLine("        --contracts-dir path    Root folder where the contract baselines are generated.");
        Console.WriteLine("      Optional:");
        Console.WriteLine("        --storage-dir path      Alternate root for storage artifacts.");
        Console.WriteLine("        --migration-dir path    Alternate root for migration files.");
        Console.WriteLine("        --increment major|minor|patch  Choose the semantic version component to increment (default patch).");
        Console.WriteLine("        other options           See documentation or source for advanced switches.");
        Console.WriteLine("      Notes:");
        Console.WriteLine("        The contract name must follow 'baseline'.");
        Console.WriteLine("        Storage and migration paths default to the tool project unless overridden.");
        Console.WriteLine("        Overriding --contracts-dir keeps Storage/Migrations alongside that folder unless explicitly set.");
        Console.WriteLine("        Options files are scaffolded beside the contracts root (../Options).");
        Console.WriteLine("        A contract options file is created if missing; existing files are left untouched.");
        Console.WriteLine("        Relative paths are resolved from the tool project directory.");
        Console.WriteLine("  baseline-sequence");
        Console.WriteLine("  migrate scaffold <ContractName>");
        return 1;
    }

    private static string LocateProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MigrationTool.csproj")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Unable to resolve MigrationTool project root.");
        }

        return directory.FullName;
    }

    private static BaselineExistingBehavior PromptForExisting(string targetPath)
    {
        Console.Write($"Baseline folder '{targetPath}' already exists. Overwrite? (y/N): ");
        var response = Console.ReadLine()?.Trim();
        return string.Equals(response, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase)
            ? BaselineExistingBehavior.Force
            : BaselineExistingBehavior.Skip;
    }

    private static bool PromptForIncrement(Version previousVersion, Version targetVersion)
    {
        Console.Write($"Increment baseline from {previousVersion} to {targetVersion}? (y/N): ");
        var response = Console.ReadLine()?.Trim();
        return string.Equals(response, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase);
    }
}

