using System.Buffers.Binary;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>Durably reserves timestamp ranges before use, including across reopen/deletion/clock rollback.</summary>
internal sealed class StoreRevisionClock(string root, IFilePublication? publication = null) : IRevisionTimestampGenerator
{
    private readonly object _gate = new();
    private readonly MonotonicRevisionTimestampGenerator _clock = new();
    private readonly IFilePublication _publication = publication ?? PlatformFilePublication.Instance;
    private long _next;
    private long _end;
    private bool _directoryReady;

    public long Next(long observed)
    {
        lock (_gate)
        {
            var next = Math.Max(checked(_next + 1), _clock.Next(observed));
            if (next > _end)
            {
                if (!_directoryReady)
                {
                    _publication.EnsureDirectory(root);
                    _directoryReady = true;
                }
                var path = Path.Combine(root, ".idb-revision-clock");
                // The stable lease survives replacement of the clock file and serializes
                // independent processes. Upgrade every writer before using this protocol.
                using var lease = AcquireLease(path + ".lock");
                Span<byte> reservation = stackalloc byte[16];
                if (File.Exists(path))
                {
                    using var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (current.Length != 16) throw new InvalidDataException("Invalid revision clock; restore the store including its clock.");
                    current.ReadExactly(reservation);
                    var floor = BinaryPrimitives.ReadInt64LittleEndian(reservation);
                    if (floor < 0 || BinaryPrimitives.ReadInt64LittleEndian(reservation[8..]) != ~floor)
                        throw new InvalidDataException("Corrupt revision clock; restore the store including its clock.");
                    next = Math.Max(next, checked(floor + 1));
                }
                var reservedEnd = checked(next + 1_048_575);
                BinaryPrimitives.WriteInt64LittleEndian(reservation, reservedEnd);
                BinaryPrimitives.WriteInt64LittleEndian(reservation[8..], ~reservedEnd);
                var staging = path + ".creating-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var prepared = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough))
                    {
                        prepared.Write(reservation);
                        prepared.Flush(true);
                    }
                    // No revision from this range is issued before durable publication.
                    // A torn staging write leaves the last committed clock untouched.
                    _publication.Replace(staging, path);
                    _end = reservedEnd;
                }
                finally { if (File.Exists(staging)) File.Delete(staging); }
            }
            return _next = next;
        }
    }

    private static FileStream AcquireLease(string path)
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None); }
            catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(5); }
        }
    }
}
