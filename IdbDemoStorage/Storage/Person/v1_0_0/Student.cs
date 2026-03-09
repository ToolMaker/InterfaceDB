using System;
using Idb.Libraries.Abstractions.Services.Serialization;
using ContractPerson = MigrationTool.Contracts.Person.v1_0_0;

namespace IdbDemoStorage.Storage.Person.v1_0_0;

public sealed class Student : ContractPerson.IPerson
{
    public Student(Guid id, string name, int age, string course)
    {
        Id = id;
        Name = name;
        Age = age;
        Role = "Student";
        Course = course;
    }

    public Guid Id { get; }
    public string Name { get; }
    public int Age { get; }
    public string Role { get; }
    public string Course { get; }
    public IObjectEnvelope<ContractPerson.IPerson>? Envelope { get; private set; }
}
