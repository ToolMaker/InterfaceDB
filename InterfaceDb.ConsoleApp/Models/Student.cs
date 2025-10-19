using System;

namespace InterfaceDb.ConsoleApp.Models;

public sealed class Student : PersonBase
{
    public Student(Guid id, string name, int age, string course)
        : base(id, name, age, "Student")
    {
        Course = course;
    }

    public string Course { get; }
}
