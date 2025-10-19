using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;
using System;
using System.IO;

namespace InterfaceDb.ConsoleApp.Models;

[StorageScope(VersionTag ="1.0.0")]
public interface IPerson : IRepoDto<IPerson, FileInfo>
{
    Guid Id { get; }
    string Name { get; }
    int Age { get; }
    string Role { get; }
}
