using System;
using System.IO;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Implementation.Services.Baselines;

namespace IdbConsole.Tests;

public sealed class BaselineGeneratorTests
{
    [Fact]
    public void Generate_WithInitialBaseline_CreatesInterfaceAndImplementations()
    {
        var projectRoot = CreateTempDirectory();

        try
        {
            var options = new BaselineOptions(
                "Account",
                "Custom.Namespace",
                "Contracts",
                "Storage",
                "Migrations",
                new[] { "AccountImpl", "SecondaryImpl" },
                VersionIncrement.Patch,
                ExplicitVersion: null,
                ProjectRoot: null,
                ExistingBehavior: BaselineExistingBehavior.Force,
                IncrementBehavior: BaselineIncrementBehavior.Auto);

            var result = BaselineGenerator.Generate(projectRoot, options);
            var version = new Version(result.NewVersion);
            var segment = BaselineGenerator.NormalizeVersionSegment(version);
            var versionFolder = Path.Combine(projectRoot, "Contracts", "Account", segment);

            Assert.True(File.Exists(Path.Combine(versionFolder, "IAccount.cs")));
            Assert.True(Directory.Exists(Path.Combine(projectRoot, "Storage", "Account", segment)));
            Assert.True(Directory.Exists(Path.Combine(projectRoot, "Migrations", "Account")));
        }
        finally
        {
            DeleteDirectoryIfExists(projectRoot);
        }
    }

    [Fact]
    public void Generate_WithExplicitVersion_CreatesRequestedVersionFolder()
    {
        var projectRoot = CreateTempDirectory();

        try
        {
            var options = new BaselineOptions(
                "Invoice",
                "Custom.Namespace",
                "Contracts",
                "Storage",
                "Migrations",
                Array.Empty<string>(),
                VersionIncrement.Patch,
                ExplicitVersion: "2.1.3",
                ProjectRoot: null,
                ExistingBehavior: BaselineExistingBehavior.Force,
                IncrementBehavior: BaselineIncrementBehavior.Auto);

            var result = BaselineGenerator.Generate(projectRoot, options);
            Assert.Equal("2.1.3", result.NewVersion);
            var segment = BaselineGenerator.NormalizeVersionSegment(new Version(result.NewVersion));

            Assert.True(Directory.Exists(Path.Combine(projectRoot, "Contracts", "Invoice", segment)));
            Assert.True(Directory.Exists(Path.Combine(projectRoot, "Storage", "Invoice", segment)));
            Assert.True(Directory.Exists(Path.Combine(projectRoot, "Migrations", "Invoice")));
        }
        finally
        {
            DeleteDirectoryIfExists(projectRoot);
        }
    }

    [Fact]
    public void Generate_WithProjectRootOverride_UsesOverrideDirectory()
    {
        var overrideRoot = CreateTempDirectory();

        try
        {
            var args = new[] { "baseline", "Customer", "--contracts-dir", "Contracts", "--dbpath", overrideRoot };
            var options = BaselineOptions.Parse(args, "MigrationTool.Contracts", "Contracts", "Storage", "Migrations");
            Assert.NotNull(options.ProjectRoot);

            var projectRoot = options.ProjectRoot!;

            var result = BaselineGenerator.Generate(projectRoot, options);
            Assert.Equal("1.0.0", result.NewVersion);
            var segment = BaselineGenerator.NormalizeVersionSegment(new Version(result.NewVersion));

            Assert.True(Directory.Exists(Path.Combine(projectRoot, "Contracts", "Customer", segment)));
            Assert.True(Directory.Exists(Path.Combine(projectRoot, "Storage", "Customer", segment)));
            Assert.True(Directory.Exists(Path.Combine(projectRoot, "Migrations", "Customer")));
        }
        finally
        {
            DeleteDirectoryIfExists(overrideRoot);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
