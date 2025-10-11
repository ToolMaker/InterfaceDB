using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RepoTcpHost.Configuration;
using RepoTcpHost.Domain;
using RepoTcpHost.Messaging;
using RepoTcpHost.Serialization;

namespace RepoTcpHost.HostedServices;

public sealed class DemoClientHostedService : IHostedService
{
    private readonly TcpConfig _tcpConfig;
    private readonly ISerializer _serializer;
    private readonly ILogger<DemoClientHostedService> _logger;
    private readonly IPersonStore _personStore;
    private readonly IHostApplicationLifetime _lifetime;

    public DemoClientHostedService(
        TcpConfig tcpConfig,
        ISerializer serializer,
        ILogger<DemoClientHostedService> logger,
        IPersonStore personStore,
        IHostApplicationLifetime lifetime)
    {
        _tcpConfig = tcpConfig;
        _serializer = serializer;
        _logger = logger;
        _personStore = personStore;
        _lifetime = lifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => RunScenarioAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RunScenarioAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);

            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", _tcpConfig.Port, cancellationToken).ConfigureAwait(false);
            await using var stream = client.GetStream();

            var persons = Enumerable.Range(1, 100)
                .Select(i => new Person($"person-{i:D3}", $"First{i}", $"Last{i}", 18 + (i % 50)))
                .ToList();

            await SendRequestAsync(stream, "PERSON_STORE", persons, cancellationToken).ConfigureAwait(false);
            var storeResponse = await ReceiveResponseAsync(stream, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Stored {Count} persons (status {Status})", persons.Count, storeResponse.Envelope.Status);

            var queryPayload = new PersonQuery { Count = 50 };
            await SendRequestAsync(stream, "PERSON_GET", queryPayload, cancellationToken).ConfigureAwait(false);
            var getResponse = await ReceiveResponseAsync(stream, cancellationToken).ConfigureAwait(false);
            var fetched = _serializer.Unpack<List<Person>>(getResponse.Payload.AsSpan());
            _logger.LogInformation("Retrieved {Count} persons from server", fetched.Count);

            var storeSnapshot = _personStore.Take(5);
            _logger.LogInformation("Server-side store sample: {Sample}", string.Join(", ", storeSnapshot.Select(p => p.Id)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Demo scenario failed");
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private async Task SendRequestAsync<T>(NetworkStream stream, string command, T payload, CancellationToken cancellationToken)
    {
        var envelope = new MsgEnvelope
        {
            Command = command,
            CorrelationId = Guid.NewGuid(),
            Type = MsgType.Request,
            Version = 1
        };

        var bytes = _serializer.Pack(payload);
        var framed = TransportCodec.SerializeFramed(_serializer, envelope, bytes.Span);
        await stream.WriteAsync(framed, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(MsgEnvelope Envelope, byte[] Payload)> ReceiveResponseAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var lengthPrefix = new byte[4];
        await stream.ReadExactlyAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        var length = BitConverter.ToUInt32(lengthPrefix, 0);
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return TransportCodec.DeserializeFrame(_serializer, buffer);
    }
}

internal static class NetworkStreamExtensions
{
    public static async Task ReadExactlyAsync(this NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of stream");
            }
            offset += read;
        }
    }
}
