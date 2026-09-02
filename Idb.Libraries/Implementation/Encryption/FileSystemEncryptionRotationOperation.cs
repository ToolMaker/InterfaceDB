using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;

namespace Idb.Libraries.Implementation.Encryption;

/// <summary>
/// Re-encrypts all file payloads directly to the active key under an exclusive storage scope.
/// </summary>
public sealed class FileSystemEncryptionRotationOperation<TContract> :
    IEncryptionRotationOperation<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly IDbContractConfigDto<TContract> _contractConfig;
    private readonly IEncryptor _encryptor;
    private readonly IFileWriteCoordinator _fileWriteCoordinator;

    public FileSystemEncryptionRotationOperation(
        IDbContractConfigDto<TContract> contractConfig,
        IEncryptor encryptor,
        IFileWriteCoordinator fileWriteCoordinator)
    {
        _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
        _encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
        _fileWriteCoordinator = fileWriteCoordinator ?? throw new ArgumentNullException(nameof(fileWriteCoordinator));
    }

    public async ValueTask<EncryptionRotationResult> RotateAllAsync(
        CancellationToken cancellationToken = default)
    {
        if (_encryptor is not IRotatingEncryptor rotatingEncryptor)
        {
            throw new NotSupportedException(
                "The configured encryptor does not support historical-key rotation.");
        }

        if (string.IsNullOrWhiteSpace(_contractConfig.DataRoot))
        {
            throw new InvalidOperationException("Contract configuration must supply a DataRoot directory.");
        }

        var storageRoot = Path.GetFullPath(_contractConfig.DataRoot);
        if (!Directory.Exists(storageRoot))
        {
            return new EncryptionRotationResult(0, 0, 0);
        }

        var extension = _contractConfig.FileExtension.NormalizeExtension();
        await using var rotationScope = await _fileWriteCoordinator
            .AcquireMigrationScopeAsync(cancellationToken)
            .ConfigureAwait(false);
        await _fileWriteCoordinator
            .DrainPendingWritesAsync(storageRoot, extension, cancellationToken)
            .ConfigureAwait(false);

        var files = Directory
            .EnumerateFiles(storageRoot, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(
                Path.GetExtension(path),
                extension,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var reencrypted = 0;
        var unchanged = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            var decrypted = rotatingEncryptor.DecryptWithMetadata(original);
            if (decrypted.UsesCurrentEncryption)
            {
                unchanged++;
                continue;
            }

            var replacement = _encryptor.Encrypt(decrypted.Plaintext);
            if (await _fileWriteCoordinator.ReplaceIfUnchangedAsync(
                file,
                original,
                replacement,
                cancellationToken).ConfigureAwait(false))
            {
                reencrypted++;
            }
            else
            {
                unchanged++;
            }
        }

        return new EncryptionRotationResult(files.Length, reencrypted, unchanged);
    }
}
