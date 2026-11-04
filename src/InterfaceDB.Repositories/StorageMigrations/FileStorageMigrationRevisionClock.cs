using System.Buffers.Binary;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Commercial.Repositories.StorageMigrations;

/// <summary>Adapter for the current File/SegmentCore/Atlas durable revision reservation format.</summary>
public sealed class FileStorageMigrationRevisionClock(string storeDirectory, IFilePublication publication)
    : IStorageMigrationRevisionClock
{
    private readonly string _root = Path.GetFullPath(storeDirectory);
    private string PathName => Path.Combine(_root, ".idb-revision-clock");
    public bool Exists => File.Exists(PathName);

    public async ValueTask<long> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(PathName)) return 0;
        await using var file = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.Read, 1, true);
        if (file.Length != 16) throw new InvalidDataException("Invalid source or destination revision clock.");
        var bytes = new byte[16];
        await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var value = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        if (value < 0 || BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8)) != ~value)
            throw new InvalidDataException("Corrupt source or destination revision clock.");
        return value;
    }

    public async ValueTask SeedAsync(long floor, CancellationToken cancellationToken)
    {
        if (floor < 0 || floor > long.MaxValue - 1_048_576)
            throw new InvalidDataException("Revision clock has no usable reservation space.");
        publication.EnsureDirectory(_root);
        await using var lease = await MigrationFileIO.AcquireAsync(PathName + ".lock", cancellationToken).ConfigureAwait(false);
        var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (Exists && current >= floor) return;
        var bytes = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, floor);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), ~floor);
        await MigrationFileIO.PublishAsync(PathName, bytes, publication, cancellationToken).ConfigureAwait(false);
    }
}
