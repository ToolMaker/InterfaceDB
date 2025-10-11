using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using RepoTcpHost.Messaging;
using RepoTcpHost.Server;

namespace RepoTcpHost.Events;

public sealed class EventBus : IEventBus
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<RepoSession, int>> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<RepoSession, HashSet<string>> _sessionTopics = new();

    public void Subscribe(RepoSession session, string topic, int mask)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            throw new ArgumentException("Topic cannot be null or whitespace.", nameof(topic));
        }

        var topicSet = _subscriptions.GetOrAdd(topic, _ => new ConcurrentDictionary<RepoSession, int>());
        topicSet[session] = mask;

        var sessionSet = _sessionTopics.GetOrAdd(session, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        lock (sessionSet)
        {
            sessionSet.Add(topic);
        }
    }

    public void Unsubscribe(RepoSession session, string topic)
    {
        if (_subscriptions.TryGetValue(topic, out var topicSet))
        {
            topicSet.TryRemove(session, out _);
        }

        if (_sessionTopics.TryGetValue(session, out var sessionSet))
        {
            lock (sessionSet)
            {
                sessionSet.Remove(topic);
                if (sessionSet.Count == 0)
                {
                    _sessionTopics.TryRemove(session, out _);
                }
            }
        }
    }

    public void UnsubscribeAll(RepoSession session)
    {
        if (_sessionTopics.TryRemove(session, out var topics))
        {
            foreach (var topic in topics)
            {
                if (_subscriptions.TryGetValue(topic, out var topicSet))
                {
                    topicSet.TryRemove(session, out _);
                }
            }
        }
    }

    public void Publish(string topic, ReadOnlyMemory<byte> payload)
    {
        if (!_subscriptions.TryGetValue(topic, out var topicSet) || topicSet.IsEmpty)
        {
            return;
        }

        var envelope = new MsgEnvelope
        {
            Version = 1,
            Type = MsgType.Event,
            Topic = topic,
            CorrelationId = Guid.NewGuid(),
            Command = "EVENT",
            Status = 0
        };

        foreach (var (session, _) in topicSet)
        {
            session.Send(envelope, payload);
        }
    }
}
