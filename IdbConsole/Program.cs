using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Migrations;
using Idb.Libraries.Implementation.Migrations;

using Idb.Libraries.Implementation.Baselines;

namespace IdbConsole
{
    internal static partial class Program
    {
        private const string DefaultContractsRoot = "Contracts";
        private const string DefaultContractsNamespace = "IdbConsole.Contracts";

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
        {
            return args[0].ToLowerInvariant() switch
            {
                "baseline" => HandleBaselineCommand(args),
                "baseline-sequence" => await RunBaselineSequenceAsync().ConfigureAwait(false),
                "migrate" => HandleMigrateCommand(args),
                _ => PrintUsage(),
            };
        }

        private static int HandleBaselineCommand(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine(
                    "Usage: dotnet run --project IdbConsole -- baseline <ContractName> [--impl impl1,impl2] [--namespace Namespace.Root] [--root ContractsRoot] [--increment patch|minor|major] [--version x.y.z] [--dbpath path]");
                return 1;
            }

            var options = BaselineOptions.Parse(args, DefaultContractsNamespace, DefaultContractsRoot);
            var projectRoot = options.ProjectRoot ?? LocateProjectRoot();
            var result = BaselineGenerator.Generate(projectRoot, options);

            Console.WriteLine($"Baseline created for {options.ContractName} @ {result.NewVersion} (namespace {result.Namespace}).");
            if (!string.IsNullOrWhiteSpace(result.PreviousVersion))
            {
                Console.WriteLine($"Previous baseline detected at version {result.PreviousVersion}.");
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
                Console.WriteLine("Usage: dotnet run --project IdbConsole -- migrate scaffold <ContractName> [--dbpath path]");
                return 1;
            }

            var contractName = args[2];
            string? projectRootOverride = null;

            for (var i = 3; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--dbpath", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.WriteLine("Missing value for --dbpath option.");
                        return 1;
                    }

                    projectRootOverride = Path.GetFullPath(args[++i]);
                    continue;
                }

                Console.WriteLine($"Unknown option '{args[i]}' for migrate scaffold.");
                return 1;
            }

            var projectRoot = projectRootOverride ?? LocateProjectRoot();
            var contractRoot = Path.Combine(projectRoot, DefaultContractsRoot, contractName);

            if (!Directory.Exists(contractRoot))
            {
                Console.WriteLine($"Contract '{contractName}' has no baselines yet. Run the baseline command first.");
                return 1;
            }

            var baselineOptions = new BaselineOptions(
                contractName,
                DefaultContractsNamespace,
                DefaultContractsRoot,
                Array.Empty<string>(),
                VersionIncrement.Patch,
                null,
                projectRootOverride);

            var baselines = BaselineGenerator.LoadBaselines(projectRoot, baselineOptions);

            if (baselines.Count < 2)
            {
                Console.WriteLine("At least two baselines are required to scaffold migrations.");
                return 1;
            }

            MigrationScaffolder.ScaffoldMigrations(projectRoot, baselineOptions, baselines);
            return 0;
        }
        private static Task<int> RunBaselineSequenceAsync()
        {
            var contractName = "Person";
            var implementations = new[] { "Person" };

            DeleteExistingContract(contractName);
            DeleteExistingMigrations(contractName);

            var projectRoot = LocateProjectRoot();

            var initial = new BaselineOptions(contractName, DefaultContractsNamespace, DefaultContractsRoot, implementations, VersionIncrement.Patch, "1.0.0", null);
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
            var migrationsRoot = Path.Combine(projectRoot, "Migrations", contractName);
            if (Directory.Exists(migrationsRoot))
            {
                Directory.Delete(migrationsRoot, recursive: true);
                Console.WriteLine($"Removed existing migrations directory '{migrationsRoot}'.");
            }
        }

        private static int PrintUsage()
        {
            Console.WriteLine("Storage CLI");
            Console.WriteLine("Commands:");
            Console.WriteLine("  baseline <ContractName> [options]");
            Console.WriteLine("  baseline-sequence");
            Console.WriteLine("  migrate scaffold <ContractName>");
            return 1;
        }

        private static string LocateProjectRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "IdbConsole.csproj")))
            {
                directory = directory.Parent;
            }

            if (directory is null)
            {
                throw new InvalidOperationException("Unable to resolve IdbConsole project root.");
            }

            return directory.FullName;
        }

    }
}




