namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// Storage-level change types that can affect an envelope payload.
/// </summary>
public enum EnvelopeStorageChangeType
{
    Created = 0,
    Changed = 1,
    Deleted = 2,
    Renamed = 3,
    Error = 4
}
