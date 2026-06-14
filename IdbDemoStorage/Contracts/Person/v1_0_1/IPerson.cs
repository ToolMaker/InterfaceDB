using System;
using System.IO;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;

namespace MigrationTool.Contracts.Person.v1_0_1;

[StorageScope(VersionTag = "1.0.1")]
public interface IPerson : IStoredContract<IPerson>
{
    Guid Id { get; }
    string Name { get; }
    int Age { get; }
    string Role { get; }

    string Surname { get; set; }
}
