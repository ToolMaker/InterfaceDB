using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TransactionManager.Abstraction.Enums;
using TransactionManager.Abstraction.Migrations;
using TransactionManager.Implementation.Migrations;

namespace TestScopeService
{
    internal static partial class Program
    {
        private const string DefaultContractsRoot = "Contracts";
        private const string DefaultContractsNamespace = "TestScopeService.Contracts";

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
                    "Usage: dotnet run --project TestScopeService -- baseline <ContractName> [--impl impl1,impl2] [--namespace Namespace.Root] [--root ContractsRoot] [--increment patch|minor|major] [--version x.y.z]");
                return 1;
            }

            var options = BaselineOptions.Parse(args, DefaultContractsNamespace, DefaultContractsRoot);
            var result = BaselineGenerator.Generate(options);

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
                Console.WriteLine("Usage: dotnet run --project TestScopeService -- migrate scaffold <ContractName>");
                return 1;
            }

            var contractName = args[2];
            var projectRoot = LocateProjectRoot();
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
                null);

            var baselines = BaselineGenerator.LoadBaselines(baselineOptions);

            if (baselines.Count < 2)
            {
                Console.WriteLine("At least two baselines are required to scaffold migrations.");
                return 1;
            }

            var migrationsRoot = Path.Combine(projectRoot, "Migrations", contractName);
            Directory.CreateDirectory(migrationsRoot);

            var createdAny = false;

            for (var index = 0; index < baselines.Count - 1; index++)
            {
                var previous = baselines[index];
                var current = baselines[index + 1];

                var previousSegment = BaselineGenerator.NormalizeVersionSegment(previous.Version);
                var currentSegment = BaselineGenerator.NormalizeVersionSegment(current.Version);
                var className = $"I{contractName}_{previousSegment}_To_{currentSegment}_Migration";
                var filePath = Path.Combine(migrationsRoot, className + ".cs");

                if (File.Exists(filePath))
                {
                    Console.WriteLine($"Migration '{className}' already exists. Skipping.");
                    continue;
                }

                var previousNamespace = $"{DefaultContractsNamespace}.{contractName}.{previousSegment}";
                var currentNamespace = $"{DefaultContractsNamespace}.{contractName}.{currentSegment}";

                var builder = new StringBuilder();
                builder.AppendLine("namespace TestScopeService.Migrations;");
                builder.AppendLine();
                builder.AppendLine("using System.Threading;");
                builder.AppendLine("using System.Threading.Tasks;");
                builder.AppendLine($"using {previousNamespace};");
                builder.AppendLine($"using {currentNamespace};");
                builder.AppendLine("using TransactionManager.Abstraction.Migrations;");
                builder.AppendLine("using TransactionManager.Implementation.Migrations;");
                builder.AppendLine();
                builder.AppendLine($"[StorageMigration(typeof({currentNamespace}.I{contractName}), \"{previous.Version}\", \"{current.Version}\")]");
                builder.AppendLine($"public sealed class {className} : StorageMigrationBase<{currentNamespace}.I{contractName}>");
                builder.AppendLine("{");
                builder.AppendLine("    protected override async Task ExecuteCoreAsync(IStorageMigrationContext context, CancellationToken cancellationToken)");
                builder.AppendLine("    {");
                builder.AppendLine($"        await foreach (var record in context.ReadSourceAsync<{previousNamespace}.I{contractName}>(cancellationToken).ConfigureAwait(false))");
                builder.AppendLine("        {");
                builder.AppendLine("            // TODO: inspect record.Value and map to the new contract shape.");
                builder.AppendLine("            throw new NotImplementedException();");
                builder.AppendLine("        }");
                builder.AppendLine("    }");
                builder.AppendLine("}");

                File.WriteAllText(filePath, builder.ToString());
                Console.WriteLine($"Migration scaffold '{className}' created at {filePath}.");
                createdAny = true;
            }

            if (!createdAny)
            {
                Console.WriteLine("All adjacent migrations already exist. Nothing to scaffold.");
            }

            return 0;
        }

        private static Task<int> RunBaselineSequenceAsync()
        {
            var contractName = "Person";
            var implementations = new[] { "Person" };

            DeleteExistingContract(contractName);
            DeleteExistingMigrations(contractName);

            var initial = new BaselineOptions(contractName, DefaultContractsNamespace, DefaultContractsRoot, implementations, VersionIncrement.Patch, "1.0.0");
            BaselineGenerator.Generate(initial);

            var patch = initial with { ExplicitVersion = "1.0.1" };
            BaselineGenerator.Generate(patch);

            var major = initial with { ExplicitVersion = "2.0.0" };
            BaselineGenerator.Generate(major);

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
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TestScopeService.csproj")))
            {
                directory = directory.Parent;
            }

            if (directory is null)
            {
                throw new InvalidOperationException("Unable to resolve TestScopeService project root.");
            }

            return directory.FullName;
        }

        private sealed record BaselineOptions(
            string ContractName,
            string NamespaceRoot,
            string RootFolder,
            IReadOnlyList<string> Implementations,
            VersionIncrement Increment,
            string? ExplicitVersion)
        {
            public static BaselineOptions Parse(string[] args, string defaultNamespaceRoot, string defaultRootFolder)
            {
                var contractName = args[1];
                var implementations = new List<string>();
                var namespaceRoot = defaultNamespaceRoot;
                var rootFolder = defaultRootFolder;
                var increment = VersionIncrement.Patch;
                string? explicitVersion = null;

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
                        default:
                            throw new ArgumentException($"Unknown option '{args[i]}'");
                    }
                }


                return new BaselineOptions(contractName, namespaceRoot, rootFolder, implementations, increment, explicitVersion);
            }
        }

        private sealed record BaselineResult(string NewVersion, string? PreviousVersion, string Namespace);
    }
}




