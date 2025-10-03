using System;
using System.Collections.Generic;
using System.IO;
using TransactionManager.Abstraction.Enums;

namespace Idb.Libraries.Baselines;

public sealed record BaselineOptions(
    string ContractName,
    string NamespaceRoot,
    string RootFolder,
    IReadOnlyList<string> Implementations,
    VersionIncrement Increment,
    string? ExplicitVersion,
    string? ProjectRoot)
{
    public static BaselineOptions Parse(string[] args, string defaultNamespaceRoot, string defaultRootFolder)
    {
        if (args.Length < 2)
        {
            throw new ArgumentException("Contract name argument is required.");
        }

        var contractName = args[1];
        var implementations = new List<string>();
        var namespaceRoot = defaultNamespaceRoot;
        var rootFolder = defaultRootFolder;
        var increment = VersionIncrement.Patch;
        string? explicitVersion = null;
        string? projectRoot = null;

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
                case "--root":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --root option.");
                    }

                    rootFolder = args[++i];
                    break;
                case "--increment":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --increment option.");
                    }

                    increment = args[++i].ToLowerInvariant() switch
                    {
                        "major" => VersionIncrement.Major,
                        "minor" => VersionIncrement.Minor,
                        "patch" => VersionIncrement.Patch,
                        _ => throw new ArgumentException("Unknown increment.")
                    };
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
                default:
                    throw new ArgumentException($"Unknown option '{args[i]}'");
            }
        }

        return new BaselineOptions(contractName, namespaceRoot, rootFolder, implementations, increment, explicitVersion, projectRoot);
    }
}
