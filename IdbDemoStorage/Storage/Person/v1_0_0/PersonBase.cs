using System;
using System.IO;
using Idb.Libraries.Abstractions.Services.Serialization;
using ContractPerson = MigrationTool.Contracts.Person.v1_0_0;

namespace IdbDemoStorage.Storage.Person.v1_0_0;

public abstract class PersonBase : ContractPerson.IPerson
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

    public IObjectEnvelope<ContractPerson.IPerson, FileInfo>? Envelope { get; set; }
}
