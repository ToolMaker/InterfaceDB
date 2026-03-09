using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Idb.Libraries.Implementation.Services.Baselines;

namespace Idb.Libraries.Implementation.Services.Migrations;

public static class MigrationScaffolder
{
    public static bool ScaffoldMigrations(string projectRoot, BaselineOptions options, IReadOnlyList<BaselineGenerator.BaselineDescriptor> baselines, TextWriter? output = null)
    {
        if (baselines is null)
        {
            throw new ArgumentNullException(nameof(baselines));
        }

        if (baselines.Count < 2)
        {
            WriteLine(output, "At least two baselines are required to scaffold migrations.");
            return false;
        }

        var migrationsRoot = Path.Combine(options.GetMigrationRoot(projectRoot), options.ContractName);
        Directory.CreateDirectory(migrationsRoot);

        var createdAny = false;

        for (var index = 0; index < baselines.Count - 1; index++)
        {
            var previous = baselines[index];
            var current = baselines[index + 1];

            var previousSegment = BaselineGenerator.NormalizeVersionSegment(previous.Version);
            var currentSegment = BaselineGenerator.NormalizeVersionSegment(current.Version);
            var className = $"{options.ContractName}_{previousSegment}_To_{currentSegment}_Migration";
            var filePath = Path.Combine(migrationsRoot, className + ".cs");

            if (File.Exists(filePath))
            {
                WriteLine(output, $"Migration '{className}' already exists. Skipping.");
                continue;
            }

            var previousNamespace = $"{options.NamespaceRoot}.{options.ContractName}.{previousSegment}";
            var currentNamespace = $"{options.NamespaceRoot}.{options.ContractName}.{currentSegment}";
            var migrationsNamespace = options.NamespaceRoot.EndsWith(".Contracts", StringComparison.Ordinal)
                ? options.NamespaceRoot[..^10] + ".Migrations"
                : options.NamespaceRoot + ".Migrations";

            var builder = new StringBuilder();
            var previousInterfacePath = Path.Combine(previous.Directory.FullName, $"I{options.ContractName}.cs");
            var currentInterfacePath = Path.Combine(current.Directory.FullName, $"I{options.ContractName}.cs");

            var propertyMap = new Dictionary<string, string>(StringComparer.Ordinal);
            MergeProperties(propertyMap, previousInterfacePath);
            MergeProperties(propertyMap, currentInterfacePath);

            builder.AppendLine("using MessagePack;");
            builder.AppendLine("using Idb.Libraries.Abstraction.Services.Migrations;");
            builder.AppendLine("using Idb.Libraries.Abstractions.Dtos;");
            builder.AppendLine("using Idb.Libraries.Abstractions.Services.Serialization;");
            builder.AppendLine($"using {previousNamespace};");
            builder.AppendLine($"using {currentNamespace};");
            builder.AppendLine();
            builder.AppendLine($"namespace {migrationsNamespace};");
            builder.AppendLine();
            builder.AppendLine("[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]");
            builder.AppendLine($"public sealed class {className} : {previousNamespace}.I{options.ContractName}, {currentNamespace}.I{options.ContractName}, IMigration");
            builder.AppendLine("{");
            builder.AppendLine("    [IgnoreMember]");
            builder.AppendLine($"    public IObjectEnvelope<{previousNamespace}.I{options.ContractName}>? LegacyEnvelope {{ get; set; }};");
            builder.AppendLine("    [IgnoreMember]");
            builder.AppendLine($"    public IObjectEnvelope<{currentNamespace}.I{options.ContractName}>? CurrentEnvelope {{ get; set; }};");
            builder.AppendLine();
            builder.AppendLine($"    public string SourceVersion => \"{previous.Version}\";");
            builder.AppendLine($"    public string TargetVersion => \"{current.Version}\";");
            builder.AppendLine();
            builder.AppendLine($"    [IgnoreMember]");
            builder.AppendLine($"    IObjectEnvelope<{previousNamespace}.I{options.ContractName}>? IStoredContract<{previousNamespace}.I{options.ContractName}>.Envelope => LegacyEnvelope;");
            builder.AppendLine();
            builder.AppendLine("    [IgnoreMember]");
            builder.AppendLine($"    IObjectEnvelope<{currentNamespace}.I{options.ContractName}>? IStoredContract<{currentNamespace}.I{options.ContractName}>.Envelope => CurrentEnvelope;");
            builder.AppendLine();

            if (propertyMap.Count == 0)
            {
                builder.AppendLine("    // TODO: Define contract members shared by both versions.");
            }
            else
            {
                foreach (var property in propertyMap)
                {
                    builder.AppendLine($"    public {property.Value} {property.Key} {{ get; set; }}");
                    builder.AppendLine();
                }
            }

            builder.AppendLine("    public bool Up()");
            builder.AppendLine("    {");
            builder.AppendLine("        // TODO: Map legacy state to the new schema. Return false when migration cannot be completed.");
            builder.AppendLine("        return true;");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine("    public bool Down()");
            builder.AppendLine("    {");
            builder.AppendLine("        // TODO: Map current state back to the legacy schema. Return false when rollback cannot be completed.");
            builder.AppendLine("        return true;");
            builder.AppendLine("    }");
            builder.AppendLine("}");

            File.WriteAllText(filePath, builder.ToString());
            WriteLine(output, $"Migration scaffold '{className}' created at {filePath}.");
            createdAny = true;
        }

        if (!createdAny)
        {
            WriteLine(output, "All adjacent migrations already exist. Nothing to scaffold.");
        }

        return createdAny;
    }

    private static void WriteLine(TextWriter? output, string message)
    {
        if (output is null)
        {
            Console.WriteLine(message);
        }
        else
        {
            output.WriteLine(message);
        }
    }

    private static void MergeProperties(IDictionary<string, string> propertyMap, string interfacePath)
    {
        if (!File.Exists(interfacePath))
        {
            return;
        }

        var propertyPattern = new Regex(@"^\s*(?:public\s+)?(?<type>[\w<>\[\],\?\.]+)\s+(?<name>[A-Za-z_][\w]*)\s*\{\s*get;.*$", RegexOptions.Compiled);
        foreach (var rawLine in File.ReadAllLines(interfacePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var match = propertyPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var type = match.Groups["type"].Value;
            var name = match.Groups["name"].Value;

            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            propertyMap[name] = type;
        }
    }
}

