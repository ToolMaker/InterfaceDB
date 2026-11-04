namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>Verifies exclusive-handle enforcement in an existing storage directory.
/// A successful check is required before relying on exclusive opens to reclaim abandoned leases.</summary>
public interface IFileLeaseValidation
{
    void RequireExclusiveHandles(string directory);
}
