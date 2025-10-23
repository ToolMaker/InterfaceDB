using System;
using System.Collections.Generic;
using System.Linq;
using PersonContract = MigrationTool.Contracts.Person.v1_0_0;
using PersonStorage = IdbDemoStorage.Storage.Person.v1_0_0;

namespace PerformanceCommon;

public static class PersonDataFactory
{
    private static readonly string[] FirstNames = { "Alex", "Jordan", "Taylor", "Casey", "Morgan", "Riley", "Sydney", "Robin" };
    private static readonly string[] Departments = { "Engineering", "Finance", "Marketing", "Operations", "Research" };
    private static readonly string[] Courses = { "Mathematics", "Physics", "Computer Science", "History", "Economics" };
    private static readonly Random Random = new();

    public static List<PersonContract.IPerson> CreatePersons(int count)
        => Enumerable.Range(0, count)
            .Select(CreatePerson)
            .ToList();

    public static List<PersonContract.IPerson> ClonePersons(IEnumerable<PersonContract.IPerson> source)
        => source.Select(ClonePerson).ToList();

    private static PersonContract.IPerson CreatePerson(int index)
    {
        var name = $"{Pick(FirstNames)} {Pick(FirstNames)}";
        var age = Random.Next(18, 60);

        return index % 2 == 0
            ? new PersonStorage.Student(Guid.NewGuid(), name, age, Pick(Courses))
            : new PersonStorage.Employee(Guid.NewGuid(), name, age, Pick(Departments));
    }

    private static PersonContract.IPerson ClonePerson(PersonContract.IPerson person)
        => person switch
        {
            PersonStorage.Student student => new PersonStorage.Student(student.Id, student.Name, student.Age, student.Course),
            PersonStorage.Employee employee => new PersonStorage.Employee(employee.Id, employee.Name, employee.Age, employee.Department),
            _ => new PersonStorage.Employee(person.Id, person.Name, person.Age, person.Role)
        };

    private static string Pick(IReadOnlyList<string> source)
        => source[Random.Next(source.Count)];
}
