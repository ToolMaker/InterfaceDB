namespace Idb.Libraries.Abstractions.Services
{
    using System.IO;
    /// <summary>
    /// Produces unique storage identifiers for contract instances.
    /// </summary>
    /// <typeparam name="TContract">Contract type associated with the value.</typeparam>
    /// <typeparam name="TFileInfo">Descriptor used to locate the persisted payload (e.g., file info).</typeparam>
    public interface IFileNameFactory<TContract, TFileInfo>
    {
        /// <summary>
        /// Creates a storage identifier for the supplied instance.
        /// </summary>
        /// <param name="value">Contract instance being persisted.</param>
        /// <param name="location">Existing location metadata if the instance has already been stored.</param>
        /// <param name="normalizedId">Caller-supplied normalized identifier that can be reused by the factory.</param>
        /// <returns>Location-safe identifier (without extension).</returns>
        string Create(TContract value, TFileInfo? location, string? normalizedId);
    }
}

