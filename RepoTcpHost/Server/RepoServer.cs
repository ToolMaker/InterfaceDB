using System;
using System.Net;
using NetCoreServer;
using RepoTcpHost.Events;
using RepoTcpHost.Serialization;

namespace RepoTcpHost.Server;

public sealed class RepoServer : TcpServer
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IEventBus _eventBus;
    private readonly ISerializer _serializer;

    public RepoServer(IPAddress address, int port, IServiceProvider services, IEventBus eventBus, ISerializer serializer)
        : base(address, port)
    {
        _serviceProvider = services ?? throw new ArgumentNullException(nameof(services));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    protected override TcpSession CreateSession() => new RepoSession(this, _serviceProvider, _eventBus, _serializer);
}
