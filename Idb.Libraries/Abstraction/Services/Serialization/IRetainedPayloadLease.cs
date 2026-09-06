namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>Provider-neutral ownership of encoded history, separate from logical envelope revisions.</summary>
public interface IRetainedPayloadLease : IDisposable
{
    Guid RecordId { get; }
    string Key { get; }
    IRetainedPayloadUse AcquireUse();
    void Renew(TimeSpan duration);
    ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken = default);
}

public interface IRetainedPayloadUse : IDisposable
{
    bool IsSameIncarnation { get; }
    ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken = default);
}
