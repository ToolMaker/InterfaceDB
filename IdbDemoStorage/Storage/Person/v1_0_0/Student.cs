using System;

namespace IdbDemoStorage.Storage.Person.v1_0_0;

public sealed class Student : PersonBase
{
    public Student(Guid id, string name, int age, string course)
        : base(id, name, age, "Student")
    {
        Course = course;
    }

    public string Course { get; }
}
