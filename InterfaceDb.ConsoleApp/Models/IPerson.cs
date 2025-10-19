using System;
using System.IO;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace InterfaceDb.ConsoleApp.Models;

public interface IPerson : IRepoDto<IPerson, FileInfo>
{
    Guid Id { get; }
    string Name { get; }
    int Age { get; }
    string Role { get; }
}
