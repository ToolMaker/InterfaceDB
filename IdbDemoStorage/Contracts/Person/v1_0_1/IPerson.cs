namespace MigrationTool.Contracts.Person.v1_0_1;

using Idb.Libraries.Abstractions.Attributes;

[StorageScope(VersionTag = "1.0.1")]
public interface IPerson
{
    string FirstName { get; }

    string LastName { get; }

    string Address { get; }
}

