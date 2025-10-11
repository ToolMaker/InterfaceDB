using MessagePack;

namespace RepoTcpHost.Domain;

public interface IPerson
{
    string Id { get; }
    string FirstName { get; }
    string LastName { get; }
    int Age { get; }
}

[MessagePackObject]
public sealed record Person(
    [property: Key(0)] string Id,
    [property: Key(1)] string FirstName,
    [property: Key(2)] string LastName,
    [property: Key(3)] int Age
) : IPerson;
