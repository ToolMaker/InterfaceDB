using System.Text.Json;

namespace Idb.Transport.Repositories;

public interface IManagedSetupReceiptStore
{
    ValueTask<string?> ReadAsync(string scope, CancellationToken cancellationToken = default);
    ValueTask WriteAsync(string scope, string signedReceipt, CancellationToken cancellationToken = default);
}

/// <summary>Bounded, atomically replaced approval receipt. Integrity comes from the server signature, not this file.</summary>
public sealed class FileManagedSetupReceiptStore(string path) : IManagedSetupReceiptStore
{
    private readonly string _path = Path.GetFullPath(path);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed record State(string Scope, string Receipt);
    public async ValueTask<string?> ReadAsync(string scope, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return null;
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > ManagedSetupProtocol.MaximumReceiptBytes * 3) throw new InvalidDataException("Invalid setup receipt size.");
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            var state = JsonSerializer.Deserialize<State>(bytes) ?? throw new InvalidDataException("Invalid setup receipt.");
            if (state.Scope is null || state.Scope.Length > 128 || state.Receipt is null || state.Receipt.Length > ManagedSetupProtocol.MaximumReceiptBytes * 2)
                throw new InvalidDataException("Invalid setup receipt.");
            return state.Scope == scope ? state.Receipt : null;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask WriteAsync(string scope, string signedReceipt, CancellationToken cancellationToken = default)
    {
        if (scope.Length > 128 || signedReceipt.Length > ManagedSetupProtocol.MaximumReceiptBytes * 2) throw new ArgumentException("Invalid setup receipt size.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new State(scope, signedReceipt)), cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); _gate.Release(); }
    }
}
