using System;

namespace Idb.Libraries.Demo.Person.v1_0_0;

/// <summary>
/// Original Person contract (v1.0.0).
/// </summary>
public interface IPerson
{
    Guid Id { get; }
    string Name { get; }
    int Age { get; }
}

