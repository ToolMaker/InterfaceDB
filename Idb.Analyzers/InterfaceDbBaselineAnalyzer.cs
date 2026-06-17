using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Idb.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InterfaceDbBaselineAnalyzer : DiagnosticAnalyzer
{
    public const string MissingContractId = "IDB001";
    public const string UnmanagedContractId = "IDB002";
    public const string VersionMismatchId = "IDB003";
    public const string InvalidManifestId = "IDB004";

    private const string ManifestFileName = ".interfacedb-baseline";
    private const string StorageScopeAttributeName =
        "Idb.Libraries.Abstractions.Attributes.StorageScopeAttribute";

    private static readonly DiagnosticDescriptor MissingContract = new(
        MissingContractId,
        "InterfaceDB baseline contract is missing",
        "Baseline '{0}' version '{1}' expects stored contract '{2}', but that type is no longer present. Restore it or create a new version and migration before renaming or moving the contract.",
        "InterfaceDB.Migration",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly DiagnosticDescriptor UnmanagedContract = new(
        UnmanagedContractId,
        "InterfaceDB contract has no baseline identity",
        "Stored contract '{0}' version '{1}' has no .interfacedb-baseline manifest. Generate or update its baseline before persisting data.",
        "InterfaceDB.Migration",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly DiagnosticDescriptor VersionMismatch = new(
        VersionMismatchId,
        "InterfaceDB baseline version changed in place",
        "Stored contract '{0}' declares version '{1}', but its immutable baseline manifest records '{2}'. Create a new baseline and migration instead of changing an existing version.",
        "InterfaceDB.Migration",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly DiagnosticDescriptor InvalidManifest = new(
        InvalidManifestId,
        "InterfaceDB baseline manifest is invalid",
        "Baseline manifest '{0}' is invalid: {1}",
        "InterfaceDB.Migration",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(MissingContract, UnmanagedContract, VersionMismatch, InvalidManifest);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(AnalyzeCompilation);
    }

    private static void AnalyzeCompilation(CompilationAnalysisContext context)
    {
        var manifests = ReadManifests(context);
        if (manifests.Any(manifest => !manifest.IsValid))
        {
            return;
        }

        var contracts = EnumerateTypes(context.Compilation.Assembly.GlobalNamespace)
            .Where(IsStoredContract)
            .ToArray();
        var contractsByMetadataName = contracts.ToDictionary(
            GetMetadataName,
            StringComparer.Ordinal);
        var manifestsByMetadataName = manifests
            .Where(manifest => manifest.IsValid)
            .GroupBy(manifest => manifest.MetadataName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var manifest in manifests.Where(manifest => manifest.IsValid))
        {
            if (!contractsByMetadataName.TryGetValue(manifest.MetadataName, out var contract))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    MissingContract,
                    manifest.Location,
                    manifest.LogicalName,
                    manifest.Version,
                    manifest.MetadataName));
                continue;
            }

            var declaredVersion = GetStorageVersion(contract);
            if (!string.Equals(declaredVersion, manifest.Version, StringComparison.Ordinal))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    VersionMismatch,
                    contract.Locations.FirstOrDefault(),
                    manifest.MetadataName,
                    declaredVersion ?? "<missing>",
                    manifest.Version));
            }
        }

        foreach (var contract in contracts)
        {
            var metadataName = GetMetadataName(contract);
            if (manifestsByMetadataName.ContainsKey(metadataName))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                UnmanagedContract,
                contract.Locations.FirstOrDefault(),
                metadataName,
                GetStorageVersion(contract) ?? "<missing>"));
        }
    }

    private static List<BaselineManifest> ReadManifests(CompilationAnalysisContext context)
    {
        var manifests = new List<BaselineManifest>();
        foreach (var additionalFile in context.Options.AdditionalFiles)
        {
            if (!string.Equals(Path.GetFileName(additionalFile.Path), ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = additionalFile.GetText(context.CancellationToken);
            if (text is null)
            {
                continue;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in text.Lines)
            {
                var value = line.ToString().Trim();
                if (value.Length == 0 || value.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var separator = value.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                values[value.Substring(0, separator).Trim()] =
                    value.Substring(separator + 1).Trim();
            }

            values.TryGetValue("logical-name", out var logicalName);
            values.TryGetValue("version", out var version);
            values.TryGetValue("metadata-name", out var metadataName);
            var location = Location.Create(
                additionalFile.Path,
                new TextSpan(0, 0),
                new LinePositionSpan(new LinePosition(0, 0), new LinePosition(0, 0)));

            var missing = new[]
                {
                    string.IsNullOrWhiteSpace(logicalName) ? "logical-name" : null,
                    string.IsNullOrWhiteSpace(version) ? "version" : null,
                    string.IsNullOrWhiteSpace(metadataName) ? "metadata-name" : null
                }
                .Where(value => value is not null)
                .ToArray();

            if (missing.Length > 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidManifest,
                    location,
                    additionalFile.Path,
                    "missing " + string.Join(", ", missing)));
                manifests.Add(BaselineManifest.Invalid(location));
                continue;
            }

            manifests.Add(new BaselineManifest(logicalName!, version!, metadataName!, location, true));
        }

        return manifests;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol namespaceSymbol)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in EnumerateNestedTypes(type))
            {
                yield return nested;
            }
        }

        foreach (var childNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            foreach (var type in EnumerateTypes(childNamespace))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNestedTypes(INamedTypeSymbol containingType)
    {
        foreach (var nested in containingType.GetTypeMembers())
        {
            yield return nested;
            foreach (var child in EnumerateNestedTypes(nested))
            {
                yield return child;
            }
        }
    }

    private static bool IsStoredContract(INamedTypeSymbol type) =>
        type.TypeKind == TypeKind.Interface
        && type.GetAttributes().Any(attribute =>
            string.Equals(
                attribute.AttributeClass?.ToDisplayString(),
                StorageScopeAttributeName,
                StringComparison.Ordinal));

    private static string? GetStorageVersion(INamedTypeSymbol type)
    {
        var attribute = type.GetAttributes().FirstOrDefault(candidate =>
            string.Equals(
                candidate.AttributeClass?.ToDisplayString(),
                StorageScopeAttributeName,
                StringComparison.Ordinal));
        if (attribute is null)
        {
            return null;
        }

        foreach (var argument in attribute.NamedArguments)
        {
            if (string.Equals(argument.Key, "VersionTag", StringComparison.Ordinal)
                && argument.Value.Value is string version)
            {
                return version;
            }
        }

        return null;
    }

    private static string GetMetadataName(INamedTypeSymbol type)
    {
        var names = new Stack<string>();
        ISymbol? current = type;
        while (current is INamedTypeSymbol namedType)
        {
            names.Push(namedType.MetadataName);
            current = namedType.ContainingType;
        }

        var typeName = string.Join("+", names);
        var namespaceName = type.ContainingNamespace?.ToDisplayString();
        return string.IsNullOrWhiteSpace(namespaceName)
            ? typeName
            : namespaceName + "." + typeName;
    }

    private sealed class BaselineManifest
    {
        public BaselineManifest(
            string logicalName,
            string version,
            string metadataName,
            Location location,
            bool isValid)
        {
            LogicalName = logicalName;
            Version = version;
            MetadataName = metadataName;
            Location = location;
            IsValid = isValid;
        }

        public string LogicalName { get; }
        public string Version { get; }
        public string MetadataName { get; }
        public Location Location { get; }
        public bool IsValid { get; }

        public static BaselineManifest Invalid(Location location) =>
            new(string.Empty, string.Empty, string.Empty, location, false);
    }
}
