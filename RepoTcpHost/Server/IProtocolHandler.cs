using RepoTcpHost.Messaging;

namespace RepoTcpHost.Server;

public interface IProtocolHandler
{
    void Handle(RepoSession session, MsgEnvelope envelope, ReadOnlyMemory<byte> payload);
}
