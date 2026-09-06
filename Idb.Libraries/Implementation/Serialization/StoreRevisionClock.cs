using System.Buffers.Binary;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>Durably reserves timestamp ranges before use, including across reopen/deletion/clock rollback.</summary>
internal sealed class StoreRevisionClock(string root)
{
    private readonly object _gate = new();
    private readonly MonotonicRevisionTimestampGenerator _clock = new();
    private long _next;
    private long _end;

    public long Next(long observed)
    {
        lock (_gate)
        {
            var next = Math.Max(checked(_next + 1), _clock.Next(observed));
            if (next > _end)
            {
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, ".idb-revision-clock");
                FileStream stream;
                var deadline = Environment.TickCount64 + 30_000;
                while (true)
                {
                    try { stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough); break; }
                    catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(5); }
                }
                using (stream)
                {
                    Span<byte> buffer = stackalloc byte[16];
                    if (stream.Length != 0)
                    {
                        if (stream.Length != 16) throw new InvalidDataException("Invalid revision clock; restore the store including its clock.");
                        stream.ReadExactly(buffer);
                        var floor = BinaryPrimitives.ReadInt64LittleEndian(buffer);
                        if (floor < 0 || BinaryPrimitives.ReadInt64LittleEndian(buffer[8..]) != ~floor)
                            throw new InvalidDataException("Corrupt revision clock; restore the store including its clock.");
                        next = Math.Max(next, checked(floor + 1));
                    }
                    var reservedEnd = checked(next + 1_048_575);
                    BinaryPrimitives.WriteInt64LittleEndian(buffer, reservedEnd);
                    BinaryPrimitives.WriteInt64LittleEndian(buffer[8..], ~reservedEnd);
                    stream.Position = 0;
                    stream.Write(buffer);
                    stream.Flush(true);
                    _end = reservedEnd;
                }
            }
            return _next = next;
        }
    }
}
