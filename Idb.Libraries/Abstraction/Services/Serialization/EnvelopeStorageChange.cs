namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// Describes a committed storage-level change observed for an envelope payload.
/// </summary>
public sealed record EnvelopeStorageChange(
    EnvelopeStorageChangeType ChangeType,
    string? OldPath,
    string? NewPath,
    Exception? Error = null);
