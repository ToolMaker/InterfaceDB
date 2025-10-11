using MessagePack;

namespace RepoTcpHost.Domain;

[MessagePackObject]
public sealed class PersonQuery
{
    [Key(0)] public int Count { get; set; }
}
