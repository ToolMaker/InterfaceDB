using System.Collections.Immutable;
using Idb.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Idb.Analyzers.Tests;

[TestClass]
public sealed class OrdinalSearchAnalyzerTests
{
    [TestMethod]
    [DataRow("x.Name.StartsWith(\"a\")", 1)]
    [DataRow("x.Name.EndsWith(\"z\")", 0)]
    [DataRow("x.Name.StartsWith(\"a\", StringComparison.CurrentCulture)", 1)]
    [DataRow("x.Name.EndsWith(\"z\", StringComparison.OrdinalIgnoreCase)", 0)]
    [DataRow("x.Name.StartsWith(value: \"a\", comparisonType: StringComparison.CurrentCulture)", 1)]
    [DataRow("x.Name.StartsWith(value: \"a\", comparisonType: StringComparison.Ordinal)", 0)]
    [DataRow("x.Name.StartsWith(\"a\", StringComparison.Ordinal)", 0)]
    [DataRow("x.Name.StartsWith(\"a\", comparison)", 0)]
    [DataRow("x.Name.Contains(\"a\")", 0)]
    [DataRow("x.Plain.StartsWith(\"a\")", 0)]
    [DataRow("x.Secret.StartsWith(\"a\")", 0)]
    [DataRow("x.ExactOnly.StartsWith(\"a\")", 0)]
    [DataRow("x.PrefixOnly.EndsWith(\"a\")", 0)]
    [DataRow("x.PrefixOnly.StartsWith(\"a\")", 1)]
    [DataRow("x.ExplicitDefault.StartsWith(\"a\")", 1)]
    [DataRow("x.ExplicitDefault.EndsWith(\"z\")", 0)]
    [DataRow("x.SuffixOnly.EndsWith(\"z\")", 1)]
    [DataRow("x.SuffixOnly.EndsWith(\"z\", StringComparison.OrdinalIgnoreCase)", 1)]
    [DataRow("x.SuffixOnly.EndsWith(\"z\", StringComparison.Ordinal)", 0)]
    [DataRow("x.SuffixOnly.StartsWith(\"a\")", 0)]
    [DataRow("x.All.EndsWith(\"z\")", 1)]
    [DataRow("x.Name.ToLower().StartsWith(\"a\")", 0)]
    [DataRow("\"constant\".StartsWith(\"a\")", 0)]
    [DataRow("x.Name.StartsWith('a')", 0)]
    public async Task WarnsOnlyForKnownIndexedNonOrdinalSearch(string expression, int expected)
    {
        var diagnostics = await Analyze(expression);
        Assert.HasCount(expected, diagnostics);
        Assert.IsTrue(diagnostics.All(d => d.Id == OrdinalSearchAnalyzer.DiagnosticId));
    }

    [TestMethod]
    public async Task OrdinaryCodeAndUnrelatedSearchMethodsDoNotWarn()
    {
        Assert.IsEmpty(await Analyze("x.Name.StartsWith(\"a\")", repositoryNamespace: "Other"));
        Assert.IsEmpty(await Analyze("x.Name.StartsWith(\"a\")", method: "QueryManyAsync"));
    }

    [TestMethod]
    public async Task TypeLevelIndexModeStillUsesDefaultModesForMembers()
    {
        Assert.IsEmpty(await Analyze("x.Name.EndsWith(\"z\")", typeIndex: "[SearchIndex(8)]"));
        Assert.HasCount(1, await Analyze("x.Name.StartsWith(\"a\")", typeIndex: "[SearchIndex(8)]"));
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string expression,
        string repositoryNamespace = "Idb.Libraries.Abstractions.Services.Repository", string method = "SearchManyAsync",
        string typeIndex = "[SearchIndex]")
    {
        var source = $$"""
            using System;
            using System.Linq.Expressions;
            using Idb.Libraries.Abstractions.Attributes;
            namespace Idb.Libraries.Abstractions.Attributes {
                [AttributeUsage(AttributeTargets.All)] public sealed class SearchIndexAttribute(int mode=0) : Attribute {}
                [AttributeUsage(AttributeTargets.All)] public sealed class NotSearchIndexedAttribute : Attribute {}
                [AttributeUsage(AttributeTargets.All)] public sealed class EncryptedStorageFieldAttribute : Attribute {}
            }
            namespace {{repositoryNamespace}} {
                public interface IRepository<TContract> { void {{method}}(Expression<Func<TContract,bool>> predicate); }
            }
            {{typeIndex}} public interface IRow {
                string Name { get; }
                [NotSearchIndexed] string Plain { get; }
                [EncryptedStorageField] string Secret { get; }
                [SearchIndex(1)] string ExactOnly { get; }
                [SearchIndex(4)] string PrefixOnly { get; }
                [SearchIndex] string ExplicitDefault { get; }
                [SearchIndex(8)] string SuffixOnly { get; }
                [SearchIndex(31)] string All { get; }
            }
            public class Consumer {
                public void Run({{repositoryNamespace}}.IRepository<IRow> repository, StringComparison comparison) {
                    repository.{{method}}(x => {{expression}});
                }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("OrdinalTests", [CSharpSyntaxTree.ParseText(source)],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.IsFalse(compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error),
            string.Join("\n", compilation.GetDiagnostics()));
        return await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new OrdinalSearchAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
    }
}
