using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MigrationTool.Tests;

public sealed class BaselineCommandTests : IDisposable
{
    private readonly string _tempRoot;

    public BaselineCommandTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "MigrationToolTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void BaselineCommand_CreatesInitialStructure()
    {
        var contractsDir = Path.Combine(_tempRoot, "Abstractions");
        var storageDir = Path.Combine(_tempRoot, "Implementations");
        var migrationsDir = Path.Combine(_tempRoot, "Migrations");

        var exitCode = InvokeBaseline(
            "baseline",
            "Person",
            "--dbpath", _tempRoot,
            "--contracts-dir", contractsDir,
            "--storage-dir", storageDir,
            "--migration-dir", migrationsDir,
            "--existing", "force",
            "--increment", "auto");

        Assert.Equal(0, exitCode);

        var contractsFolder = Path.Combine(contractsDir, "Person", "v1_0_0");
        Assert.True(File.Exists(Path.Combine(contractsFolder, "IPerson.cs")));

        var implementationsFolder = Path.Combine(storageDir, "Person", "v1_0_0");
        Assert.True(Directory.Exists(implementationsFolder));

        var migrationsFolder = Path.Combine(migrationsDir, "Person");
        Assert.True(Directory.Exists(migrationsFolder));
    }

    [Fact]
    public void BaselineCommand_WithExistingVersion_CreatesNextVersionAndMigration()
    {
        var contractsDir = Path.Combine(_tempRoot, "Abstractions");
        var storageDir = Path.Combine(_tempRoot, "Implementations");
        var migrationsDir = Path.Combine(_tempRoot, "Migrations");

        // Initial baseline
        InvokeBaseline(
            "baseline",
            "Person",
            "--dbpath", _tempRoot,
            "--contracts-dir", contractsDir,
            "--storage-dir", storageDir,
            "--migration-dir", migrationsDir,
            "--existing", "force",
            "--increment", "auto");

        // Increment baseline
        var exitCode = InvokeBaseline(
            "baseline",
            "Person",
            "--dbpath", _tempRoot,
            "--contracts-dir", contractsDir,
            "--storage-dir", storageDir,
            "--migration-dir", migrationsDir,
            "--existing", "force",
            "--increment", "auto");

        Assert.Equal(0, exitCode);

        var contractsFolder = Path.Combine(contractsDir, "Person", "v1_0_1");
        Assert.True(Directory.Exists(contractsFolder));

        var implementationsFolder = Path.Combine(storageDir, "Person", "v1_0_1");
        Assert.True(Directory.Exists(implementationsFolder));

        var migrationsFolder = Path.Combine(migrationsDir, "Person");
        Assert.True(Directory.Exists(migrationsFolder));

        var migrationStub = Directory
            .EnumerateFiles(migrationsFolder, "IPerson_v1_0_0_To_v1_0_1_Migration.cs", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        Assert.NotNull(migrationStub);
    }

    [Fact]
    public void BaselineCommand_WithExeRelativePaths_ResolvesAgainstExecutableBase()
    {
        var scopeId = Guid.NewGuid().ToString("N");
        var relativePrefix = Path.Combine("..", $"RelPaths_{scopeId}");
        var contractsArg = Path.Combine(relativePrefix, "Cont");
        var storageArg = Path.Combine(relativePrefix, "Stor");
        var migrationsArg = Path.Combine(relativePrefix, "Mig");

        var expectedBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativePrefix));

        try
        {
            var exitCode = InvokeBaseline(
                "baseline",
                "Person",
                "--dbpath", _tempRoot,
                "--contracts-dir", contractsArg,
                "--storage-dir", storageArg,
                "--migration-dir", migrationsArg,
                "--existing", "force",
                "--increment", "auto");

            Assert.Equal(0, exitCode);

            var expectedContracts = Path.Combine(expectedBase, "Cont", "Person", "v1_0_0");
            Assert.True(File.Exists(Path.Combine(expectedContracts, "IPerson.cs")));

            var expectedStorage = Path.Combine(expectedBase, "Stor", "Person", "v1_0_0");
            Assert.True(Directory.Exists(expectedStorage));

            var expectedMigration = Path.Combine(expectedBase, "Mig", "Person");
            Assert.True(Directory.Exists(expectedMigration));
        }
        finally
        {
            if (Directory.Exists(expectedBase))
            {
                Directory.Delete(expectedBase, recursive: true);
            }
        }
    }

    [Fact]
    public void BaselineCommand_WithOptionsFile_UsesFileToDeriveContract()
    {
        var fixture = CreateOptionsFixture("Person");

        var exitCode = InvokeBaseline(
            "baseline",
            "--dbpath", _tempRoot,
            "--config-file-dir", fixture.ConfigFilePath);

        Assert.Equal(0, exitCode);

        Assert.True(Directory.Exists(fixture.ContractsVersionPath));
        Assert.True(Directory.Exists(fixture.ImplementationsVersionPath));
        Assert.True(Directory.Exists(fixture.MigrationsPath));
    }

    private sealed record OptionsFixture(
        string ConfigFilePath,
        string ContractsVersionPath,
        string ImplementationsVersionPath,
        string MigrationsPath);

    private OptionsFixture CreateOptionsFixture(string contractName, string version = "1.0.0")
    {
        var optionsRoot = Path.Combine(_tempRoot, "Options");
        Directory.CreateDirectory(optionsRoot);

        var versionValue = new Version(version);
        var versionSegment = $"v{versionValue.Major}_{versionValue.Minor}_{Math.Max(versionValue.Build, 0)}";

        var contractsRoot = Path.Combine(_tempRoot, "Abstractions");
        var storageRoot = Path.Combine(_tempRoot, "Implementations");
        var migrationsRoot = Path.Combine(_tempRoot, "Migrations");

        var contractVersionPath = Path.Combine(contractsRoot, contractName, versionSegment);
        var implementationVersionPath = Path.Combine(storageRoot, contractName, versionSegment);
        var migrationsContractPath = Path.Combine(migrationsRoot, contractName);

        var optionsFile = Path.Combine(optionsRoot, $"{contractName}Options.cs");
        var optionLines = new[]
        {
            "using System;",
            "using Idb.Libraries.Abstraction.Enums;",
            "using Idb.Libraries.Abstractions.Options;",
            "using Idb.Libraries.Abstractions.Services.Indexes;",
            string.Empty,
            "namespace Test.Options;",
            string.Empty,
            $"public sealed class {contractName}Options : IDbContractOptions",
            "{",
            $"    public string Key {{ get; }} = \"{contractName}\";",
            $"    public string? ImplementationsPath {{ get; }} = {ToVerbatimLiteral(implementationVersionPath)};",
            $"    public string? ContractPath {{ get; }} = {ToVerbatimLiteral(contractVersionPath)};",
            $"    public string? MigrationsPath {{ get; }} = {ToVerbatimLiteral(migrationsContractPath)};",
            $"    public string? ActiveVersion {{ get; }} = \"{version}\";",
            "    public IRepositoryIndexConfiguration[]? Indexes { get; } = Array.Empty<IRepositoryIndexConfiguration>();",
            "    public ContractIndexProvisioningModeType IndexProvisioningMode { get; } = ContractIndexProvisioningModeType.Dynamic;",
            "    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;",
            "    public ContractObjectWriterType ObjectWriter { get; } = ContractObjectWriterType.None;",
            "    public ContractObjectReaderType ObjectReader { get; } = ContractObjectReaderType.None;",
            "}",
            string.Empty
        };
        File.WriteAllLines(optionsFile, optionLines);

        return new OptionsFixture(optionsFile, contractVersionPath, implementationVersionPath, migrationsContractPath);
    }

    private static int InvokeBaseline(params string[] args)
    {
        var assembly = Assembly.Load("MigrationTool");
        var programType = assembly.GetType("MigrationTool.Program", throwOnError: true);
        var method = programType!.GetMethod("HandleBaselineCommand", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Failed to locate HandleBaselineCommand method.");

        using var writer = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(writer);
        try
        {
            var result = method.Invoke(null, new object[] { args });
            return result is int exitCode ? exitCode : -1;
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // ignore cleanup issues
        }
    }

    private static string ToVerbatimLiteral(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return "@\"" + fullPath.Replace("\"", "\"\"") + "\"";
    }
}
