using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Commercial.Repositories.StorageMigrations;

/// <summary>The encrypted binding travels with the physical store. An external receipt alone
/// cannot attach to an unrelated store at a reused or changed path.</summary>
public sealed class FileStorageMigrationDestinationBinding(string directory, IEncryptor encryptor,
    IFilePublication publication) : IStorageMigrationDestinationBinding
{
    private FileStorageMigrationStateStore Store(string plan) => new(
        Path.Combine(directory, ".idb-storage-handover.binding"), "destination-binding:" + plan, encryptor, publication);

    public async ValueTask<string> ProposeAsync(string plan, CancellationToken ct)
    {
        var existing = await Store(plan).ReadAsync(ct).ConfigureAwait(false);
        if (existing is null) return Guid.NewGuid().ToString("N");
        if (existing.Plan != plan || !Guid.TryParseExact(existing.DestinationInstance, "N", out _))
            throw new InvalidDataException("Destination belongs to a different migration.");
        return existing.DestinationInstance!;
    }

    public async ValueTask BindAsync(string plan, string instance, CancellationToken ct)
    {
        if (!Guid.TryParseExact(instance, "N", out _)) throw new ArgumentException("Invalid destination instance.", nameof(instance));
        var store = Store(plan);
        await using var lease = await store.AcquireLeaseAsync(ct).ConfigureAwait(false);
        var existing = await store.ReadAsync(ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Plan != plan || existing.DestinationInstance != instance)
                throw new InvalidDataException("Destination belongs to a different migration.");
            return;
        }
        await store.WriteAsync(new(plan, "binding-v2", 0, 0, 0, true) { DestinationInstance = instance }, ct).ConfigureAwait(false);
    }

    public async ValueTask ValidateAsync(string plan, string instance, CancellationToken ct)
    {
        var existing = await Store(plan).ReadAsync(ct).ConfigureAwait(false);
        if (existing?.Plan != plan || existing.DestinationInstance != instance)
            throw new InvalidDataException("Destination identity does not match its migration receipt. Restore the complete store.");
    }
}
