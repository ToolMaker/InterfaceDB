using System;
using Microsoft.Extensions.DependencyInjection;
using NetCoreServer;
using RepoTcpHost.Events;
using RepoTcpHost.Messaging;
using RepoTcpHost.Serialization;
using RepoTcpHost.Transport;

namespace RepoTcpHost.Server;

public sealed class RepoSession : TcpSession
{
    private readonly IProtocolHandler _handler;
    private readonly IEventBus _eventBus;
    private readonly ISerializer _serializer;
    private readonly FrameReassembler _frames = new();

    public RepoSession(TcpServer server, IServiceProvider services, IEventBus eventBus, ISerializer serializer)
        : base(server)
    {
        _handler = services.GetRequiredService<IProtocolHandler>();
        _eventBus = eventBus;
        _serializer = serializer;
    }

    protected override void OnDisconnected()
    {
        base.OnDisconnected();
        _eventBus.UnsubscribeAll(this);
    }

    protected override void OnReceived(byte[] buffer, long offset, long size)
    {
        var span = buffer.AsSpan((int)offset, (int)size);
        var frames = _frames.Feed(span);
        foreach (var frame in frames)
        {
            var (envelope, payload) = TransportCodec.DeserializeFrame(_serializer, frame.Span);
            _handler.Handle(this, envelope, payload);
        }
    }

    public void Send(MsgEnvelope envelope, ReadOnlyMemory<byte> payload)
    {
        var framed = TransportCodec.SerializeFramed(_serializer, envelope, payload.Span);
        if (!SendAsync(framed))
        {
            Disconnect();
        }
    }
}
