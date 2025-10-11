using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using RepoTcpHost.Messaging;
using RepoTcpHost.Serialization;

namespace RepoTcpConsoleApp;

internal sealed class TcpClientSession : IAsyncDisposable
{
    private readonly ISerializer _serializer;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly byte[] _lengthBuffer = new byte[sizeof(uint)];

    public TcpClientSession(ISerializer serializer)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    public ISerializer Serializer => _serializer;
    public bool IsConnected => _client?.Connected == true;
    public string? Endpoint { get; private set; }

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        await DisconnectAsync().ConfigureAwait(false);

        var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        client.NoDelay = true;

        _client = client;
        _stream = client.GetStream();
        Endpoint = $"{host}:{port}";
    }

    public async Task DisconnectAsync()
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        _client?.Close();
        _client?.Dispose();
        _client = null;
        Endpoint = null;
    }

    public async Task<(MsgEnvelope Envelope, byte[] Payload)> SendAsync(
        string command,
        ReadOnlyMemory<byte> payload,
        string? topic = null,
        CancellationToken cancellationToken = default)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("Not connected to a TCP server.");
        }

        var envelope = new MsgEnvelope
        {
            Command = command,
            Topic = topic
        };

        var frame = TransportCodec.SerializeFramed(_serializer, envelope, payload.Span);
        await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        await ReadExactAsync(_stream, _lengthBuffer, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(_lengthBuffer);

        if (length == 0)
        {
            return (new MsgEnvelope { Status = -1 }, Array.Empty<byte>());
        }

        var buffer = new byte[length];
        await ReadExactAsync(_stream, buffer, cancellationToken).ConfigureAwait(false);
        return TransportCodec.DeserializeFrame(_serializer, buffer);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Remote host closed the connection.");
            }

            offset += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }
}
