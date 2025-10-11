using RepoTcpHost.Messaging;
using RepoTcpHost.Server;

namespace RepoTcpHost.Events;

public interface IEventBus
{
    void Subscribe(RepoSession session, string topic, int mask = 0);
    void Unsubscribe(RepoSession session, string topic);
    void UnsubscribeAll(RepoSession session);
    void Publish(string topic, ReadOnlyMemory<byte> payload);
}
