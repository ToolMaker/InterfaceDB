using System;
using System.Collections.Generic;
using System.IO;
using Idb.Libraries.Abstractions.Enums;

namespace Idb.Libraries.Implementation.Services.Baselines;

public sealed record BaselineOptions(
    string ContractName,
    string NamespaceRoot,
    string ContractsFolder,
    string StorageFolder,
    string MigrationFolder,
    IReadOnlyList<string> Implementations,
    VersionIncrementType Increment,
    string? ExplicitVersion,
    string? ProjectRoot,
    string? ConfigFilePath,
    BaselineExistingBehavior ExistingBehavior,
    BaselineIncrementBehavior IncrementBehavior)
{
    public static BaselineOptions Parse(
        string[] args,
        string defaultNamespaceRoot,
        string defaultContractsFolder,
        string defaultStorageFolder,
        string defaultMigrationFolder,
        BaselineExistingBehavior defaultExistingBehavior = BaselineExistingBehavior.Prompt,
        BaselineIncrementBehavior defaultIncrementBehavior = BaselineIncrementBehavior.Auto)
    {
        if (args.Length < 2)
        {
            throw new ArgumentException("At least one argument is required for the baseline command.");
        }

        var contractName = string.Empty;
        var index = 1;
        if (args.Length > 1 && !IsOption(args[1]))
        {
            contractName = args[1];
            index = 2;
        }

        var implementations = new List<string>();
        var namespaceRoot = defaultNamespaceRoot;
        var contractsFolder = defaultContractsFolder;
        var storageFolder = defaultStorageFolder;
        var migrationFolder = defaultMigrationFolder;
        var increment = VersionIncrementType.Patch;
        var existingBehavior = defaultExistingBehavior;
        var incrementBehavior = defaultIncrementBehavior;
        string? explicitVersion = null;
        string? projectRoot = null;
        string? configFilePath = null;
        var contractsDirProvided = false;
        var configFileProvided = false;

        for (var i = index; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--impl":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --impl option.");
                    }

                    implementations.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;

                case "--namespace":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --namespace option.");
                    }

                    namespaceRoot = args[++i];
                    break;

                case "--contracts-dir":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException($"Missing value for {args[i]} option.");
                    }

                    contractsFolder = args[++i];
                    contractsDirProvided = true;
                    break;

                case "--config-file-dir":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException($"Missing value for {args[i]} option.");
                    }

                    configFilePath = args[++i];
                    configFileProvided = true;
                    break;

                case "--storage":
                case "--storage-dir":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException($"Missing value for {args[i]} option.");
                    }

                    storageFolder = args[++i];
                    break;

                case "--increment":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --increment option.");
                    }

                    var incrementToken = args[++i].ToLowerInvariant();
                    switch (incrementToken)
                    {
                        case "major":
                            increment = VersionIncrementType.Major;
                            break;
                        case "minor":
                            increment = VersionIncrementType.Minor;
                            break;
                        case "patch":
                            increment = VersionIncrementType.Patch;
                            break;
                        case "auto":
                            incrementBehavior = BaselineIncrementBehavior.Auto;
                            break;
                        case "prompt":
                            incrementBehavior = BaselineIncrementBehavior.Prompt;
                            break;
                        case "skip":
                            incrementBehavior = BaselineIncrementBehavior.Skip;
                            break;
                        default:
                            throw new ArgumentException($"Unknown increment option '{incrementToken}'. Expected major, minor, patch, auto, prompt, or skip.");
                    }
                    break;

                case "--increment-behavior":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --increment-behavior option.");
                    }

                    var incrementBehaviorToken = args[++i].ToLowerInvariant();
                    switch (incrementBehaviorToken)
                    {
                        case "auto":
                            incrementBehavior = BaselineIncrementBehavior.Auto;
                            break;
                        case "prompt":
                            incrementBehavior = BaselineIncrementBehavior.Prompt;
                            break;
                        case "skip":
                            incrementBehavior = BaselineIncrementBehavior.Skip;
                            break;
                        default:
                            throw new ArgumentException($"Unknown increment behavior option '{incrementBehaviorToken}'. Expected auto, prompt, or skip.");
                    }
                    break;

                case "--existing":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --existing option.");
                    }

                    var existingToken = args[++i].ToLowerInvariant();
                    switch (existingToken)
                    {
                        case "force":
                            existingBehavior = BaselineExistingBehavior.Force;
                            break;
                        case "skip":
                            existingBehavior = BaselineExistingBehavior.Skip;
                            break;
                        case "prompt":
                            existingBehavior = BaselineExistingBehavior.Prompt;
                            break;
                        default:
                            throw new ArgumentException($"Unknown existing option '{existingToken}'. Expected force, skip, or prompt.");
                    }
                    break;

                case "--version":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --version option.");
                    }

                    explicitVersion = args[++i];
                    break;

                case "--dbpath":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --dbpath option.");
                    }

                    projectRoot = Path.GetFullPath(args[++i]);
                    break;

                case "--migration-dir":
                case "--migration":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException($"Missing value for {args[i]} option.");
                    }

                    migrationFolder = args[++i];
                    break;

                default:
                    throw new ArgumentException($"Unknown option '{args[i]}'");
            }
        }

        if (contractsDirProvided && configFileProvided)
        {
            throw new ArgumentException("Specify either '--contracts-dir' or '--config-file-dir', not both.");
        }

        if (!contractsDirProvided && !configFileProvided)
        {
            throw new ArgumentException("The baseline command requires either '--contracts-dir' or '--config-file-dir'.");
        }

        return new BaselineOptions(
            contractName,
            namespaceRoot,
            contractsFolder,
            storageFolder,
            migrationFolder,
            implementations,
            increment,
            explicitVersion,
            projectRoot,
            configFilePath,
            existingBehavior,
            incrementBehavior);
    }

    public string GetContractsRoot(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("Project root cannot be null or whitespace.", nameof(projectRoot));
        }

        return Path.IsPathRooted(ContractsFolder)
            ? ContractsFolder
            : Path.Combine(projectRoot, ContractsFolder);
    }

    public string GetStorageRoot(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("Project root cannot be null or whitespace.", nameof(projectRoot));
        }

        if (string.IsNullOrWhiteSpace(StorageFolder))
        {
            return projectRoot;
        }

        return Path.IsPathRooted(StorageFolder)
            ? StorageFolder
            : Path.Combine(projectRoot, StorageFolder);
    }

    public string GetMigrationRoot(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("Project root cannot be null or whitespace.", nameof(projectRoot));
        }

        if (string.IsNullOrWhiteSpace(MigrationFolder))
        {
            return projectRoot;
        }

        return Path.IsPathRooted(MigrationFolder)
            ? MigrationFolder
            : Path.Combine(projectRoot, MigrationFolder);
    }

    private static bool IsOption(string token)
        => token.StartsWith("--", StringComparison.Ordinal);
}
