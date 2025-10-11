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
        var exitCode = InvokeBaseline(
            "baseline",
            "Person",
            "--dbpath", _tempRoot,
            "--contracts-dir", Path.Combine(_tempRoot, "Abstractions"),
            "--storage-dir", Path.Combine(_tempRoot, "Implementations"),
            "--migration-dir", Path.Combine(_tempRoot, "Migrations"),
            "--existing", "force",
            "--increment", "auto");

        Assert.Equal(0, exitCode);

        var contractsFolder = Path.Combine(_tempRoot, "Abstractions", "Person", "v1_0_0");
        Assert.True(File.Exists(Path.Combine(contractsFolder, "IPerson.cs")));

        var implementationsFolder = Path.Combine(_tempRoot, "Implementations", "Person", "v1_0_0");
        Assert.True(Directory.Exists(implementationsFolder));

        var migrationsFolder = Path.Combine(_tempRoot, "Migrations", "Person");
        Assert.True(Directory.Exists(migrationsFolder));
    }

    [Fact]
    public void BaselineCommand_WithExistingVersion_CreatesNextVersionAndMigration()
    {
        // Initial baseline
        InvokeBaseline(
            "baseline",
            "Person",
            "--dbpath", _tempRoot,
            "--contracts-dir", Path.Combine(_tempRoot, "Abstractions"),
            "--storage-dir", Path.Combine(_tempRoot, "Implementations"),
            "--migration-dir", Path.Combine(_tempRoot, "Migrations"),
            "--existing", "force",
            "--increment", "auto");

        // Increment baseline
        var exitCode = InvokeBaseline(
            "baseline",
            "Person",
            "--dbpath", _tempRoot,
            "--contracts-dir", Path.Combine(_tempRoot, "Abstractions"),
            "--storage-dir", Path.Combine(_tempRoot, "Implementations"),
            "--migration-dir", Path.Combine(_tempRoot, "Migrations"),
            "--existing", "force",
            "--increment", "auto");

        Assert.Equal(0, exitCode);

        var contractsFolder = Path.Combine(_tempRoot, "Abstractions", "Person", "v1_0_1");
        Assert.True(Directory.Exists(contractsFolder));

        var implementationsFolder = Path.Combine(_tempRoot, "Implementations", "Person", "v1_0_1");
        Assert.True(Directory.Exists(implementationsFolder));

        var migrationsFolder = Path.Combine(_tempRoot, "Migrations", "Person");
        Assert.True(Directory.Exists(migrationsFolder));

        var migrationStub = Directory
            .EnumerateFiles(migrationsFolder, "IPerson_v1_0_0_To_v1_0_1_Migration.cs", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        Assert.NotNull(migrationStub);
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
}
