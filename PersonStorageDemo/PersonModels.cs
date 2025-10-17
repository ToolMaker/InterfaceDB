using Idb.Libraries.Abstractions.Attributes;

[StorageScope(VersionTag = "v1")]
public interface IPerson
{
    string Id { get; set; }
    string FirstName { get; set; }
    string LastName { get; set; }
    int Age { get; set; }
}

public sealed class Person : IPerson
{
    public string Id { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public int Age { get; set; }
}
