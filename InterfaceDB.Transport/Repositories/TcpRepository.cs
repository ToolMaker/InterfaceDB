using Idb.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Idb.Authentication;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Indexes;
using Idb.Libraries.Implementation.Repositories;
using Idb.Libraries.Implementation.Serialization;

namespace Idb.Transport.Repositories;

/// <summary>
/// An online repository bound to one signed-in principal, with isolated or explicitly shared server data. Each API operation obtains an exclusive
/// server lease, loads a bounded fresh snapshot, uses the existing InterfaceDB upper layer,
/// and closes the session. Independent clients serialize operations, never share stale indexes,
/// and retain exact revision preconditions on entities returned by earlier operations.
/// </summary>
public sealed partial class TcpRepository<TContract> : IEnvelopeMetadataRepository<TContract>, Idb.Libraries.Repositories.IRepositoryChangeNotifications, Idb.Libraries.Repositories.IRepositoryCacheIdentity, IAsyncDisposable
    where TContract : class, IStoredContract<TContract>
{
    private readonly TcpRepositoryOptions<TContract> _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IAccessTokenProvider? _tokens;
    private readonly IObjectSerializer<TContract> _serializer;
    private int _disposed;
    private string? _scopeIdentity;
    private AccountIdentity? _accountIdentity;

    public TcpRepository(TcpRepositoryOptions<TContract> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        _serializer = options.ResolveSerializer();
        _tokens = options.Connection.AuthenticationMode == TcpStoreAuthenticationMode.Provider ? null : new BoundTokenProvider(options.Connection.TokenProvider!);
        _accountIdentity = options.Connection.ExpectedAccountIdentity;
        _scopeIdentity = options.Connection.ExpectedScopeIdentity;
    }

    public ContractRepositoryType RepositoryType => ContractRepositoryType.Remote;
    public string RunningVersion => _options.Contract.ActiveVersion!;

    private async ValueTask<TcpEncodedEnvelopeStore> ConnectAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.AdmissionTimeout);
        await EnsureScopeAsync(deadline.Token).ConfigureAwait(false);
        var connection = _options.Connection with
        {
            TokenProvider = _tokens,
            ExpectedAccountIdentity = Volatile.Read(ref _accountIdentity),
            Telemetry = _options.Connection.Telemetry ?? _options.StoragePolicy.Telemetry,
            // Preserve the requested capabilities so a read-only principal can open a typed repository.
            RequiredCapabilities = _options.Connection.RequiredCapabilities,
            ExpectedScopeIdentity = Volatile.Read(ref _scopeIdentity) ?? _options.Connection.ExpectedScopeIdentity
        };
        var rejectedAttempts = 0;
        while (true)
        {
            try
            {
                var store = await TcpEncodedEnvelopeStore.ConnectAsync(connection, deadline.Token).ConfigureAwait(false);
                try { BindAccount(store.AccountIdentity); BindScope(store.ScopeIdentity); return store; }
                catch { await store.DisposeAsync().ConfigureAwait(false); throw; }
            }
            catch (RemoteStoreException exception) when (exception.Code == RemoteErrorCode.Busy)
            {
                // Only rejected session admission is retried. No operation has been dispatched.
                var delay = _options.AdmissionRetryPolicy.GetDelay(++rejectedAttempts, _options.BusyRetryDelay);
                TransportValidation.Duration(delay, nameof(ITcpAdmissionRetryPolicy));
                await Task.Delay(delay, deadline.Token).ConfigureAwait(false);
            }
        }
    }

    private void BindScope(string identity)
    {
        if (Interlocked.CompareExchange(ref _scopeIdentity, identity, null) is { } prior && prior != identity)
            throw new UnauthorizedAccessException("The server data scope changed. Create a new repository and cache for the newly approved scope.");
    }

    private void BindAccount(AccountIdentity? identity)
    {
        if (_options.Connection.AuthenticationMode != TcpStoreAuthenticationMode.Provider) return;
        if (identity is null) throw new UnauthorizedAccessException("A server-authenticated account is required.");
        if (Interlocked.CompareExchange(ref _accountIdentity, identity, null) is { } prior && prior != identity)
            throw new UnauthorizedAccessException("The repository belongs to another account. Create a new repository after switching accounts.");
    }

    private ValueTask<TResult> WithRepositoryAsync<TResult>(
        Func<InterfaceRepositoryDb<TContract>, ValueTask<TResult>> operation, CancellationToken cancellationToken,
        Func<TcpEncodedEnvelopeStore, CancellationToken, ValueTask<IEncodedEnvelopeStore>>? prepare = null)
        => !DatabaseTelemetryOperation.IsEnabled((_options.Connection.Telemetry ?? _options.StoragePolicy.Telemetry)) ? WithRepositoryAsyncUnobserved<TResult>(operation, cancellationToken, prepare)
            : WithRepositoryAsyncObserved<TResult>(operation, cancellationToken, prepare);

    private ValueTask<TResult> WithRepositoryAsyncObserved<TResult>(
        Func<InterfaceRepositoryDb<TContract>, ValueTask<TResult>> operation, CancellationToken cancellationToken,
        Func<TcpEncodedEnvelopeStore, CancellationToken, ValueTask<IEncodedEnvelopeStore>>? prepare = null)
        => TransportTelemetry.ObserveAsync(_options.Connection.Telemetry ?? _options.StoragePolicy.Telemetry, DatabaseComponent.TcpRepository, DatabaseOperation.Execute,
                () => WithRepositoryAsyncUnobserved<TResult>(operation, cancellationToken, prepare), log: true, bytes: 0);

    private async ValueTask<TResult> WithRepositoryAsyncUnobserved<TResult>(
        Func<InterfaceRepositoryDb<TContract>, ValueTask<TResult>> operation, CancellationToken cancellationToken,
        Func<TcpEncodedEnvelopeStore, CancellationToken, ValueTask<IEncodedEnvelopeStore>>? prepare = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!await _gate.WaitAsync(_options.AdmissionTimeout, cancellationToken).ConfigureAwait(false))
            throw new TimeoutException("Timed out waiting for another local repository operation.");
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await using var session = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            var source = prepare is null ? session : await prepare(session, cancellationToken).ConfigureAwait(false);
            var operationOptions = _options with { Connection = _options.Connection with { Limits = session.Limits } };
            operationOptions.Validate();
            var policy = _options.StoragePolicy with
            {
                Telemetry = _options.StoragePolicy.Telemetry ?? _options.Connection.Telemetry,
                RaiseNotifications = false, WatchStorageChanges = false, HydrateOnInitialize = true,
                DurabilityMode = StorageDurabilityMode.Durable
            };
            var identity = _options.StorageIdentityFactory ?? new RemoteIdentity(_options.Contract);
            var lifecycle = new EnvelopeLifecycle<TContract>(() => new FileObjectEnvelope<TContract>(_options.Contract)
            {
                EnvelopeCacheKey = string.Empty, VersionTag = _options.Contract.ActiveVersion!
            }, identity, _options.Encryptor);
            var envelopes = new RemoteEnvelopeRepository<TContract>(operationOptions, source, _serializer, lifecycle, identity, ReadFreshPhysicalAsync);
            IIndexManager<TContract> indexes = SearchIndexMetadata.HasIndexes(typeof(TContract))
                ? new SearchIndexManager<TContract>(envelopes, policy) : new PassthroughIndexManager<TContract>(envelopes);
            using var repository = new InterfaceRepositoryDb<TContract>(indexes, _options.Contract, policy,
                lifecycle, envelopes, StorageDurabilityMode.Durable);
            try
            {
                await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
                return await operation(repository).ConfigureAwait(false);
            }
            finally
            {
                if (indexes is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else if (indexes is IDisposable disposable) disposable.Dispose();
            }
        }
        finally { _gate.Release(); }
    }

    private ValueTask<TContract?> ReadFreshPhysicalAsync(string key, CancellationToken token)
        => WithRepositoryAsync(async repository =>
        {
            await foreach (var entity in repository.QueryManyByStorageKeyAsync(key, token).ConfigureAwait(false))
                if (entity.Envelope?.EnvelopeCacheKey == key) return entity;
            return null;
        }, token);

    private async IAsyncEnumerable<T> StreamAsync<T>(
        Func<InterfaceRepositoryDb<TContract>, IAsyncEnumerable<T>> operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Release the store before handing control to an arbitrary application/UI enumerator.
        var snapshot = await WithRepositoryAsync(async repository =>
        {
            var result = new List<T>();
            await foreach (var item in operation(repository).WithCancellation(cancellationToken).ConfigureAwait(false))
                result.Add(item);
            return result;
        }, cancellationToken).ConfigureAwait(false);
        foreach (var item in snapshot)
        { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        => _ = await WithRepositoryAsync(_ => ValueTask.FromResult(true), cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var subscriptions = _changeSubscriptions.Values.ToArray();
        foreach (var subscription in subscriptions) subscription.Dispose();
        foreach (var subscription in subscriptions)
            try { await subscription.Drained.ConfigureAwait(false); } catch (Exception) { /* Reported by subscription.Completion. */ }
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
    }

    private sealed class RemoteIdentity(IDbContractConfigDto<TContract> config) : IEnvelopeStorageIdentityFactory<TContract>
    {
        public StorageObjectCardinality ObjectCardinality => config.ObjectCardinality;
        public void InitEnvelope(IObjectEnvelope<TContract> envelope)
        {
            if (string.IsNullOrWhiteSpace(envelope.EnvelopeCacheKey))
                envelope.EnvelopeCacheKey = ObjectCardinality == StorageObjectCardinality.Single
                    ? config.ContractName : Guid.CreateVersion7().ToString("N");
            envelope.Location = envelope.EnvelopeCacheKey;
        }
    }

    private sealed class BoundTokenProvider(IAccessTokenProvider inner) : IAccessTokenProvider
    {
        private (string Issuer, string Subject)? _identity;
        private readonly object _identityGate = new();
        internal (string Issuer, string Subject) Identity => _identity ?? throw new InvalidOperationException("No token identity has been read.");
        public async ValueTask<AccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            var token = await inner.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            // This is an account-switch guard only. The server independently validates the JWT.
            // Never authorize anything based on this unverified client-side parse.
            try
            {
                if (token.Value.Length > TransportValidation.MaximumAccessTokenBytes) throw new InvalidDataException();
                var parts = token.Value.Split('.');
                if (parts.Length != 3) throw new InvalidDataException();
                var encoded = parts[1].Replace('-', '+').Replace('_', '/');
                encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
                using var payload = JsonDocument.Parse(Convert.FromBase64String(encoded));
                string Claim(string name)
                {
                    var claims = payload.RootElement.EnumerateObject().Where(property => property.Name == name).ToArray();
                    return claims.Length == 1 && claims[0].Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(claims[0].Value.GetString()) ? claims[0].Value.GetString()! : throw new InvalidDataException();
                }
                var identity = (Claim("iss"), Claim("sub"));
                lock (_identityGate)
                {
                    if (_identity is { } prior && prior != identity)
                        throw new InvalidOperationException("The repository belongs to another signed-in account. Create a new repository after switching accounts.");
                    _identity ??= identity;
                }
                return token;
            }
            catch (Exception exception) when (exception is JsonException or FormatException or InvalidDataException)
            {
                throw new InvalidOperationException("A bounded JWT with issuer and subject claims is required.");
            }
        }
    }
}
