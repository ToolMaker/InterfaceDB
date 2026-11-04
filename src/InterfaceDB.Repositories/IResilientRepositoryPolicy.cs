using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Commercial.Repositories;

/// <summary>
/// Supplies repository-specific synchronization, availability, and diagnostics policy.
/// </summary>
public interface IResilientRepositoryPolicy<TContract>
    where TContract : class, IStoredContract<TContract>
{
    /// <summary>
    /// Stable identifier used to route change notifications and persist synchronization state.
    /// </summary>
    string DatasetId { get; }

    Idb.Diagnostics.IDatabaseTelemetry? Telemetry => null;

    RevisionCheckMode GetRevisionCheckMode(string? storageKey);

    MutationAuthority GetMutationAuthority(string storageKey);

    bool RestorePreferredOnFallback { get; }

    bool EnableBackgroundSynchronization { get; }

    TimeSpan BackgroundRetryDelay { get; }

    int MaximumPendingSynchronizationKeys { get; }

    int MaximumSynchronizationScanItems { get; }

    bool IsEndpointUnavailable(Exception exception);

    /// <summary>
    /// Returns true when a transient failure indicates that further keys are expected to fail
    /// against the same endpoint. Key-scoped transient failures should return false so unrelated
    /// pending keys can continue making progress.
    /// </summary>
    bool IsEndpointWideFailure(Exception exception);

    void ReportFailure(ResilientRepositoryFailure failure);
}
