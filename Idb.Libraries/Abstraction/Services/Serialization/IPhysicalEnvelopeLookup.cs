namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>Optional exact-key membership lookup that does not materialize object payloads.</summary>
public interface IPhysicalEnvelopeLookup
{
    /// <summary>
    /// Checks the provider's current physical-key metadata, initializing it if needed.
    /// Logical group aliases must not be accepted. External changes follow the provider's
    /// normal metadata refresh policy; this is not a payload integrity check.
    /// </summary>
    ValueTask<bool> ExistsPhysicalAsync(string physicalKey, CancellationToken cancellationToken = default);
}
