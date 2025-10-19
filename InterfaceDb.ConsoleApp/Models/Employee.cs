using System;

namespace InterfaceDb.ConsoleApp.Models;

public sealed class Employee : PersonBase
{
    public Employee(Guid id, string name, int age, string department)
        : base(id, name, age, "Employee")
    {
        Department = department;
    }

    public string Department { get; }
}
