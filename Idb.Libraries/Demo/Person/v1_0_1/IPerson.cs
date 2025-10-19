using System;

namespace Idb.Libraries.Demo.Person.v1_0_1;

/// <summary>
/// Upgraded Person contract (v1.0.1) adds the Department field.
/// </summary>
public interface IPerson
{
    Guid Id { get; }
    string Name { get; }
    int Age { get; }
    string Department { get; }
}

