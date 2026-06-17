using System.Collections.Immutable;
using Idb.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Idb.Analyzers.Tests;

[TestClass]
public sealed class InterfaceDbBaselineAnalyzerTests
{
    [TestMethod]
    public async Task MatchingBaselineProducesNoDiagnostics()
    {
        var diagnostics = await AnalyzeAsync(
            Contract("Demo.Contracts.Person.v1_0_0", "IPerson", "1.0.0"),
            Manifest("Person", "1.0.0", "Demo.Contracts.Person.v1_0_0.IPerson"));

        Assert.AreEqual(0, diagnostics.Length);
    }

    [TestMethod]
    public async Task RenamedContractProducesMissingAndUnmanagedDiagnostics()
    {
        var diagnostics = await AnalyzeAsync(
            Contract("Demo.Contracts.Person.v1_0_0", "IRenamedPerson", "1.0.0"),
            Manifest("Person", "1.0.0", "Demo.Contracts.Person.v1_0_0.IPerson"));

        CollectionAssert.AreEquivalent(
            new[] { InterfaceDbBaselineAnalyzer.MissingContractId, InterfaceDbBaselineAnalyzer.UnmanagedContractId },
            diagnostics.Select(diagnostic => diagnostic.Id).ToArray());
    }

    [TestMethod]
    public async Task ChangingVersionInPlaceProducesVersionMismatch()
    {
        var diagnostics = await AnalyzeAsync(
            Contract("Demo.Contracts.Person.v1_0_0", "IPerson", "2.0.0"),
            Manifest("Person", "1.0.0", "Demo.Contracts.Person.v1_0_0.IPerson"));

        Assert.AreEqual(1, diagnostics.Length);
        Assert.AreEqual(InterfaceDbBaselineAnalyzer.VersionMismatchId, diagnostics[0].Id);
    }

    [TestMethod]
    public async Task InvalidManifestProducesDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            Contract("Demo.Contracts.Person.v1_0_0", "IPerson", "1.0.0"),
            "logical-name=Person");

        Assert.AreEqual(1, diagnostics.Length);
        Assert.AreEqual(InterfaceDbBaselineAnalyzer.InvalidManifestId, diagnostics[0].Id);
    }

    [TestMethod]
    public async Task ContractWithoutManifestProducesUnmanagedDiagnostic()
    {
        var diagnostics = await AnalyzeAsync(
            Contract("Demo.Contracts.Person.v1_0_0", "IPerson", "1.0.0"),
            manifest: null);

        Assert.AreEqual(1, diagnostics.Length);
        Assert.AreEqual(InterfaceDbBaselineAnalyzer.UnmanagedContractId, diagnostics[0].Id);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, string? manifest)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrWhiteSpace(assembly.Location))
            .Select(assembly => MetadataReference.CreateFromFile(assembly.Location))
            .Cast<MetadataReference>()
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "AnalyzerTests",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var additionalFiles = manifest is null
            ? ImmutableArray<AdditionalText>.Empty
            : ImmutableArray.Create<AdditionalText>(
                new InMemoryAdditionalText(".interfacedb-baseline", manifest));
        var options = new AnalyzerOptions(additionalFiles);

        return await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new InterfaceDbBaselineAnalyzer()), options)
            .GetAnalyzerDiagnosticsAsync();
    }

    private static string Contract(string contractNamespace, string name, string version) => $$"""
        using System;

        namespace Idb.Libraries.Abstractions.Attributes
        {
            [AttributeUsage(AttributeTargets.Interface)]
            public sealed class StorageScopeAttribute : Attribute
            {
                public string VersionTag { get; init; }
            }
        }

        namespace {{contractNamespace}}
        {
            [Idb.Libraries.Abstractions.Attributes.StorageScope(VersionTag = "{{version}}")]
            public interface {{name}}
            {
            }
        }
        """;

    private static string Manifest(string logicalName, string version, string metadataName) => $$"""
        # InterfaceDB baseline identity v1
        logical-name={{logicalName}}
        version={{version}}
        metadata-name={{metadataName}}
        """;

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public InMemoryAdditionalText(string path, string text)
        {
            Path = path;
            _text = SourceText.From(text);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }
}
