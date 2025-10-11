using System;
using System.IO;
using Idb.Libraries.Implementation.Baselines;
using Idb.Libraries.Abstractions.Enums;

namespace IdbConsole.Tests;

public sealed class BaselineOptionsParseTests
{
    private const string DefaultNamespace = "MigrationTool.Contracts";
    private const string DefaultRoot = "Contracts";

    [Fact]
    public void Parse_WithContractNameOnly_UsesDefaults()
    {
        var args = new[] { "baseline", "Person" };

        var options = BaselineOptions.Parse(args, DefaultNamespace, DefaultRoot);

        Assert.Equal("Person", options.ContractName);
        Assert.Equal(DefaultNamespace, options.NamespaceRoot);
        Assert.Equal(DefaultRoot, options.RootFolder);
        Assert.Empty(options.Implementations);
        Assert.Equal(VersionIncrement.Patch, options.Increment);
        Assert.Null(options.ExplicitVersion);
        Assert.Null(options.ProjectRoot);
    }

    [Fact]
    public void Parse_WithImplementations_SplitsAndTrims()
    {
        var args = new[] { "baseline", "Account", "--impl", "Core , Extra", "--impl", "Premium" };

        var options = BaselineOptions.Parse(args, DefaultNamespace, DefaultRoot);

        Assert.Collection(options.Implementations,
            impl => Assert.Equal("Core", impl),
            impl => Assert.Equal("Extra", impl),
            impl => Assert.Equal("Premium", impl));
    }

    [Fact]
    public void Parse_WithNamespaceOverride_UsesProvidedNamespace()
    {
        var args = new[] { "baseline", "Account", "--namespace", "Custom.Namespace" };

        var options = BaselineOptions.Parse(args, DefaultNamespace, DefaultRoot);

        Assert.Equal("Custom.Namespace", options.NamespaceRoot);
    }

    [Fact]
    public void Parse_WithRootOverride_UsesProvidedRoot()
    {
        var args = new[] { "baseline", "Account", "--root", "ContractsRoot" };

        var options = BaselineOptions.Parse(args, DefaultNamespace, DefaultRoot);

        Assert.Equal("ContractsRoot", options.RootFolder);
    }

    [Theory]
    [InlineData("major", VersionIncrement.Major)]
    [InlineData("minor", VersionIncrement.Minor)]
    [InlineData("patch", VersionIncrement.Patch)]
    [InlineData("MaJoR", VersionIncrement.Major)]
    public void Parse_WithIncrementOverride_ParsesEnum(string input, VersionIncrement expected)
    {
        var args = new[] { "baseline", "Account", "--increment", input };

        var options = BaselineOptions.Parse(args, DefaultNamespace, DefaultRoot);

        Assert.Equal(expected, options.Increment);
    }

    [Fact]
    public void Parse_WithExplicitVersion_UsesProvidedVersion()
    {
        var args = new[] { "baseline", "Account", "--version", "2.0.1" };

        var options = BaselineOptions.Parse(args, DefaultNamespace, DefaultRoot);

        Assert.Equal("2.0.1", options.ExplicitVersion);
    }

    [Fact]
    public void Parse_WithDbPath_ResolvesAbsolutePath()
    {
        var relativePath = Path.Combine(".", "tempDirectory");
        var expectedPath = Path.GetFullPath(relativePath);
        var args = new[] { "baseline", "Account", "--dbpath", relativePath };

        var options = BaselineOptions.Parse(args, DefaultNamespace, DefaultRoot);

        Assert.Equal(expectedPath, options.ProjectRoot);
    }

    [Theory]
    [InlineData("--impl")]
    [InlineData("--namespace")]
    [InlineData("--root")]
    [InlineData("--increment")]
    [InlineData("--version")]
    [InlineData("--dbpath")]
    public void Parse_MissingValueForOption_ThrowsArgumentException(string option)
    {
        var args = new[] { "baseline", "Account", option };

        var ex = Assert.Throws<ArgumentException>(() => BaselineOptions.Parse(args, DefaultNamespace, DefaultRoot));
        Assert.Contains(option, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_WithUnknownIncrement_Throws()
    {
        var args = new[] { "baseline", "Account", "--increment", "build" };

        var ex = Assert.Throws<ArgumentException>(() => BaselineOptions.Parse(args, DefaultNamespace, DefaultRoot));

        Assert.Equal("Unknown increment.", ex.Message);
    }

    [Fact]
    public void Parse_WithUnknownOption_Throws()
    {
        var args = new[] { "baseline", "Account", "--unknown" };

        var ex = Assert.Throws<ArgumentException>(() => BaselineOptions.Parse(args, DefaultNamespace, DefaultRoot));

        Assert.Equal("Unknown option '--unknown'", ex.Message);
    }
}
