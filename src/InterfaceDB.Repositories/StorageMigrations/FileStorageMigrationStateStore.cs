using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;

namespace Idb.Commercial.Repositories.StorageMigrations;

/// <summary>Bounded durable receipt, including recovery assignments and published ownership.
/// Use the destination's encryptor so identities and content hashes are not exposed.</summary>
public sealed class FileStorageMigrationStateStore : IStorageMigrationStateStore
{
    private readonly string _path;
    private readonly IEncryptor _encryptor;
    private readonly IFilePublication _publication;
    private readonly byte[] _context;
    private readonly int _maximumBytes;
    private readonly IFileLeaseValidation _leaseValidation;
    public string? StoragePath => _path;
    public FileStorageMigrationStateStore(string path, string databaseIdentity, IEncryptor encryptor, IFilePublication publication,
        int maximumReceiptBytes = 16 * 1024 * 1024, IFileLeaseValidation? leaseValidation = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReceiptBytes);
        _maximumBytes = maximumReceiptBytes;
        _leaseValidation = leaseValidation ?? PlatformFileLeaseValidation.Instance;
        _path = Path.GetFullPath(path);
        _encryptor = encryptor;
        _publication = publication;
        _context = Encoding.UTF8.GetBytes("InterfaceDB:storage-migration:v1:" + databaseIdentity);
    }

    public async ValueTask<IAsyncDisposable> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        StorageMigrationPaths.Validate(_path);
        StorageMigrationPaths.Validate(_path + ".lock");
        _publication.EnsureDirectory(Path.GetDirectoryName(_path)!);
        _leaseValidation.RequireExclusiveHandles(Path.GetDirectoryName(_path)!);
        var lease = await MigrationFileIO.AcquireAsync(_path + ".lock", cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var temporary in Directory.EnumerateFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".creating-*"))
            {
                var suffix = temporary[(_path.Length + ".creating-".Length)..];
                if (!Guid.TryParseExact(suffix, "N", out _)) continue;
                StorageMigrationPaths.Validate(temporary);
                _publication.Delete(temporary);
            }
            return lease;
        }
        catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async ValueTask<StorageMigrationState?> ReadAsync(CancellationToken cancellationToken)
    {
        StorageMigrationPaths.Validate(_path);
        byte[] data;
        try
        {
            await using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (file.Length > _maximumBytes) throw new InvalidDataException("Migration receipt exceeds its size limit.");
            data = new byte[checked((int)file.Length)];
            await file.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var plain = _encryptor is IContextualEncryptor contextual ? contextual.Decrypt(data, _context) : _encryptor.Decrypt(data);
        try
        {
            if (plain.Length > _maximumBytes) throw new InvalidDataException("Migration receipt exceeds its size limit.");
            var state = JsonSerializer.Deserialize<StorageMigrationState>(plain)
                ?? throw new InvalidDataException("Invalid storage migration receipt.");
            if (string.IsNullOrWhiteSpace(state.Plan) || string.IsNullOrWhiteSpace(state.SourceDigest)
                || state.RecordCount < 0 || state.VerifiedRecords < 0 || state.VerifiedRecords > state.RecordCount
                || state.RevisionFloor < 0 || state.Completed && (state.VerifiedRecords != state.RecordCount || state.PendingKeys.Length != 0))
                throw new InvalidDataException("Invalid storage migration receipt fields.");
            if (state.Retired && (!state.Completed || state.MigrationId is null || state.CleanupRecords is not null)
                || state.CleanupRequired && !state.Retired && state.CleanupRecords is null)
                throw new InvalidDataException("Invalid migration retirement marker.");
            return state;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public ValueTask WriteAsync(StorageMigrationState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return MigrationFileIO.PublishAsync(_path, Encode(state), _publication, cancellationToken);
    }

    public ValueTask ValidateCapacityAsync(StorageMigrationState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var encoded = Encode(state);
        CryptographicOperations.ZeroMemory(encoded);
        return ValueTask.CompletedTask;
    }

    private byte[] Encode(StorageMigrationState state)
    {
        using var buffer = new BoundedReceiptBuffer(_maximumBytes);
        JsonSerializer.Serialize(buffer, state);
        var plain = buffer.ToArray();
        byte[] cipher;
        try
        {
            if (plain.Length > _maximumBytes) throw new InvalidDataException("Migration receipt exceeds its size limit; increase the configured bound before retrying with fresh providers.");
            cipher = _encryptor is IContextualEncryptor contextual ? contextual.Encrypt(plain, _context) : _encryptor.Encrypt(plain);
            if (cipher.Length > _maximumBytes) throw new InvalidDataException("Encrypted migration receipt exceeds its size limit.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        return cipher;
    }

    private sealed class BoundedReceiptBuffer(int maximumBytes) : MemoryStream
    {
        private void Check(int count)
        {
            if (Position + count > maximumBytes)
                throw new InvalidDataException("Migration receipt exceeds its size limit; increase the configured bound before retrying.");
        }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
        protected override void Dispose(bool disposing)
        {
            if (disposing && TryGetBuffer(out var bytes)) CryptographicOperations.ZeroMemory(bytes.AsSpan());
            base.Dispose(disposing);
        }
    }
}

internal static class MigrationFileIO
{
    public static async ValueTask<IAsyncDisposable> AcquireAsync(string path, CancellationToken cancellationToken)
    {
        var until = Environment.TickCount64 + 30_000;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None); }
            catch (IOException) when (Environment.TickCount64 < until)
            { await Task.Delay(25, cancellationToken).ConfigureAwait(false); }
        }
    }

    public static async ValueTask PublishAsync(string path, byte[] bytes, IFilePublication publication, CancellationToken cancellationToken)
    {
        publication.EnsureDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".creating-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            publication.Replace(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
