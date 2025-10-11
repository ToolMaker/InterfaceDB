using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RepoTcpHost.Configuration;
using RepoTcpHost.Domain;
using RepoTcpHost.Events;
using RepoTcpHost.HostedServices;
using RepoTcpHost.Serialization;
using RepoTcpHost.Server;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

builder.Services.AddLogging(logging => logging.AddConsole());

var tcpConfig = builder.Configuration.GetSection("Tcp").Get<TcpConfig>() ?? new TcpConfig();
var serializationConfig = builder.Configuration.GetSection("Serialization").Get<SerializationConfig>() ?? new SerializationConfig();

builder.Services.AddSingleton(tcpConfig);
builder.Services.AddSingleton(serializationConfig);
builder.Services.AddSingleton<ISerializer>(sp => new MsgPackSerializer(serializationConfig));
builder.Services.AddSingleton<IEventBus, EventBus>();
builder.Services.AddSingleton<IPersonStore, InMemoryPersonStore>();
builder.Services.AddSingleton<IProtocolHandler, ProtocolHandler>();
builder.Services.AddHostedService<DemoClientHostedService>();

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RepoTcpHost");
var serializer = host.Services.GetRequiredService<ISerializer>();
var eventBus = host.Services.GetRequiredService<IEventBus>();
var ip = IPAddress.Parse(tcpConfig.IpAddress);
var server = new RepoServer(ip, tcpConfig.Port, host.Services, eventBus, serializer);

void StopServer()
{
    if (server.IsStarted)
    {
        server.Stop();
    }
}

AppDomain.CurrentDomain.ProcessExit += (_, _) => StopServer();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    StopServer();
};

host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(StopServer);

if (server.Start())
{
    logger.LogInformation("Listening on {Address}:{Port}", tcpConfig.IpAddress, tcpConfig.Port);
    await host.RunAsync();
}
else
{
    logger.LogCritical("Failed to start server on {Address}:{Port}", tcpConfig.IpAddress, tcpConfig.Port);
}
