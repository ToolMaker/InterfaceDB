using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Encryption;

public interface IEncryptionRotationOperation<TContract>
    where TContract : class, IStoredContract<TContract>
{
    ValueTask<EncryptionRotationResult> RotateAllAsync(
        CancellationToken cancellationToken = default);
}

public sealed record EncryptionRotationResult(
    int ScannedPayloads,
    int ReencryptedPayloads,
    int UnchangedPayloads);
