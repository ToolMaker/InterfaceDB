using System;
using System.IO;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace InterfaceDb.ConsoleApp.Models;

public abstract class PersonBase : IPerson
{
    protected PersonBase(Guid id, string name, int age, string role)
    {
        Id = id;
        Name = name;
        Age = age;
        Role = role;
    }

    public Guid Id { get; }
    public string Name { get; }
    public int Age { get; }
    public string Role { get; }

    public IObjectEnvelope<IPerson, FileInfo>? Envelope { get; set; }
}
