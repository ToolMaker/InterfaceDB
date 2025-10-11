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
            throw new ArgumentException("Contract name argument is required.");
        }

        var contractName = args[1];
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
        var contractsDirProvided = false;

        for (var i = 2; i < args.Length; i++)
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
                        default:
                            throw new ArgumentException($"Unknown increment option '{incrementToken}'. Expected major, minor, or patch.");
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

        if (!contractsDirProvided)
        {
            throw new ArgumentException("The '--contracts-dir' option is required.");
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
}
