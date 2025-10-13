namespace MigrationTool.Contracts.Person.v1_0_1;

public sealed record Person(string FirstName, string LastName, string Address) : IPerson;
