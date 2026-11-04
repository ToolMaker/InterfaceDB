using System.Linq.Expressions;
using System.Reflection;

namespace Idb.Commercial.Repositories.Peers;

public enum PeerJoinMode { FromNow, LoadAllHistory, LoadHistoryRange }
public enum PeerResumeMode { None, ResumeAll, ResumeRange }

/// <summary>UTC instants, inclusive start and exclusive end.</summary>
public sealed record PeerHistoryRange(DateTimeOffset Start, DateTimeOffset End)
{
    public void Validate()
    {
        if (Start >= End) throw new ArgumentException("A history range needs start before end.");
    }
    public bool Contains(DateTimeOffset? date) => date is { } value && value >= Start && value < End;
}

/// <summary>Client preferences, submitted during authenticated registration. Preserve RegistrationId across restarts.</summary>
public sealed record PeerRegistrationOptions
{
    public required Guid RegistrationId { get; init; }
    public PeerJoinMode Join { get; init; } = PeerJoinMode.LoadAllHistory;
    public PeerResumeMode Resume { get; init; } = PeerResumeMode.ResumeAll;
    public string? DateProperty { get; init; }
    public PeerHistoryRange? HistoryRange { get; init; }
    public PeerHistoryRange? ResumeRange { get; init; }
    public TimeSpan? ResumeWindow { get; init; }
    public void Validate()
    {
        if (RegistrationId == Guid.Empty || !Enum.IsDefined(Join) || !Enum.IsDefined(Resume))
            throw new ArgumentException("A stable registration ID and valid modes are required.");
        if (DateProperty is not null && (string.IsNullOrWhiteSpace(DateProperty) || DateProperty.Length > 256))
            throw new ArgumentException("Invalid history date property.");
        if ((Join == PeerJoinMode.LoadHistoryRange) != (HistoryRange is not null))
            throw new ArgumentException("Only LoadHistoryRange accepts and requires HistoryRange.");
        if (Resume == PeerResumeMode.ResumeRange
            ? (ResumeRange is null) == (ResumeWindow is null)
            : ResumeRange is not null || ResumeWindow is not null)
            throw new ArgumentException("ResumeRange requires either fixed dates or a rolling window.");
        if ((Join == PeerJoinMode.LoadHistoryRange || Resume == PeerResumeMode.ResumeRange) && DateProperty is null)
            throw new ArgumentException("Range modes require a date property on the storage interface.");
        HistoryRange?.Validate(); ResumeRange?.Validate();
        if (ResumeWindow is { } window && (window <= TimeSpan.Zero || window > TimeSpan.FromDays(36500)))
            throw new ArgumentOutOfRangeException(nameof(ResumeWindow));
    }
}

/// <summary>Server-owned restrictions, evaluated again before each transfer. Dates are preferences, not access credentials.</summary>
public sealed record PeerHistoryPolicy
{
    public bool AllowHistory { get; init; } = true;
    public bool AllowResume { get; init; } = true;
    public long MinimumCreatedSequence { get; init; }
    public bool Allows(PeerRegistrationOptions? request) => MinimumCreatedSequence >= 0
        && (AllowHistory || request?.Join == PeerJoinMode.FromNow)
        && (AllowResume || request?.Resume == PeerResumeMode.None);
    public bool Allows(PeerRecordDescriptor record) => MinimumCreatedSequence == 0
        || MinimumCreatedSequence > 0 && record.CreatedSequence >= MinimumCreatedSequence;
}

public sealed record PeerRegistrationState(PeerRegistrationOptions Options, string Principal, long JoinedThrough, bool InitialComplete);

public interface IPeerRegistrationLedger : IAsyncDisposable
{
    ValueTask InitializeAsync(string scope, CancellationToken cancellationToken = default);
    ValueTask<PeerRegistrationState> GetOrCreateAsync(PeerRegistrationOptions options, string principal, long joinedThrough,
        CancellationToken cancellationToken = default);
    ValueTask MarkInitialCompleteAsync(Guid registrationId, string principal, CancellationToken cancellationToken = default);
}

public interface IPeerHistoryDateAccessor<T>
{
    string PropertyName { get; }
    DateTimeOffset Get(T entity);
}

/// <summary>Selects one readable DateTimeOffset property, including inherited interface properties.</summary>
public sealed class PeerHistoryDateAccessor<T> : IPeerHistoryDateAccessor<T>
{
    private readonly Func<T, DateTimeOffset> _read;
    public string PropertyName { get; }
    public PeerHistoryDateAccessor(Expression<Func<T, DateTimeOffset>> selector)
    {
        if (!typeof(T).IsInterface || selector.Body is not MemberExpression { Member: PropertyInfo property } member
            || member.Expression != selector.Parameters[0] || property.GetMethod is null
            || property.PropertyType != typeof(DateTimeOffset))
            throw new ArgumentException("Select a readable DateTimeOffset property directly on the storage interface.", nameof(selector));
        PropertyName = property.Name; _read = selector.Compile();
    }
    public DateTimeOffset Get(T entity)
    {
        var value = _read(entity).ToUniversalTime();
        if (value == DateTimeOffset.MinValue || value == DateTimeOffset.MaxValue)
            throw new ArgumentException("Assign the history date before saving.");
        return value;
    }
}

/// <summary>Pure selection strategy. No network, storage, or serializer dependencies.</summary>
public interface IPeerHistorySelector
{
    bool Includes(PeerRegistrationState? registration, long connectedThrough, PeerRecordDescriptor record,
        bool retainedLocally, DateTimeOffset now, bool requiredForPendingWrite = false);
}

public sealed class PeerHistorySelector : IPeerHistorySelector
{
    public bool Includes(PeerRegistrationState? registration, long connectedThrough, PeerRecordDescriptor record,
        bool retainedLocally, DateTimeOffset now, bool requiredForPendingWrite = false)
    {
        if (registration is null) return true; // Existing full-replica registration.
        var options = registration.Options;
        var created = record.CreatedSequence == 0 ? record.Sequence : record.CreatedSequence;
        var initial = created <= registration.JoinedThrough && (options.Join == PeerJoinMode.LoadAllHistory
            || options.Join == PeerJoinMode.LoadHistoryRange && options.HistoryRange!.Contains(record.HistoryDateUtc));
        if (!initial && created <= registration.JoinedThrough) return false;
        // Deletion invalidates any retained copy even when its date has left the resume window.
        if (record.Deleted && retainedLocally) return true;
        // New local inserts can collide without a retained committed copy. Repair may bypass
        // resume preferences, but never the joining-scope check above or server authorization.
        if (requiredForPendingWrite) return true;
        if (record.Sequence > connectedThrough || !registration.InitialComplete && initial) return true;
        return options.Resume switch
        {
            PeerResumeMode.ResumeAll => true,
            PeerResumeMode.ResumeRange => options.ResumeRange?.Contains(record.HistoryDateUtc)
                ?? (record.HistoryDateUtc is { } date && date >= now - options.ResumeWindow!.Value && date <= now),
            _ => false
        };
    }
}
