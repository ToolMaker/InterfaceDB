using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
            var className = $"I{options.ContractName}_{previousSegment}_To_{currentSegment}_Migration";
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
            builder.AppendLine($"namespace {migrationsNamespace};");
            builder.AppendLine();
            builder.AppendLine("using System.Threading;");
            builder.AppendLine("using System.Threading.Tasks;");
            builder.AppendLine($"using {previousNamespace};");
            builder.AppendLine($"using {currentNamespace};");
            builder.AppendLine("using Idb.Libraries.Abstractions.Services.Migrations;");
            builder.AppendLine("using Idb.Libraries.Implementation.Services.Migrations;");
            builder.AppendLine();
            builder.AppendLine($"[StorageMigration(typeof({currentNamespace}.I{options.ContractName}), \"{previous.Version}\", \"{current.Version}\")]");
            builder.AppendLine($"public sealed class {className} : StorageMigrationBase<{currentNamespace}.I{options.ContractName}>");
            builder.AppendLine("{");
            builder.AppendLine("    protected override async Task ExecuteCoreAsync(IStorageMigrationContext context, CancellationToken cancellationToken)");
            builder.AppendLine("    {");
            builder.AppendLine($"        await foreach (var record in context.ReadSourceAsync<{previousNamespace}.I{options.ContractName}>(cancellationToken).ConfigureAwait(false))");
            builder.AppendLine("        {");
            builder.AppendLine("            // TODO: inspect record.Value and map to the new contract shape.");
            builder.AppendLine("            throw new NotImplementedException();");
            builder.AppendLine("        }");
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
}
