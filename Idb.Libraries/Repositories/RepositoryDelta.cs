using System.Text.Json;
using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Repositories;

public sealed record RepositoryDelta<T>(string Cursor, bool IsSnapshot, IReadOnlyList<T> Records,
    IReadOnlyList<string> DeletedPhysicalKeys);
public interface IRepositoryDeltaSource<T> where T : class, IStoredContract<T>
{
    ValueTask<RepositoryDelta<T>> ReadDeltaAsync(string? cursor, IReadOnlyCollection<string> includePhysicalKeys,
        CancellationToken cancellationToken = default);
}
public interface IRepositoryCursorStore
{
    ValueTask<string?> ReadAsync(string scope, CancellationToken cancellationToken = default);
    ValueTask WriteAsync(string scope, string cursor, CancellationToken cancellationToken = default);
}

/// <summary>Own this file with the cache. Advance only after durable record application; back it up with the cache.</summary>
public sealed class FileRepositoryCursorStore(string path) : IRepositoryCursorStore
{
    private readonly string _path = Path.GetFullPath(path);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed record State(string Scope, string Cursor);
    public async ValueTask<string?> ReadAsync(string scope, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return null;
            if (new FileInfo(_path).Length > 16 * 1024) throw new InvalidDataException("Invalid synchronization cursor.");
            var state = JsonSerializer.Deserialize<State>(await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false))
                ?? throw new InvalidDataException("Invalid synchronization cursor.");
            if (state.Scope is null || state.Scope.Length > 2048 || state.Cursor is null || state.Cursor.Length > 128)
                throw new InvalidDataException("Invalid synchronization cursor.");
            return state.Scope == scope ? state.Cursor : null;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask WriteAsync(string scope, string cursor, CancellationToken cancellationToken = default)
    {
        if (scope.Length > 2048 || cursor.Length > 128) throw new ArgumentException("Invalid cursor identity.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new State(scope, cursor));
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false); stream.Flush(true); }
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); _gate.Release(); }
    }
}