using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Idb.Libraries.Abstraction.Services.Migrations;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Implementation.Services.Baselines;
using Idb.Libraries.Implementation.Services.Migrations;

namespace MigrationTool;

internal static class Program
{
    private const string DefaultContractsRoot = "Contracts";
    private const string DefaultStorageRoot = "Storage";
    private const string DefaultMigrationsRoot = "Migrations";
    private const string DefaultContractsNamespace = "MigrationTool.Contracts";
    private static readonly HashSet<string> WindowsReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    };

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
        if (!ContainsContractsDirOption(args) && !ContainsConfigFileOption(args))
        {
            Console.WriteLine("Specify either '--contracts-dir' or '--config-file-dir' when running the baseline command.");
            return PrintUsage();
        }

        BaselineOptions options;
        try
        {
            options = BaselineOptions.Parse(
                args,
                DefaultContractsNamespace,
                DefaultContractsRoot,
                DefaultStorageRoot,
                DefaultMigrationsRoot,
                BaselineExistingBehavior.Prompt,
                BaselineIncrementBehavior.Auto);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine(ex.Message);
            return PrintUsage();
        }

        options = NormalizeUserSuppliedPaths(options, args);

        if (!TryApplyConfigOverrides(options, out options, out var configError))
        {
            Console.WriteLine(configError);
            return 1;
        }

        if (string.IsNullOrWhiteSpace(options.ContractName))
        {
            Console.WriteLine("Unable to determine the contract name. Supply it explicitly or ensure the options file exposes a Key value.");
            return 1;
        }

        var projectRoot = options.ProjectRoot ?? LocateProjectRoot();
        options = ResolveImplicitPaths(options, projectRoot);

        var contractsRoot = options.GetContractsRoot(projectRoot);
        if (!ValidateDirectoryRoot("Contracts root", contractsRoot, out var contractRootError))
        {
            Console.WriteLine(contractRootError);
            return 1;
        }
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
        if (!ValidateDirectoryRoot("Storage root", storageRoot, out var storageRootError))
        {
            Console.WriteLine(storageRootError);
            return 1;
        }
        var storageRootFull = Path.GetFullPath(storageRoot);
        var storageExisted = Directory.Exists(storageRoot);
        var migrationRoot = options.GetMigrationRoot(projectRoot);
        if (!ValidateDirectoryRoot("Migration root", migrationRoot, out var migrationRootError))
        {
            Console.WriteLine(migrationRootError);
            return 1;
        }
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
            Console.WriteLine("Usage: dotnet run --project MigrationTool -- migrate scaffold <ContractName> (--contracts-dir <path> | --config-file-dir <path>) [--dbpath path] [--storage-dir path] [--migration-dir path]");
            return 1;
        }

        var contractName = args[2];

        var normalizedArgs = new List<string> { "baseline", contractName };
        if (args.Length > 3)
        {
            normalizedArgs.AddRange(args[3..]);
        }

        if (!ContainsContractsDirOption(normalizedArgs) && !ContainsConfigFileOption(normalizedArgs))
        {
            Console.WriteLine("Specify either '--contracts-dir' or '--config-file-dir' when scaffolding migrations.");
            return 1;
        }

        BaselineOptions baselineOptions;
        try
        {
            baselineOptions = BaselineOptions.Parse(
                normalizedArgs.ToArray(),
                DefaultContractsNamespace,
                DefaultContractsRoot,
                DefaultStorageRoot,
                DefaultMigrationsRoot,
                BaselineExistingBehavior.Prompt,
                BaselineIncrementBehavior.Auto);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }

        baselineOptions = NormalizeUserSuppliedPaths(baselineOptions, normalizedArgs);

        if (!TryApplyConfigOverrides(baselineOptions, out baselineOptions, out var migrateConfigError))
        {
            Console.WriteLine(migrateConfigError);
            return 1;
        }

        var projectRoot = baselineOptions.ProjectRoot ?? LocateProjectRoot();
        baselineOptions = ResolveImplicitPaths(baselineOptions, projectRoot);
        var migrateContractsRoot = baselineOptions.GetContractsRoot(projectRoot);
        if (!ValidateDirectoryRoot("Contracts root", migrateContractsRoot, out var migrateContractsError))
        {
            Console.WriteLine(migrateContractsError);
            return 1;
        }
        var contractRoot = Path.Combine(migrateContractsRoot, contractName);

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

    private static bool TryApplyConfigOverrides(BaselineOptions options, out BaselineOptions updatedOptions, out string? error)
    {
        if (string.IsNullOrWhiteSpace(options.ConfigFilePath))
        {
            updatedOptions = options;
            error = null;
            return true;
        }

        var suppliedPath = NormalizeRelativeToExe(options.ConfigFilePath);
        string? optionsFilePath = null;

        if (File.Exists(suppliedPath))
        {
            optionsFilePath = suppliedPath;
        }
        else if (Directory.Exists(suppliedPath))
        {
            var optionFiles = Directory
                .EnumerateFiles(suppliedPath, "*Options.cs", SearchOption.AllDirectories)
                .ToList();
            if (optionFiles.Count == 0)
            {
                updatedOptions = options;
                error = $"No '*Options.cs' files were found under '{suppliedPath}'.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(options.ContractName))
            {
                var expectedFile = options.ContractName + "Options.cs";
                optionsFilePath = optionFiles.FirstOrDefault(file =>
                    string.Equals(Path.GetFileName(file), expectedFile, StringComparison.OrdinalIgnoreCase));

                if (optionsFilePath is null)
                {
                    foreach (var candidate in optionFiles)
                    {
                        if (TryParseContractOptions(candidate, out var candidateConfig, out _))
                        {
                            var candidateName = ResolveContractName(candidateConfig, candidate);
                            if (string.Equals(candidateName, options.ContractName, StringComparison.OrdinalIgnoreCase))
                            {
                                optionsFilePath = candidate;
                                break;
                            }
                        }
                    }
                }
            }
            else if (optionFiles.Count == 1)
            {
                optionsFilePath = optionFiles[0];
            }

            if (optionsFilePath is null)
            {
                updatedOptions = options;
                if (!string.IsNullOrWhiteSpace(options.ContractName))
                {
                    error = $"Unable to locate an options file for contract '{options.ContractName}' under '{suppliedPath}'.";
                }
                else
                {
                    error = $"Multiple options files were found under '{suppliedPath}'. Specify the contract name or point to a directory containing only one contract options file.";
                }

                return false;
            }
        }
        else
        {
            updatedOptions = options;
            error = $"The config path '{suppliedPath}' does not exist.";
            return false;
        }

        if (!TryParseContractOptions(optionsFilePath, out var config, out error))
        {
            updatedOptions = options;
            return false;
        }

        var resolvedContractName = string.IsNullOrWhiteSpace(options.ContractName)
            ? ResolveContractName(config, optionsFilePath)
            : options.ContractName;

        if (string.IsNullOrWhiteSpace(resolvedContractName))
        {
            updatedOptions = options;
            error = $"Unable to determine contract name from '{optionsFilePath}'. Add a Key property or specify the contract name explicitly.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(options.ContractName)
            && !string.Equals(options.ContractName, resolvedContractName, StringComparison.OrdinalIgnoreCase))
        {
            updatedOptions = options;
            error = $"Contract name '{options.ContractName}' does not match the options file '{optionsFilePath}' (resolved contract '{resolvedContractName}').";
            return false;
        }

        var contractPathFull = ResolvePathRelativeToOptionsFile(optionsFilePath, config.ContractPath);
        var implementationsPathFull = ResolvePathRelativeToOptionsFile(optionsFilePath, config.ImplementationsPath);
        var migrationsPathFull = ResolvePathRelativeToOptionsFile(optionsFilePath, config.MigrationsPath);
        var versionSegment = DetectVersionSegment(contractPathFull, config.ActiveVersion);

        if (!TryDeriveRootFromConfigPath(contractPathFull, resolvedContractName, versionSegment, includesVersionSegment: true, out var contractsRoot, out error)
            || !TryDeriveRootFromConfigPath(implementationsPathFull, resolvedContractName, versionSegment, includesVersionSegment: true, out var storageRoot, out error)
            || !TryDeriveRootFromConfigPath(migrationsPathFull, resolvedContractName, versionSegment, includesVersionSegment: false, out var migrationsRoot, out error))
        {
            updatedOptions = options;
            return false;
        }

        updatedOptions = options with
        {
            ContractName = resolvedContractName,
            ContractsFolder = contractsRoot,
            StorageFolder = storageRoot,
            MigrationFolder = migrationsRoot,
            ConfigFilePath = optionsFilePath
        };

        Console.WriteLine($"Baseline paths resolved from '{optionsFilePath}'.");
        error = null;
        return true;
    }

    private sealed record ContractOptionsConfig(
        string? Key,
        string? ContractName,
        string ContractPath,
        string ImplementationsPath,
        string MigrationsPath,
        string? ActiveVersion,
        string? DataRoot);

    private static bool TryParseContractOptions(string optionsFilePath, out ContractOptionsConfig config, out string? error)
    {
        var lines = File.ReadAllLines(optionsFilePath);
        var key = ExtractStringLiteral(lines, "Key");
        var contractName = ExtractStringLiteral(lines, "ContractName");
        var contractPath = ExtractStringLiteral(lines, "ContractPath");
        var implementationsPath = ExtractStringLiteral(lines, "ImplementationsPath");
        var migrationsPath = ExtractStringLiteral(lines, "MigrationsPath");
        var dataRoot = ExtractStringLiteral(lines, "DataRoot");
        var activeVersion = ExtractStringLiteral(lines, "ActiveVersion");

        if (string.IsNullOrWhiteSpace(contractPath) || string.IsNullOrWhiteSpace(implementationsPath) || string.IsNullOrWhiteSpace(migrationsPath))
        {
            error = $"The options file '{optionsFilePath}' must contain ContractPath, ImplementationsPath, and MigrationsPath values.";
            config = default!;
            return false;
        }

        config = new ContractOptionsConfig(key, contractName, contractPath, implementationsPath, migrationsPath, activeVersion, dataRoot);
        error = null;
        return true;
    }

    private static string? ExtractStringLiteral(IEnumerable<string> lines, string propertyName)
    {
        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (!trimmed.StartsWith("public", StringComparison.Ordinal)
                || !trimmed.Contains(propertyName, StringComparison.Ordinal))
            {
                continue;
            }

            var equalsIndex = trimmed.IndexOf('=');
            if (equalsIndex < 0)
            {
                continue;
            }

            var valuePortion = trimmed[(equalsIndex + 1)..].Trim();
            if (valuePortion.EndsWith(";"))
            {
                valuePortion = valuePortion[..^1].TrimEnd();
            }

            if (string.Equals(valuePortion, "null", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return ParseStringLiteral(valuePortion);
        }

        return null;
    }

    private static string ParseStringLiteral(string literal)
    {
        literal = literal.Trim();

        if (literal.StartsWith("@\"", StringComparison.Ordinal) && literal.EndsWith("\"", StringComparison.Ordinal))
        {
            var inner = literal.Substring(2, literal.Length - 3);
            return inner.Replace("\"\"", "\"");
        }

        if (literal.StartsWith("\"", StringComparison.Ordinal) && literal.EndsWith("\"", StringComparison.Ordinal))
        {
            var inner = literal.Substring(1, literal.Length - 2);
            return System.Text.RegularExpressions.Regex.Unescape(inner);
        }

        return literal;
    }

    private static string ResolvePathRelativeToOptionsFile(string optionsFilePath, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, ".", StringComparison.Ordinal))
        {
            return NormalizeRelativeToExe(".");
        }

        if (Path.IsPathRooted(value))
        {
            return Path.GetFullPath(value);
        }

        return NormalizeRelativeToExe(value);
    }

    private static string? DetectVersionSegment(string contractPathFull, string? activeVersion)
    {
        if (!string.IsNullOrWhiteSpace(activeVersion) && Version.TryParse(activeVersion, out var parsedVersion))
        {
            return BaselineGenerator.NormalizeVersionSegment(parsedVersion);
        }

        var directory = new DirectoryInfo(contractPathFull);
        return LooksLikeVersionSegment(directory.Name) ? directory.Name : null;
    }

    private static string? NormalizeContractIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var token = value.Trim();
        if (token.StartsWith("nameof(", StringComparison.Ordinal))
        {
            var inner = token.Substring("nameof(".Length).TrimEnd(')', ' ').Trim();
            var segments = inner.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            token = segments.Length > 0 ? segments[^1] : inner;
        }

        return string.Equals(token, "null", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(token)
            ? null
            : token;
    }

    private static string? ResolveContractName(ContractOptionsConfig config, string optionsFilePath)
    {
        var resolvedFromContract = NormalizeContractIdentifier(config.ContractName);
        if (!string.IsNullOrWhiteSpace(resolvedFromContract))
        {
            return resolvedFromContract;
        }

        var resolvedFromKey = NormalizeContractIdentifier(config.Key);
        if (!string.IsNullOrWhiteSpace(resolvedFromKey))
        {
            return resolvedFromKey;
        }

        var fileName = Path.GetFileNameWithoutExtension(optionsFilePath);
        if (fileName.EndsWith("Options", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^"Options".Length];
        }

        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }

    private static bool LooksLikeVersionSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || char.ToLowerInvariant(segment[0]) != 'v')
        {
            return false;
        }

        var parts = segment[1..].Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Length > 3)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (!int.TryParse(part, out _))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryDeriveRootFromConfigPath(
        string path,
        string contractName,
        string? versionSegment,
        bool includesVersionSegment,
        out string root,
        out string? error)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));

        if (includesVersionSegment)
        {
            if (!string.IsNullOrWhiteSpace(versionSegment) && string.Equals(directory.Name, versionSegment, StringComparison.OrdinalIgnoreCase))
            {
                directory = directory.Parent ?? directory;
            }
            else if (LooksLikeVersionSegment(directory.Name))
            {
                directory = directory.Parent ?? directory;
            }
        }

        var contractDirectory = directory;
        while (contractDirectory is not null && !string.Equals(contractDirectory.Name, contractName, StringComparison.OrdinalIgnoreCase))
        {
            contractDirectory = contractDirectory.Parent;
        }

        if (contractDirectory?.Parent is null)
        {
            root = string.Empty;
            error = $"Unable to derive root directory from '{path}'. Ensure the configuration paths for '{contractName}' include the contract folder.";
            return false;
        }

        root = contractDirectory.Parent.FullName;
        error = null;
        return true;
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
        var exeBaseFull = Path.GetFullPath(AppContext.BaseDirectory ?? Directory.GetCurrentDirectory());

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

        var contractRoot = Path.Combine(contractsRootResolved, options.ContractName);
        var implementationsRoot = Path.Combine(storageRootFull, options.ContractName);
        var migrationsRoot = Path.Combine(migrationsRootFull, options.ContractName);

        var contractVersionPath = Path.Combine(contractRoot, versionSegment);
        var implementationsVersionPath = Path.Combine(implementationsRoot, versionSegment);

        Directory.CreateDirectory(contractVersionPath);
        Directory.CreateDirectory(implementationsVersionPath);
        Directory.CreateDirectory(migrationsRoot);

        var namespaceOwner = new DirectoryInfo(optionsBaseFull).Name;
        var namespaceName = namespaceOwner + ".Options";
        var sanitizedContractName = options.ContractName.Replace("\"", "\\\"");
        var sanitizedKey = sanitizedContractName;

        var contractInterface = $"{options.NamespaceRoot}.{options.ContractName}.{versionSegment}.I{options.ContractName}";
        var lines = new[]
        {
            "using System;",
            "using System.IO;",
            "using Idb.Libraries.Abstractions.Dtos;",
            "using Idb.Libraries.Abstractions.Enums;",
            "using Idb.Libraries.Abstractions.Services.Indexes;",
            "using Idb.Libraries.Abstractions.Options;",
            string.Empty,
            $"namespace {namespaceName};",
            string.Empty,
            $"// TODO: Update the generic argument if your runtime contract type differs from {contractInterface}.",
            $"public sealed class {className} : IDbContractConfigDto<{contractInterface}, FileInfo>",
            "{",
            $"    public string Key {{ get; set; }} = \"{sanitizedKey}\";",
            $"    public string ContractName {{ get; set; }} = \"{sanitizedContractName}\";",
            $"    public string? ImplementationsPath {{ get; set; }} = {ToVerbatimLiteral(exeBaseFull, implementationsRoot)};",
            $"    public string? ContractPath {{ get; set; }} = {ToVerbatimLiteral(exeBaseFull, contractRoot)};",
            $"    public string? MigrationsPath {{ get; set; }} = {ToVerbatimLiteral(exeBaseFull, migrationsRoot)};",
            "    public string? DataRoot { get; set; } = \"Data\";",
            $"    public string? ActiveVersion {{ get; set; }} = \"{result.NewVersion}\";",
            $"    public IIndex<{contractInterface}>[]? Indexes {{ get; set; }} = Array.Empty<IIndex<{contractInterface}>>();",
            "    public IndexProvisioningModeType IndexProvisioningMode { get; set; } = IndexProvisioningModeType.Dynamic;",
            "    public ContractRepositoryType RepositoryType { get; set; } = ContractRepositoryType.FileSystem;",
            "    public StoragePolicy? StoragePolicy { get; set; } = StoragePolicy.Default;",
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
            VersionIncrementType.Patch,
            "1.0.0",
            null,
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

    private static bool ValidateDirectoryRoot(string description, string rootPath, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            error = $"{description} path cannot be empty.";
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var trimmed = Path.TrimEndingDirectorySeparator(rootPath);
        if (string.IsNullOrEmpty(trimmed))
        {
            trimmed = rootPath;
        }

        var segments = trimmed.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment.Contains(':'))
            {
                continue;
            }

            if (IsReservedWindowsDeviceName(segment))
            {
                error = $"{description} '{rootPath}' cannot be created because '{segment}' is a reserved device name on Windows. Choose a different directory name.";
                return false;
            }
        }

        return true;
    }

    private static bool IsReservedWindowsDeviceName(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return false;
        }

        var trimmed = segment.Trim().TrimEnd(' ', '.');
        if (trimmed.Length == 0)
        {
            return false;
        }

        var dotIndex = trimmed.IndexOf('.');
        if (dotIndex >= 0)
        {
            trimmed = trimmed[..dotIndex];
        }

        return WindowsReservedDeviceNames.Contains(trimmed);
    }

    private static BaselineOptions NormalizeUserSuppliedPaths(BaselineOptions options, IEnumerable<string> args)
    {
        var updated = options;

        if (ContainsContractsDirOption(args) && !Path.IsPathRooted(updated.ContractsFolder))
        {
            updated = updated with { ContractsFolder = NormalizeRelativeToExe(updated.ContractsFolder) };
        }

        if (ContainsStorageDirOption(args) && !Path.IsPathRooted(updated.StorageFolder))
        {
            updated = updated with { StorageFolder = NormalizeRelativeToExe(updated.StorageFolder) };
        }

        if (ContainsMigrationDirOption(args) && !Path.IsPathRooted(updated.MigrationFolder))
        {
            updated = updated with { MigrationFolder = NormalizeRelativeToExe(updated.MigrationFolder) };
        }

        return updated;
    }

    private static string NormalizeRelativeToExe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Path.GetFullPath(AppContext.BaseDirectory ?? Directory.GetCurrentDirectory());
        }

        var baseDirectory = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = Directory.GetCurrentDirectory();
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, path));
    }

    private static bool ContainsContractsDirOption(IEnumerable<string> args)
        => ContainsOption(args, "--contracts-dir");

    private static bool ContainsConfigFileOption(IEnumerable<string> args)
        => ContainsOption(args, "--config-file-dir");

    private static bool ContainsStorageDirOption(IEnumerable<string> args)
        => ContainsOption(args, "--storage-dir", "--storage");

    private static bool ContainsMigrationDirOption(IEnumerable<string> args)
        => ContainsOption(args, "--migration-dir", "--migration");

    private static bool ContainsOption(IEnumerable<string> args, params string[] optionNames)
    {
        foreach (var arg in args)
        {
            foreach (var option in optionNames)
            {
                if (string.Equals(arg, option, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int PrintUsage()
    {
        Console.WriteLine("Migration Tool");
        Console.WriteLine("Commands:");
        Console.WriteLine("  baseline <ContractName> --contracts-dir <path>");
        Console.WriteLine("  baseline [<ContractName>] --config-file-dir <path>");
        Console.WriteLine("  baseline-sequence");
        Console.WriteLine("  migrate scaffold <ContractName>");
        Console.WriteLine("  Required option sets:");
        Console.WriteLine("    <ContractName> with --contracts-dir <path>    Root folder where the contract baselines are generated.");
        Console.WriteLine("    [<ContractName>] with --config-file-dir <path>  Contract options C# file (or directory) used to infer baseline locations.");
        Console.WriteLine("  Parameter descriptions:");
        Console.WriteLine("    <ContractName>          Name used to create or increment the baseline.");
        Console.WriteLine("    --contracts-dir path    Root folder where the contract baselines are generated.");
        Console.WriteLine("    --config-file-dir path  Path to a contract options C# file or directory that contains options used to infer baseline locations (contract name derived when omitted).");
        Console.WriteLine("    --storage-dir path      Alternate root for storage artifacts.");
        Console.WriteLine("    --migration-dir path    Alternate root for migration files.");
        Console.WriteLine("    --existing force|skip|prompt  Choose how the tool reacts when the target baseline already exists.");
        Console.WriteLine("    --increment-behavior auto|prompt|skip  Control whether new versions are created automatically or interactively.");
        Console.WriteLine("    --increment major|minor|patch  Choose the semantic version component to increment (default patch).");
        Console.WriteLine("    other options           See documentation or source for advanced switches.");
        Console.WriteLine("  Notes:");
        Console.WriteLine("    The contract name must follow 'baseline' when specified.");
        Console.WriteLine("    Storage and migration paths default to the tool project unless overridden.");
        Console.WriteLine("    Options files are scaffolded beside the contracts root (../Options).");
        Console.WriteLine("    A contract options file is created if missing; existing files are left untouched.");
        Console.WriteLine("    Relative paths are resolved from the tool project directory.");
        Console.WriteLine("    When using --config-file-dir, the tool reads the specified options class to derive baseline locations.");
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



