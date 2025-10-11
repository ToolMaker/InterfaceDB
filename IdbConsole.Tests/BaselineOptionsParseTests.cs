using System;
using System.Collections.Generic;
using System.IO;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Implementation.Services.Baselines;

namespace IdbConsole.Tests;

public sealed class BaselineOptionsParseTests
{
    private const string DefaultNamespace = "MigrationTool.Contracts";
    private const string DefaultContracts = "Contracts";
    private const string DefaultStorage = "Storage";
    private const string DefaultMigrations = "Migrations";

    [Fact]
    public void Parse_WithoutContractsDir_Throws()
    {
        var args = new[] { "baseline", "Person" };

        var ex = Assert.Throws<ArgumentException>(() => BaselineOptions.Parse(args, DefaultNamespace, DefaultContracts, DefaultStorage, DefaultMigrations));
        Assert.Equal("The '--contracts-dir' option is required.", ex.Message);
    }

    [Fact]
    public void Parse_WithContractNameAndContractsDir_UsesDefaults()
    {
        var args = new[] { "baseline", "Person", "--contracts-dir", DefaultContracts };

        var options = BaselineOptions.Parse(args, DefaultNamespace, DefaultContracts, DefaultStorage, DefaultMigrations);

        Assert.Equal("Person", options.ContractName);
        Assert.Equal(DefaultNamespace, options.NamespaceRoot);
        Assert.Equal(DefaultContracts, options.ContractsFolder);
        Assert.Equal(DefaultStorage, options.StorageFolder);
        Assert.Equal(DefaultMigrations, options.MigrationFolder);
        Assert.Empty(options.Implementations);
        Assert.Equal(VersionIncrementType.Patch, options.Increment);
        Assert.Null(options.ExplicitVersion);
        Assert.Null(options.ProjectRoot);
        Assert.Equal(BaselineExistingBehavior.Prompt, options.ExistingBehavior);
        Assert.Equal(BaselineIncrementBehavior.Auto, options.IncrementBehavior);
    }

    [Fact]
    public void Parse_WithImplementations_SplitsAndTrims()
    {
        var args = new[] { "baseline", "Account", "--contracts-dir", DefaultContracts, "--impl", "Core , Extra", "--impl", "Premium" };

        var options = BaselineOptions.Parse(args, DefaultNamespace, DefaultContracts, DefaultStorage, DefaultMigrations);

        Assert.Collection(options.Implementations,
            impl => Assert.Equal("Core", impl),
            impl => Assert.Equal("Extra", impl),
            impl => Assert.Equal("Premium", impl));
    }

    [Fact]
    public void Parse_WithNamespaceOverride_UsesProvidedNamespace()
    {
        var args = new[] { "baseline", "Account", "--contracts-dir", DefaultContracts, "--namespace", "Custom.Namespace" };

        var options = BaselineOptions.Parse(args, DefaultNamespace, DefaultContracts, DefaultStorage, DefaultMigrations);

        Assert.Equal("Custom.Namespace", options.NamespaceRoot);
    }

    [Fact]
    public void Parse_WithContractsOverride_UsesProvidedFolder()
    {
        var args = new[] { "baseline", "Account", "--contracts-dir", "ContractsRoot" };

        var options = BaselineOptions.Parse(args, DefaultNamespace, DefaultContracts, DefaultStorage, DefaultMigrations);

        Assert.Equal("ContractsRoot", options.ContractsFolder);
    }

    [Fact]
    public void Parse_WithStorageOverride_UsesProvidedFolder()
    {
        var args = new[] { "baseline", "Account", "--contracts-dir", DefaultContracts, "--storage-dir", "StorageRoot" };

        var options = BaselineOptions.Parse(args, DefaultNamespace, DefaultContracts, DefaultStorage, DefaultMigrations);

        Assert.Equal("StorageRoot", options.StorageFolder);
    }

    [Fact]
    public void Parse_WithMigrationOverride_UsesProvidedFolder()
    {
        var args = new[] { "baseline", "Account", "--contracts-dir", DefaultContracts, "--migration-dir", "CustomMigrations" };

        var options = BaselineOptions.Parse(args, DefaultNamespace, DefaultContracts, DefaultStorage, DefaultMigrations);

        Assert.Equal("CustomMigrations", options.MigrationFolder);
    }

    [Theory]
    [InlineData("major", VersionIncrementType.Major)]
    [InlineData("minor", VersionIncrementType.Minor)]
    [InlineData("patch", VersionIncrementType.Patch)]
    [InlineData("MaJoR", VersionIncrementType.Major)]
    public void Parse_WithVersionIncrementOverride_ParsesEnum(string input, VersionIncrementType expected)
    {
        var args = new[] { "baseline", "Account", "--contracts-dir", DefaultContracts, "--increment", input };

        var options = BaselineOptions.Parse(args, DefaultNamespace, DefaultContracts, DefaultStorage, DefaultMigrations);

        Assert.Equal(expected, options.Increment);
    }

    [Fact]
    public void Parse_WithExplicitVersion_UsesProvidedVersion()
    {
        var args = new[] { "baseline", "Account", "--contracts-dir", DefaultContracts, "--version", "2.0.1" };

        var options = BaselineOptions.Parse(args, DefaultNamespace, DefaultContracts, DefaultStorage, DefaultMigrations);

        Assert.Equal("2.0.1", options.ExplicitVersion);
    }

    [Fact]
    public void Parse_WithOverwriteOption_Throws()
    {
        var args = new[] { "baseline", "Account", "--contracts-dir", DefaultContracts, "--overwrite", "force" };

        var ex = Assert.Throws<ArgumentException>(() => BaselineOptions.Parse(args, DefaultNamespace, DefaultContracts, DefaultStorage, DefaultMigrations));

        Assert.Equal("Unknown option '--overwrite'", ex.Message);
    }

    [Fact]
    public void Parse_WithIncrementPromptBehavior_Throws()
    {
        var args = new[] { "baseline", "Account", "--contracts-dir", DefaultContracts, "--increment", "prompt" };

        var ex = Assert.Throws<ArgumentException>(() => BaselineOptions.Parse(args, DefaultNamespace, DefaultContracts, DefaultStorage, DefaultMigrations));

        Assert.Equal("Unknown increment option 'prompt'. Expected major, minor, or patch.", ex.Message);
    }

    [Fact]
    public void Parse_WithDbPath_ResolvesAbsolutePath()
    {
        var relativePath = Path.Combine(".", "tempDirectory");
        var expectedPath = Path.GetFullPath(relativePath);
        var args = new[] { "baseline", "Account", "--contracts-dir", DefaultContracts, "--dbpath", relativePath };

        var options = BaselineOptions.Parse(args, DefaultNamespace, DefaultContracts, DefaultStorage, DefaultMigrations);

        Assert.Equal(expectedPath, options.ProjectRoot);
    }

    [Theory]
    [InlineData("--impl")]
    [InlineData("--namespace")]
    [InlineData("--contracts-dir")]
    [InlineData("--storage-dir")]
    [InlineData("--migration-dir")]
    [InlineData("--increment")]
    [InlineData("--increment-mode")]
    [InlineData("--version")]
    [InlineData("--dbpath")]
    public void Parse_MissingValueForOption_ThrowsArgumentException(string option)
    {
        var args = new List<string> { "baseline", "Account", "--contracts-dir", DefaultContracts, option };

        var ex = Assert.Throws<ArgumentException>(() => BaselineOptions.Parse(args.ToArray(), DefaultNamespace, DefaultContracts, DefaultStorage, DefaultMigrations));
        Assert.Contains(option, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_WithUnknownVersionIncrement_Throws()
    {
        var args = new[] { "baseline", "Account", "--contracts-dir", DefaultContracts, "--increment", "build" };

        var ex = Assert.Throws<ArgumentException>(() => BaselineOptions.Parse(args, DefaultNamespace, DefaultContracts, DefaultStorage, DefaultMigrations));

        Assert.Equal("Unknown increment option 'build'. Expected major, minor, or patch.", ex.Message);
    }

    [Fact]
    public void Parse_WithUnknownOption_Throws()
    {
        var args = new[] { "baseline", "Account", "--contracts-dir", DefaultContracts, "--unknown" };

        var ex = Assert.Throws<ArgumentException>(() => BaselineOptions.Parse(args, DefaultNamespace, DefaultContracts, DefaultStorage, DefaultMigrations));

        Assert.Equal("Unknown option '--unknown'", ex.Message);
    }
}
