using System;
using Idb.Libraries.Abstractions.Services.Serialization;
using ContractPerson = MigrationTool.Contracts.Person.v1_0_0;

namespace IdbDemoStorage.Storage.Person.v1_0_0;

public sealed class Employee : ContractPerson.IPerson
{
    public Employee(Guid id, string name, int age, string department)
    {
        Id = id;
        Name = name;
        Age = age;
        Role = "Employee";
        Department = department;
    }

    public Guid Id { get; }
    public string Name { get; }
    public int Age { get; }
    public string Role { get; }
    public string Department { get; }
    public IObjectEnvelope<ContractPerson.IPerson>? Envelope { get; private set; }
}
