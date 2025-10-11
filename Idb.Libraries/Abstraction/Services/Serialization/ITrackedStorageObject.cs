using System;

namespace Idb.Libraries.Abstractions.Services.Serialization;

public interface ITrackedStorageObject
{
    /// <summary>
    /// Provides access to the envelope that was used when this instance was materialized.
    /// Implementations should treat this as infrastructure state and allow the repository to update it.
    /// </summary>
    IObjectEnvelope? Envelope { get; set; }
}
