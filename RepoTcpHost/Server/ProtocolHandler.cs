using System;
using MessagePack;
using RepoTcpHost.Domain;
using RepoTcpHost.Events;
using RepoTcpHost.Messaging;
using RepoTcpHost.Serialization;

namespace RepoTcpHost.Server;

public sealed class ProtocolHandler : IProtocolHandler
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IEventBus _eventBus;
    private readonly ISerializer _serializer;
    private readonly IPersonStore _personStore;

    public ProtocolHandler(IServiceProvider serviceProvider, IEventBus eventBus, ISerializer serializer, IPersonStore personStore)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _personStore = personStore ?? throw new ArgumentNullException(nameof(personStore));
    }

    public void Handle(RepoSession session, MsgEnvelope envelope, ReadOnlyMemory<byte> payload)
    {
        if (envelope.Version != 1)
        {
            RespondError(session, envelope, 426, "Unsupported protocol version");
            return;
        }

        switch (envelope.Command)
        {
            case "PING":
                Respond(session, envelope, _serializer.Pack(new { serverTimeUtc = DateTime.UtcNow }));
                break;
            case "SUB":
                HandleSubscribe(session, envelope, payload);
                break;
            case "UNSUB":
                HandleUnsubscribe(session, envelope, payload);
                break;
            case "PUB":
                HandlePublish(session, envelope, payload);
                break;
            case "PERSON_STORE":
                HandlePersonStore(session, envelope, payload);
                break;
            case "PERSON_GET":
                HandlePersonGet(session, envelope, payload);
                break;
            default:
                RespondError(session, envelope, 400, $"Unknown command '{envelope.Command}'");
                break;
        }
    }

    private void HandleSubscribe(RepoSession session, MsgEnvelope envelope, ReadOnlyMemory<byte> payload)
    {
        var request = DeserializeTopicRequest(payload);
        if (string.IsNullOrWhiteSpace(request.Topic))
        {
            RespondError(session, envelope, 422, "Missing topic");
            return;
        }

        _eventBus.Subscribe(session, request.Topic!, request.Mask);
        Respond(session, envelope, ReadOnlyMemory<byte>.Empty);
    }

    private void HandleUnsubscribe(RepoSession session, MsgEnvelope envelope, ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty && string.IsNullOrWhiteSpace(envelope.Topic))
        {
            _eventBus.UnsubscribeAll(session);
            Respond(session, envelope, ReadOnlyMemory<byte>.Empty);
            return;
        }

        var request = payload.IsEmpty
            ? new TopicDescriptor(envelope.Topic, 0)
            : DeserializeTopicRequest(payload);

        if (string.IsNullOrWhiteSpace(request.Topic))
        {
            RespondError(session, envelope, 422, "Missing topic");
            return;
        }

        _eventBus.Unsubscribe(session, request.Topic!);
        Respond(session, envelope, ReadOnlyMemory<byte>.Empty);
    }

    private void HandlePublish(RepoSession session, MsgEnvelope envelope, ReadOnlyMemory<byte> payload)
    {
        if (string.IsNullOrWhiteSpace(envelope.Topic))
        {
            RespondError(session, envelope, 422, "Missing topic");
            return;
        }

        _eventBus.Publish(envelope.Topic!, payload);
        Respond(session, envelope, ReadOnlyMemory<byte>.Empty);
    }

    private void HandlePersonStore(RepoSession session, MsgEnvelope envelope, ReadOnlyMemory<byte> payload)
    {
        try
        {
            var persons = _serializer.Unpack<List<Person>>(payload.Span);
            foreach (var person in persons)
            {
                _personStore.Upsert(person);
            }

            Respond(session, envelope, ReadOnlyMemory<byte>.Empty);
        }
        catch (Exception ex)
        {
            RespondError(session, envelope, 500, $"Failed to store persons: {ex.Message}");
        }
    }

    private void HandlePersonGet(RepoSession session, MsgEnvelope envelope, ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            RespondError(session, envelope, 422, "Missing request payload");
            return;
        }

        int count;
        try
        {
            var request = _serializer.Unpack<PersonQuery>(payload.Span);
            count = request.Count;
        }
        catch (Exception ex)
        {
            RespondError(session, envelope, 400, $"Invalid request payload: {ex.Message}");
            return;
        }

        if (count <= 0)
        {
            RespondError(session, envelope, 422, "Count must be greater than zero");
            return;
        }

        var people = _personStore.Take(count);
        Respond(session, envelope, _serializer.Pack(people));
    }

    private TopicDescriptor DeserializeTopicRequest(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return new TopicDescriptor(null, 0);
        }

        try
        {
            var request = _serializer.Unpack<TopicRequest>(payload.Span);
            return new TopicDescriptor(request.Topic, request.Mask);
        }
        catch (MessagePackSerializationException)
        {
            return new TopicDescriptor(null, 0);
        }
    }

    private void Respond(RepoSession session, MsgEnvelope request, ReadOnlyMemory<byte> body)
    {
        var envelope = new MsgEnvelope
        {
            Version = request.Version,
            Type = MsgType.Response,
            CorrelationId = request.CorrelationId,
            Command = request.Command,
            Topic = request.Topic,
            Status = 0
        };

        session.Send(envelope, body);
    }

    private void RespondError(RepoSession session, MsgEnvelope request, int status, string message)
    {
        var envelope = new MsgEnvelope
        {
            Version = request.Version,
            Type = MsgType.Response,
            CorrelationId = request.CorrelationId,
            Command = request.Command,
            Topic = request.Topic,
            Status = status
        };

        var body = _serializer.Pack(new { error = message });
        session.Send(envelope, body);
    }

    [MessagePackObject]
    public sealed class TopicRequest
    {
        [Key(0)] public string? Topic { get; set; }
        [Key(1)] public int Mask { get; set; }
    }

    private readonly record struct TopicDescriptor(string? Topic, int Mask);
}
