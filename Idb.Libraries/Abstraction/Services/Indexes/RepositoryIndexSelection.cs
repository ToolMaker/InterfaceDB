namespace Idb.Libraries.Abstractions.Services.Indexes;

/// <summary>
/// Represents the outcome of an index selection request.
/// </summary>
/// <param name="Index">The selected index.</param>
/// <param name="Key">The derived key for the probe, when available.</param>
/// <param name="Score">The relative score assigned to the selection.</param>
/// <param name="MatchedMembers">The count of index members satisfied by the probe.</param>
/// <param name="IsFullMatch">Indicates that the probe satisfied all index members.</param>
/// <param name="UsedPreferredIndex">Indicates that the selection honoured a preferred index request.</param>
public sealed record RepositoryIndexSelection(
    IRepositoryIndex Index,
    object? Key,
    double Score,
    int MatchedMembers,
    bool IsFullMatch,
    bool UsedPreferredIndex);

