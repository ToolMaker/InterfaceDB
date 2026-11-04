using Idb.Libraries.Repositories;
using System.Net.Sockets;
using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Commercial.Repositories;

/// <summary>
/// Constant policy suitable for the common one-policy-per-repository configuration.
/// </summary>
public sealed class ResilientRepositoryOptions<TContract> : IResilientRepositoryPolicy<TContract>
    where TContract : class, IStoredContract<TContract>
{
    /// <summary>
    /// Enables local/remote composition. LocalFirst durably queues local writes; RemoteFirst
    /// requires remote acknowledgement. Null retains the legacy preferred/peer policy.
    /// </summary>
    public RepositoryReadPreference? ReadPreference { get; set; }

    public Idb.Diagnostics.IDatabaseTelemetry? Telemetry { get; set; }

    /// <summary>Stable dataset/account identity for this dedicated cache. Use a separate local store per scope.</summary>
    public string? CacheScope { get; set; }
    /// <summary>Optional durable catch-up cursor. Without it, restarting performs a complete snapshot.</summary>
    public IRepositoryCursorStore? CursorStore { get; set; }

    /// <summary>Optional application session/access guard, run even for local cache hits.</summary>
    public Func<CancellationToken, ValueTask>? ValidateAccessAsync { get; set; }

    /// <summary>Stable identifier used for invalidation routing and synchronization state.</summary>
    public string DatasetId { get; set; } = typeof(TContract).FullName ?? typeof(TContract).Name;

    /// <summary>
    /// Gets or sets when reads inspect the peer for a later revision.
    /// </summary>
    public RevisionCheckMode RevisionCheckMode { get; set; } = RevisionCheckMode.PreferredOnly;

    /// <summary>
    /// Gets or sets which endpoint may originate mutations through this repository.
    /// </summary>
    public MutationAuthority MutationAuthority { get; set; } = MutationAuthority.PreferredWritable;

    /// <summary>
    /// Gets or sets whether a peer fallback value is copied durably into the preferred endpoint.
    /// </summary>
    public bool RestorePreferredOnFallback { get; set; } = true;

    /// <summary>
    /// Gets or sets whether initialization starts the bounded background synchronization worker.
    /// </summary>
    public bool EnableBackgroundSynchronization { get; set; } = true;

    /// <summary>
    /// Gets or sets the delay before retrying retained background work after a transient failure.
    /// </summary>
    public TimeSpan BackgroundRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the maximum number of distinct in-memory pending keys before work is coalesced
    /// into a dataset-wide synchronization request.
    /// </summary>
    public int MaximumPendingSynchronizationKeys { get; set; } = 10_000;

    /// <summary>
    /// Gets or sets the hard per-contract item ceiling for full recovery and broad comparison.
    /// </summary>
    public int MaximumSynchronizationScanItems { get; set; } = 100_000;

    /// <summary>
    /// Gets or sets the classifier for failures that permit offline fallback and retry.
    /// </summary>
    public Func<Exception, bool> IsUnavailableException { get; set; } = DefaultIsUnavailable;

    /// <summary>
    /// Gets or sets the classifier for transient failures expected to affect the whole endpoint.
    /// Endpoint-wide failures stop the current pass; key-specific failures do not.
    /// </summary>
    public Func<Exception, bool> IsEndpointWideFailureException { get; set; } = DefaultIsUnavailable;

    /// <summary>
    /// Gets or sets an optional diagnostics callback. Callback failures do not alter repository behavior.
    /// </summary>
    public Action<ResilientRepositoryFailure>? FailureHandler { get; set; }

    RevisionCheckMode IResilientRepositoryPolicy<TContract>.GetRevisionCheckMode(string? storageKey)
        => RevisionCheckMode;

    MutationAuthority IResilientRepositoryPolicy<TContract>.GetMutationAuthority(string storageKey)
        => MutationAuthority;

    bool IResilientRepositoryPolicy<TContract>.IsEndpointUnavailable(Exception exception)
        => IsUnavailableException(exception);

    bool IResilientRepositoryPolicy<TContract>.IsEndpointWideFailure(Exception exception)
        => IsEndpointWideFailureException(exception);

    void IResilientRepositoryPolicy<TContract>.ReportFailure(ResilientRepositoryFailure failure)
        => FailureHandler?.Invoke(failure);

    internal ResilientRepositoryOptions<TContract> Snapshot()
    {
        if (string.IsNullOrWhiteSpace(DatasetId))
        {
            throw new ArgumentException("A dataset identifier is required.", nameof(DatasetId));
        }

        if (!Enum.IsDefined(RevisionCheckMode))
        {
            throw new ArgumentOutOfRangeException(nameof(RevisionCheckMode));
        }

        if (!Enum.IsDefined(MutationAuthority))
        {
            throw new ArgumentOutOfRangeException(nameof(MutationAuthority));
        }

        if (BackgroundRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(BackgroundRetryDelay));
        }

        if (MaximumPendingSynchronizationKeys <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPendingSynchronizationKeys));
        }

        if (MaximumSynchronizationScanItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumSynchronizationScanItems));
        }

        ArgumentNullException.ThrowIfNull(IsUnavailableException);
        ArgumentNullException.ThrowIfNull(IsEndpointWideFailureException);

        if (ReadPreference is { } preference && !Enum.IsDefined(preference))
            throw new ArgumentOutOfRangeException(nameof(ReadPreference));
        if (ReadPreference is not null && (string.IsNullOrWhiteSpace(CacheScope) || CacheScope.Length > 256))
            throw new ArgumentException("A bounded cache scope identifying the dataset and account is required.", nameof(CacheScope));

        return new ResilientRepositoryOptions<TContract>
        {
            DatasetId = DatasetId.Trim(),
            Telemetry = Telemetry,
            ReadPreference = ReadPreference,
            CacheScope = CacheScope, CursorStore = CursorStore,
            ValidateAccessAsync = ValidateAccessAsync,
            RevisionCheckMode = RevisionCheckMode,
            MutationAuthority = MutationAuthority,
            RestorePreferredOnFallback = RestorePreferredOnFallback,
            EnableBackgroundSynchronization = EnableBackgroundSynchronization,
            BackgroundRetryDelay = BackgroundRetryDelay,
            MaximumPendingSynchronizationKeys = MaximumPendingSynchronizationKeys,
            MaximumSynchronizationScanItems = MaximumSynchronizationScanItems,
            IsUnavailableException = IsUnavailableException,
            IsEndpointWideFailureException = IsEndpointWideFailureException,
            FailureHandler = FailureHandler
        };
    }

    private static bool DefaultIsUnavailable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is ResilientEndpointUnavailableException
            or TimeoutException
            or TaskCanceledException
            or SocketException)
        {
            return true;
        }

        return exception is HttpRequestException httpRequestException
            && (httpRequestException.StatusCode is null
                || httpRequestException.StatusCode == System.Net.HttpStatusCode.RequestTimeout
                || httpRequestException.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                || (int)httpRequestException.StatusCode >= 500);
    }
}
