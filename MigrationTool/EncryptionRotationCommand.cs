using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Encryption.Options;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Serialization;

namespace MigrationTool;

internal static class EncryptionRotationCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2 || !string.Equals(args[1], "rotate", StringComparison.OrdinalIgnoreCase))
        {
            PrintUsage();
            return 1;
        }

        if (!TryGetRequiredOption(args, "--database", out var databaseName)
            || !TryGetRequiredOption(args, "--data-root", out var dataRoot))
        {
            Console.WriteLine("Both '--database' and '--data-root' are required.");
            PrintUsage();
            return 1;
        }

        var extension = GetOption(args, "--extension") ?? ".mpk";
        extension = extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
        var legacyKeyId = GetOption(args, "--legacy-key-id");
        var fullDataRoot = Path.GetFullPath(dataRoot);

        try
        {
            var config = new MaintenanceContractConfig(
                databaseName,
                fullDataRoot,
                extension);
            var storagePolicy = config.StoragePolicy ?? StoragePolicy.Default;
            var keyStore = new WindowsCredentialEncryptionKeyStore(databaseName);
            var encryptor = new AesGcmEncryptor(
                keyStore,
                new AesGcmEncryptorOptions
                {
                    LegacyKeyId = legacyKeyId,
                    RotationMode = EncryptionRotationMode.ReadHistoricalOnly
                });
            var writeLock = new SidecarFileWriteLock<MaintenanceContract>(storagePolicy);
            var coordinator = new CoalescingFileWriteCoordinator<MaintenanceContract>(
                writeLock,
                config,
                storagePolicy);
            var operation = new FileSystemEncryptionRotationOperation<MaintenanceContract>(
                config,
                encryptor,
                coordinator);

            Console.WriteLine($"Rotating encrypted payloads under '{fullDataRoot}' to key '{keyStore.GetActiveKeyId()}'.");
            var result = await operation.RotateAllAsync().ConfigureAwait(false);
            Console.WriteLine($"Scanned: {result.ScannedPayloads}");
            Console.WriteLine($"Re-encrypted: {result.ReencryptedPayloads}");
            Console.WriteLine($"Already current: {result.UnchangedPayloads}");
            Console.WriteLine("Do not remove historical keys until every relevant storage root reports no re-encrypted payloads on a subsequent run.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Encryption rotation failed: {ex.Message}");
            return 1;
        }
    }

    public static void PrintUsage()
    {
        Console.WriteLine("  encryption rotate --database <name> --data-root <path> [--extension .mpk] [--legacy-key-id <id>]");
        Console.WriteLine("    Re-encrypts all matching payloads directly to the active Windows Credential Manager key.");
        Console.WriteLine("    --legacy-key-id is required only when version 1 IDBE payloads may exist.");
    }

    private static bool TryGetRequiredOption(
        IReadOnlyList<string> args,
        string option,
        out string value)
    {
        value = GetOption(args, option) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? GetOption(IReadOnlyList<string> args, string option)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return index + 1 < args.Count ? args[index + 1] : null;
        }

        return null;
    }

    private sealed class MaintenanceContract : IStoredContract<MaintenanceContract>
    {
        public IObjectEnvelope<MaintenanceContract>? Envelope => null;
    }

    private sealed class MaintenanceContractConfig : IDbContractConfigDto<MaintenanceContract>
    {
        public MaintenanceContractConfig(string databaseName, string dataRoot, string fileExtension)
        {
            EnvelopeDICacheKey = databaseName;
            ContractName = databaseName;
            DataRoot = dataRoot;
            FileExtension = fileExtension;
        }

        public string EnvelopeDICacheKey { get; }
        public string ContractName { get; }
        public string FileExtension { get; }
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot { get; }
        public string? ActiveVersion => null;
        public ContractRepositoryType RepositoryType => ContractRepositoryType.FileSystem;
        public StoragePolicy? StoragePolicy => Idb.Libraries.Abstractions.Options.StoragePolicy.Default;
    }
}
