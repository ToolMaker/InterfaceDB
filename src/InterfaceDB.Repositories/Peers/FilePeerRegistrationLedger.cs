namespace Idb.Commercial.Repositories.Peers;

/// <summary>Durable, account-bound joining boundaries. Owns an exclusive directory; contains no record payloads.</summary>
public sealed class FilePeerRegistrationLedger(string directory, int maximumRegistrations = 10_000) : IPeerRegistrationLedger
{
    private readonly PeerStateDirectory _directory = new(directory);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, PeerRegistrationState> _entries = [];
    private string? _scope;
    public async ValueTask InitializeAsync(string scope, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_scope is not null) { if (_scope != scope) throw new InvalidOperationException("Registration ledger belongs to another dataset."); return; }
            if (maximumRegistrations is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(maximumRegistrations));
            await _directory.InitializeAsync(scope, cancellationToken).ConfigureAwait(false);
            foreach (var file in _directory.Files("*.registration"))
            {
                var state = await _directory.ReadAsync<PeerRegistrationState>(file, 8192, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Missing registration.");
                state.Options.Validate();
                if (state.JoinedThrough < 0 || string.IsNullOrWhiteSpace(state.Principal)
                    || Name(state.Options.RegistrationId) != file || !_entries.TryAdd(state.Options.RegistrationId, state)
                    || _entries.Count > maximumRegistrations) throw new InvalidDataException("Invalid registration ledger.");
            }
            _scope = scope;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask<PeerRegistrationState> GetOrCreateAsync(PeerRegistrationOptions options, string principal,
        long joinedThrough, CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (joinedThrough < 0 || string.IsNullOrWhiteSpace(principal)) throw new ArgumentException("Invalid registration binding.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_scope is null) throw new InvalidOperationException("Initialize registrations first.");
            if (_entries.TryGetValue(options.RegistrationId, out var existing))
            {
                if (existing.Principal != principal) throw new PeerProtocolException(PeerError.Unauthorized);
                if (existing.Options != options) throw new PeerProtocolException(PeerError.InvalidRequest);
                return existing;
            }
            if (_entries.Count >= maximumRegistrations) throw new PeerProtocolException(PeerError.LimitExceeded);
            var state = new PeerRegistrationState(options, principal, joinedThrough, options.Join == PeerJoinMode.FromNow);
            await _directory.WriteAsync(Name(options.RegistrationId), state, cancellationToken).ConfigureAwait(false);
            _entries.Add(options.RegistrationId, state); return state;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask MarkInitialCompleteAsync(Guid registrationId, string principal, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _entries[registrationId];
            if (state.Principal != principal) throw new PeerProtocolException(PeerError.Unauthorized);
            if (state.InitialComplete) return;
            state = state with { InitialComplete = true };
            await _directory.WriteAsync(Name(registrationId), state, cancellationToken).ConfigureAwait(false);
            _entries[registrationId] = state;
        }
        finally { _gate.Release(); }
    }
    private static string Name(Guid id) => id.ToString("N") + ".registration";
    public async ValueTask DisposeAsync()
    { await _gate.WaitAsync().ConfigureAwait(false); try { await _directory.DisposeAsync().ConfigureAwait(false); } finally { _gate.Release(); } }
}
