using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Idb.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OrdinalSearchAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "IDB005";
    private const string Attributes = "Idb.Libraries.Abstractions.Attributes.";
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId, "String search cannot use its ordinal index",
        "'{0}' on indexed member '{1}' uses a non-ordinal comparison; specify StringComparison.Ordinal only if those semantics are intended",
        "InterfaceDB.Performance", DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "Culture-sensitive string comparison preserves its meaning by scanning. No automatic semantic conversion is safe.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);
    }

    private static void Analyze(OperationAnalysisContext context)
    {
        var call = (IInvocationOperation)context.Operation;
        if (call.TargetMethod.ContainingType.SpecialType != SpecialType.System_String
            || call.TargetMethod.Name is not ("StartsWith" or "EndsWith")
            || call.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0)?.Value.Type?.SpecialType != SpecialType.System_String)
            return;
        foreach (var argument in call.Arguments)
        {
            if (argument.Parameter?.Type.ToDisplayString() != "System.StringComparison") continue;
            // A runtime-supplied comparison may be ordinal; avoid speculative warnings.
            if (!argument.Value.ConstantValue.HasValue || Equals(argument.Value.ConstantValue.Value, 4)) return;
        }

        IAnonymousFunctionOperation? predicate = null;
        for (var current = call.Parent; current is not null; current = current.Parent)
        {
            if (current is IAnonymousFunctionOperation lambda) { predicate = lambda; break; }
        }
        if (predicate is null || predicate.Symbol.Parameters.Length != 1) return;
        IOperation? owner = predicate.Parent;
        while (owner is IDelegateCreationOperation or IConversionOperation) owner = owner.Parent;
        if (owner is not IArgumentOperation { Parent: IInvocationOperation search }
            || search.TargetMethod.Name is not ("SearchManyAsync" or "SearchFirstAsync" or "SearchSingleAsync" or "SearchAsync" or "ExplainSearch")
            || !IsRepository(search.TargetMethod.ContainingType)) return;

        var receiver = Unwrap(call.Instance);
        var member = receiver switch
        {
            IPropertyReferenceOperation property => (ISymbol)property.Property,
            IFieldReferenceOperation field => field.Field,
            _ => null
        };
        if (member is null || !SupportsIndex(member, call.TargetMethod.Name == "StartsWith" ? 4 : 8)) return;
        var instance = receiver switch
        {
            IPropertyReferenceOperation property => Unwrap(property.Instance),
            IFieldReferenceOperation field => Unwrap(field.Instance),
            _ => null
        };
        // Only direct members of the searched contract. Nested/member-transformed expressions
        // need separate planner-aware analysis, not a warning based just on matching names.
        if (instance is not IParameterReferenceOperation parameter
            || !SymbolEqualityComparer.Default.Equals(parameter.Parameter, predicate.Symbol.Parameters[0])) return;
        context.ReportDiagnostic(Diagnostic.Create(Rule, call.Syntax.GetLocation(), call.TargetMethod.Name, member.Name));
    }

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion) operation = conversion.Operand;
        return operation;
    }

    private static bool IsRepository(INamedTypeSymbol type)
        => IsContract(type) || type.AllInterfaces.Any(IsContract);

    private static bool IsContract(INamedTypeSymbol type)
        => (type.OriginalDefinition.MetadataName == "IRepository`1"
            && type.ContainingNamespace.ToDisplayString() == "Idb.Libraries.Abstractions.Services.Repository")
            || (type.OriginalDefinition.MetadataName == "IIndexManager`1"
            && type.ContainingNamespace.ToDisplayString() == "Idb.Libraries.Abstractions.Services.Indexes");

    private static bool SupportsIndex(ISymbol member, int required)
    {
        if (Has(member, "NotSearchIndexedAttribute") || Has(member, "EncryptedStorageFieldAttribute")) return false;
        var attribute = member.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == Attributes + "SearchIndexAttribute");
        if (attribute is null
            && (Has(member.ContainingType, "NotSearchIndexedAttribute") || !Has(member.ContainingType, "SearchIndexAttribute")))
            return false;
        var mode = attribute is null || attribute.ConstructorArguments.Length == 0
            ? 0 : Convert.ToInt32(attribute.ConstructorArguments[0].Value);
        // Match SearchIndexDiscovery.NormalizeMode for strings. A type-level
        // attribute enables discovery; only member attributes select explicit modes.
        if (mode == 0) mode = 1 | 4; // Exact | Prefix, not Suffix.
        return (mode & required) != 0;
    }

    private static bool Has(ISymbol symbol, string name)
        => symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == Attributes + name);
}
