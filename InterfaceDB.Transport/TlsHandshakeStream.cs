namespace Idb.Transport;

/// <summary>Observes an empty transport close during TLS setup without interpreting TLS errors.</summary>
internal sealed class TlsHandshakeStream(Stream inner) : Stream
{
    private bool _observing = true;
    private bool _receivedBytes;
    internal bool PeerClosedWithoutResponse { get; private set; }
    internal void CompleteHandshake() => _observing = false;

    private int Observe(int count, int requested)
    {
        if (_observing && requested > 0)
        {
            if (count > 0) _receivedBytes = true;
            else PeerClosedWithoutResponse = !_receivedBytes;
        }
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => Observe(inner.Read(buffer, offset, count), count);
    public override int Read(Span<byte> buffer) => Observe(inner.Read(buffer), buffer.Length);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _observing ? ObserveReadAsync(buffer, offset, count, cancellationToken) : inner.ReadAsync(buffer, offset, count, cancellationToken);
    private async Task<int> ObserveReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Observe(await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false), count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _observing ? ObserveReadAsync(buffer, cancellationToken) : inner.ReadAsync(buffer, cancellationToken);
    private async ValueTask<int> ObserveReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => Observe(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false), buffer.Length);

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanTimeout => inner.CanTimeout;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override int ReadTimeout { get => inner.ReadTimeout; set => inner.ReadTimeout = value; }
    public override int WriteTimeout { get => inner.WriteTimeout; set => inner.WriteTimeout = value; }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => inner.WriteAsync(buffer, offset, count, cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.WriteAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
