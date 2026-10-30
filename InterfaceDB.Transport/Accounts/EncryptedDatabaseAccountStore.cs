using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Idb.Authentication;
using Idb.Libraries.Abstractions.Services.Encryption;

namespace Idb.Transport;

/// <summary>
/// Encrypted credential catalogue on a dedicated, exclusively owned durable store. Never register
/// this backing store as a TCP alias. Writes publish the new snapshot only after durable acknowledgement.
/// </summary>
public sealed class EncryptedDatabaseAccountStore : IDatabaseAccountStore
{
    private const string CatalogueKey = "idb-accounts-v1";
    private readonly IEncodedEnvelopeStore _store;
    private readonly IContextualEncryptor _encryptor;
    private readonly byte[] _context;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _maximumBytes;
    private Dictionary<string, DatabaseAccount> _accounts = new(StringComparer.Ordinal);
    private Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private bool _faulted;
    private bool _disposed;
    private EncryptedDatabaseAccountStore(IEncodedEnvelopeStore store, IContextualEncryptor encryptor, string authority, int maximumBytes)
    {
        _store = store; _encryptor = encryptor; _maximumBytes = maximumBytes;
        _context = Encoding.UTF8.GetBytes("InterfaceDB/native-credentials/v1/" + authority);
    }
    public static async ValueTask<EncryptedDatabaseAccountStore> OpenAsync(IEncodedEnvelopeStore store, IContextualEncryptor encryptor,
        string authority, int maximumBytes = 16 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(encryptor);
        if (string.IsNullOrWhiteSpace(authority) || maximumBytes is < 4096 or > 32 * 1024 * 1024
            || (store.Capabilities & (EncodedStoreCapabilities.Read | EncodedStoreCapabilities.Write | EncodedStoreCapabilities.DurableCommits))
                != (EncodedStoreCapabilities.Read | EncodedStoreCapabilities.Write | EncodedStoreCapabilities.DurableCommits))
            throw new ArgumentException("Credential storage requires durable reads/writes, an authority and bounded storage.");
        var result = new EncryptedDatabaseAccountStore(store, encryptor, authority, maximumBytes);
        // Fail startup on missing keys, plaintext/no-op implementations or context-insensitive decryption.
        var probe = RandomNumberGenerator.GetBytes(32);
        var cipher = encryptor.Encrypt(probe, result._context);
        if (cipher.AsSpan().SequenceEqual(probe)) throw new CryptographicException("Credential encryption is required.");
        var decoded = encryptor.Decrypt(cipher, result._context);
        try { if (!decoded.AsSpan().SequenceEqual(probe)) throw new CryptographicException("Credential encryption self-check failed."); }
        finally { CryptographicOperations.ZeroMemory(decoded); }
        bool rejected = false;
        try { var wrong = encryptor.Decrypt(cipher, "different-account-store"u8); CryptographicOperations.ZeroMemory(wrong); }
        catch (CryptographicException) { rejected = true; }
        if (!rejected) throw new CryptographicException("Credential encryption must authenticate its storage context.");
        if (await store.GetAsync(CatalogueKey, cancellationToken).ConfigureAwait(false) is { } stored)
        {
            if (stored.Length > maximumBytes) throw new InvalidDataException("Credential catalogue exceeds its bound.");
            var plain = encryptor.Decrypt(stored, result._context);
            try
            {
                var accounts = JsonSerializer.Deserialize<DatabaseAccount[]>(plain) ?? throw new InvalidDataException("Invalid credential catalogue.");
                if (accounts.Length > 100_000) throw new InvalidDataException("Credential catalogue exceeds its account bound.");
                foreach (var account in accounts)
                {
                    if (account is null || !Guid.TryParseExact(account.Id, "N", out _) || account.SecurityVersion < 1
                        || account.Username != AccountCredentialValidation.NormalizeUsername(account.Username)
                        || string.IsNullOrWhiteSpace(account.PasswordHash) || account.Roles is null || account.Claims is null
                        || !result._accounts.TryAdd(account.Id, account) || !result._names.TryAdd(account.Username, account.Id))
                        throw new InvalidDataException("Invalid credential catalogue.");
                }
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        return result;
    }
    public async ValueTask<DatabaseAccount?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Check(); return _names.TryGetValue(username, out var id) ? Copy(_accounts[id]) : null; }
        finally { _gate.Release(); }
    }
    public async ValueTask<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Check(); return _accounts.Count; }
        finally { _gate.Release(); }
    }
    public async ValueTask<DatabaseAccount?> FindByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Check(); return _accounts.TryGetValue(id, out var account) ? Copy(account) : null; }
        finally { _gate.Release(); }
    }
    public async ValueTask<bool> TryCreateAsync(DatabaseAccount account, int maximumAccounts, bool onlyIfEmpty, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Check();
            if (_accounts.Count >= maximumAccounts || (onlyIfEmpty && _accounts.Count != 0)
                || _accounts.ContainsKey(account.Id) || _names.ContainsKey(account.Username)) return false;
            var next = new Dictionary<string, DatabaseAccount>(_accounts, StringComparer.Ordinal) { [account.Id] = Copy(account) };
            await PublishAsync(next, cancellationToken).ConfigureAwait(false);
            _names.Add(account.Username, account.Id); return true;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask<bool> TryUpdateAsync(DatabaseAccount account, long expectedSecurityVersion, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Check();
            if (!_accounts.TryGetValue(account.Id, out var old) || old.SecurityVersion != expectedSecurityVersion
                || account.SecurityVersion != checked(expectedSecurityVersion + 1) || old.Username != account.Username) return false;
            var next = new Dictionary<string, DatabaseAccount>(_accounts, StringComparer.Ordinal) { [account.Id] = Copy(account) };
            await PublishAsync(next, cancellationToken).ConfigureAwait(false); return true;
        }
        finally { _gate.Release(); }
    }
    private async ValueTask PublishAsync(Dictionary<string, DatabaseAccount> next, CancellationToken token)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(next.Values.ToArray());
        byte[] cipher;
        try
        {
            if (plain.Length > _maximumBytes - 512) throw new AccountStoreCapacityException();
            cipher = _encryptor.Encrypt(plain, _context);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (cipher.Length > _maximumBytes) throw new AccountStoreCapacityException();
        token.ThrowIfCancellationRequested();
        try { await _store.PutAsync(CatalogueKey, cipher, token).ConfigureAwait(false); }
        catch { _faulted = true; throw; } // Unknown outcome requires controlled reopen; never trust stale permissions.
        _accounts = next;
    }
    private static DatabaseAccount Copy(DatabaseAccount account) => account with
        { Roles = account.Roles.ToArray(), Claims = account.Claims.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal) };
    private void Check()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) throw new InvalidOperationException("The credential store requires controlled reopen after an uncertain write.");
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; _accounts.Clear(); _names.Clear(); }
        finally { _gate.Release(); }
        // The backing store and encryptor are borrowed; the composition root owns their lifetimes.
    }
}
