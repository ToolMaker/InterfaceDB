using System;
using System.IO;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;

namespace MigrationTool.Contracts.Person.v1_0_0;

[StorageScope(VersionTag = "1.0.0")]
public interface IPerson : IRepoDto<IPerson, FileInfo>
{
    Guid Id { get; }
    string Name { get; }
    int Age { get; }
    string Role { get; }
}
