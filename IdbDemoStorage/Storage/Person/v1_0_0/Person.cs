namespace MigrationTool.Contracts.Person.v1_0_0;

public sealed record Person(string FirstName, string LastName) : IPerson;
