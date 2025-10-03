using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TransactionManager.Abstraction.Migrations;
using TransactionManager.Implementation.Migrations;

namespace TestScopeService
{
    internal static class Program
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

            var versions = Directory
                .EnumerateDirectories(contractRoot)
                .Select(path => new DirectoryInfo(path))
                .Select(info => (Directory: info, Version: BaselineGenerator.TryParseVersion(info.Name)))
                .Where(tuple => tuple.Version is not null)
                .Select(tuple => (tuple.Directory, Version: tuple.Version!))
                .OrderBy(tuple => tuple.Version)
                .ToList();

            if (versions.Count < 2)
            {
                Console.WriteLine("At least two baselines are required to scaffold migrations.");
                return 1;
            }

            var migrationsRoot = Path.Combine(projectRoot, "Migrations", contractName);
            Directory.CreateDirectory(migrationsRoot);

            var createdAny = false;

            for (var index = 0; index < versions.Count - 1; index++)
            {
                var previous = versions[index];
                var current = versions[index + 1];

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

        private enum VersionIncrement
        {
            Patch,
            Minor,
            Major
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

        private static class BaselineGenerator
        {
            public static BaselineResult Generate(BaselineOptions options)
            {
                var projectRoot = LocateProjectRoot();
                var contractRoot = new DirectoryInfo(Path.Combine(projectRoot, options.RootFolder, options.ContractName));
                contractRoot.Create();

                var existingVersions = contractRoot
                    .EnumerateDirectories()
                    .Select(d => (Directory: d, Version: TryParseVersion(d.Name)))
                    .Where(tuple => tuple.Version is not null)
                    .Select(tuple => (tuple.Directory, Version: tuple.Version!))
                    .OrderBy(tuple => tuple.Version)
                    .ToList();

                var previous = existingVersions.LastOrDefault();
                var targetVersion = ResolveTargetVersion(previous.Version, options);
                var newSegment = NormalizeVersionSegment(targetVersion);
                var newDirectory = new DirectoryInfo(Path.Combine(contractRoot.FullName, newSegment));

                if (newDirectory.Exists)
                {
                    throw new InvalidOperationException($"Baseline directory '{newDirectory.FullName}' already exists.");
                }

                Directory.CreateDirectory(newDirectory.FullName);

                var namespacePrefix = $"{options.NamespaceRoot}.{options.ContractName}";
                var newNamespace = $"{namespacePrefix}.{newSegment}";

                if (previous.Directory is null)
                {
                    GenerateInitialBaseline(newDirectory, options, newNamespace, targetVersion);
                }
                else
                {
                    CopyBaseline(previous.Directory, newDirectory, options, previous.Version!, targetVersion);
                }

                return new BaselineResult(targetVersion.ToString(), previous.Version?.ToString(), newNamespace);
            }

            public static Version? TryParseVersion(string directoryName)
            {
                if (!directoryName.StartsWith('v'))
                {
                    return null;
                }

                var token = directoryName[1..].Replace('_', '.');
                return Version.TryParse(token, out var version) ? version : null;
            }

            public static string NormalizeVersionSegment(Version version)
            {
                var patch = Math.Max(version.Build, 0);
                return $"v{version.Major}_{version.Minor}_{patch}";
            }

            private static void GenerateInitialBaseline(DirectoryInfo targetDirectory, BaselineOptions options, string contractNamespace, Version version)
            {
                var interfacePath = Path.Combine(targetDirectory.FullName, $"I{options.ContractName}.cs");
                var interfaceLines = new[]
                {
                    $"namespace {contractNamespace};",
                    string.Empty,
                    "using TransactionManager.Implementation.Attributes;",
                    string.Empty,
                    $"[StorageScope(VersionTag = \"{version}\")]",
                    $"public interface I{options.ContractName}",
                    "{",
                    "    // TODO: Define contract members for this version.",
                    "}"
                };

                File.WriteAllLines(interfacePath, interfaceLines);

                foreach (var implementation in options.Implementations)
                {
                    var classPath = Path.Combine(targetDirectory.FullName, implementation + ".cs");
                    var classLines = new[]
                    {
                        $"namespace {contractNamespace};",
                        string.Empty,
                        $"public sealed class {implementation} : I{options.ContractName}",
                        "{",
                        "    // TODO: Implement contract members.",
                        "}"
                    };

                    File.WriteAllLines(classPath, classLines);
                }
            }

            private static void CopyBaseline(DirectoryInfo sourceDirectory,
                                             DirectoryInfo targetDirectory,
                                             BaselineOptions options,
                                             Version previousVersion,
                                             Version newVersion)
            {
                var previousNamespace = $"{options.NamespaceRoot}.{options.ContractName}.{NormalizeVersionSegment(previousVersion)}";
                var newNamespace = $"{options.NamespaceRoot}.{options.ContractName}.{NormalizeVersionSegment(newVersion)}";

                foreach (var sourceFile in sourceDirectory.GetFiles("*.cs", SearchOption.TopDirectoryOnly))
                {
                    var content = File.ReadAllText(sourceFile.FullName);
                    content = content.Replace(previousNamespace, newNamespace, StringComparison.Ordinal);
                    content = content.Replace($"VersionTag = \"{previousVersion}\"", $"VersionTag = \"{newVersion}\"", StringComparison.Ordinal);

                    var destinationPath = Path.Combine(targetDirectory.FullName, sourceFile.Name);
                    File.WriteAllText(destinationPath, content);
                }
            }

            private static Version ResolveTargetVersion(Version? previousVersion, BaselineOptions options)
            {
                if (!string.IsNullOrWhiteSpace(options.ExplicitVersion))
                {
                    if (!Version.TryParse(options.ExplicitVersion, out var explicitVersion))
                    {
                        throw new ArgumentException($"Invalid version '{options.ExplicitVersion}'.");
                    }

                    return explicitVersion;
                }

                if (previousVersion is null)
                {
                    return new Version(1, 0, 0);
                }

                return options.Increment switch
                {
                    VersionIncrement.Major => new Version(previousVersion.Major + 1, 0, 0),
                    VersionIncrement.Minor => new Version(previousVersion.Major, previousVersion.Minor + 1, 0),
                    _ => new Version(previousVersion.Major, previousVersion.Minor, previousVersion.Build + 1)
                };
            }
        }
    }
}
