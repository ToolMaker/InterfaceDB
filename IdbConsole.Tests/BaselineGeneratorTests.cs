using System;
using System.IO;
using Idb.Libraries.Implementation.Baselines;
using Idb.Libraries.Abstractions.Enums;

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
                new[] { "AccountImpl", "SecondaryImpl" },
                VersionIncrement.Patch,
                ExplicitVersion: null,
                ProjectRoot: null);

            var result = BaselineGenerator.Generate(projectRoot, options);
            var version = new Version(result.NewVersion);
            var versionFolder = Path.Combine(
                projectRoot,
                "Contracts",
                "Account",
                BaselineGenerator.NormalizeVersionSegment(version));

            var interfacePath = Path.Combine(versionFolder, "IAccount.cs");
            Assert.True(File.Exists(interfacePath));

            var interfaceContent = File.ReadAllText(interfacePath);
            Assert.Contains("namespace Custom.Namespace.Account.v1_0_0;", interfaceContent, StringComparison.Ordinal);
            Assert.Contains("[StorageScope(VersionTag = \"1.0.0\")]", interfaceContent, StringComparison.Ordinal);

            var implementationPaths = new[]
            {
                Path.Combine(versionFolder, "AccountImpl.cs"),
                Path.Combine(versionFolder, "SecondaryImpl.cs"),
            };

            foreach (var implementationPath in implementationPaths)
            {
                Assert.True(File.Exists(implementationPath));

                var implementationContent = File.ReadAllText(implementationPath);
                Assert.Contains("namespace Custom.Namespace.Account.v1_0_0;", implementationContent, StringComparison.Ordinal);
                Assert.Contains("public sealed class", implementationContent, StringComparison.Ordinal);
            }
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
                Array.Empty<string>(),
                VersionIncrement.Patch,
                ExplicitVersion: "2.1.3",
                ProjectRoot: null);

            var result = BaselineGenerator.Generate(projectRoot, options);
            Assert.Equal("2.1.3", result.NewVersion);

            var versionFolder = Path.Combine(
                projectRoot,
                "Contracts",
                "Invoice",
                BaselineGenerator.NormalizeVersionSegment(new Version(result.NewVersion)));

            Assert.True(Directory.Exists(versionFolder));
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
            var args = new[] { "baseline", "Customer", "--dbpath", overrideRoot };
            var options = BaselineOptions.Parse(args, "MigrationTool.Contracts", "Contracts");
            Assert.NotNull(options.ProjectRoot);

            var projectRoot = options.ProjectRoot!;

            var result = BaselineGenerator.Generate(projectRoot, options);
            Assert.Equal("1.0.0", result.NewVersion);

            var versionFolder = Path.Combine(
                projectRoot,
                "Contracts",
                "Customer",
                BaselineGenerator.NormalizeVersionSegment(new Version(result.NewVersion)));

            Assert.True(Directory.Exists(versionFolder));
            Assert.True(File.Exists(Path.Combine(versionFolder, "ICustomer.cs")));
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
