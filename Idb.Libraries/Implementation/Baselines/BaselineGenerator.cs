using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Idb.Libraries.Abstractions.Enums;

namespace Idb.Libraries.Implementation.Baselines;

public static class BaselineGenerator
{
    private static readonly Regex VersionTagRegex = new("VersionTag\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public sealed record BaselineDescriptor(DirectoryInfo Directory, Version Version);

    public static BaselineResult Generate(string projectRoot, BaselineOptions options)
    {
        if (projectRoot is null)
        {
            throw new ArgumentNullException(nameof(projectRoot));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var contractRoot = new DirectoryInfo(Path.Combine(projectRoot, options.RootFolder, options.ContractName));
        contractRoot.Create();

        var existingBaselines = LoadBaselines(contractRoot, options);
        var latestExistingVersion = existingBaselines.LastOrDefault()?.Version;
        var targetVersion = ResolveTargetVersion(latestExistingVersion, options);
        var segment = NormalizeVersionSegment(targetVersion);
        var targetDirectory = new DirectoryInfo(Path.Combine(contractRoot.FullName, segment));
        var targetNamespace = $"{options.NamespaceRoot}.{options.ContractName}.{segment}";

        var previousBaseline = existingBaselines.Where(b => b.Version < targetVersion).LastOrDefault();

        if (existingBaselines.Any(b => b.Version == targetVersion))
        {
            Console.WriteLine($"Baseline for {options.ContractName} version {targetVersion} already exists. Reusing existing files.");
            return new BaselineResult(targetVersion.ToString(), previousBaseline?.Version.ToString(), targetNamespace);
        }

        Directory.CreateDirectory(targetDirectory.FullName);

        if (previousBaseline is null)
        {
            GenerateInitialBaseline(targetDirectory, options, targetNamespace, targetVersion);
        }
        else
        {
            CopyBaseline(previousBaseline.Directory, targetDirectory, options, previousBaseline.Version, targetVersion);
        }

        return new BaselineResult(targetVersion.ToString(), previousBaseline?.Version.ToString(), targetNamespace);
    }

    public static IReadOnlyList<BaselineDescriptor> LoadBaselines(string projectRoot, BaselineOptions options)
    {
        var contractRoot = new DirectoryInfo(Path.Combine(projectRoot, options.RootFolder, options.ContractName));
        return contractRoot.Exists ? LoadBaselines(contractRoot, options) : Array.Empty<BaselineDescriptor>();
    }

    public static string NormalizeVersionSegment(Version version)
    {
        var patch = Math.Max(version.Build, 0);
        return $"v{version.Major}_{version.Minor}_{patch}";
    }

    private static List<BaselineDescriptor> LoadBaselines(DirectoryInfo contractRoot, BaselineOptions options)
    {
        var list = new List<BaselineDescriptor>();

        foreach (var directory in contractRoot.EnumerateDirectories())
        {
            if (TryReadVersionFromBaseline(directory, options.ContractName, out var version))
            {
                list.Add(new BaselineDescriptor(directory, version));
            }
            else
            {
                Console.WriteLine($"Skipping baseline folder '{directory.Name}' because no [StorageScope] VersionTag was found in I{options.ContractName}.cs.");
            }
        }

        list.Sort((a, b) => a.Version.CompareTo(b.Version));
        return list;
    }

    private static bool TryReadVersionFromBaseline(DirectoryInfo baselineDirectory, string contractName, out Version version)
    {
        var interfacePath = Path.Combine(baselineDirectory.FullName, $"I{contractName}.cs");
        if (!File.Exists(interfacePath))
        {
            version = null!;
            return false;
        }

        var contents = File.ReadAllText(interfacePath);
        var match = VersionTagRegex.Match(contents);
        if (!match.Success)
        {
            version = null!;
            return false;
        }

        if (!Version.TryParse(match.Groups[1].Value, out var parsed))
        {
            version = null!;
            return false;
        }

        version = parsed;
        return true;
    }

    private static void GenerateInitialBaseline(DirectoryInfo targetDirectory, BaselineOptions options, string contractNamespace, Version version)
    {
        var interfacePath = Path.Combine(targetDirectory.FullName, $"I{options.ContractName}.cs");
        var interfaceLines = new[]
        {
            $"namespace {contractNamespace};",
            string.Empty,
            "using Idb.Libraries.Abstractions.Attributes;",
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
            if (string.IsNullOrWhiteSpace(implementation))
            {
                continue;
            }

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
