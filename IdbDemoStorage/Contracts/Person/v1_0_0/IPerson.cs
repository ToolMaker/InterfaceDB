namespace MigrationTool.Contracts.Person.v1_0_0;

using Idb.Libraries.Abstractions.Attributes;

[StorageScope(VersionTag = "1.0.0")]
public interface IPerson
{
    string FirstName { get; }

    string LastName { get; }
}

