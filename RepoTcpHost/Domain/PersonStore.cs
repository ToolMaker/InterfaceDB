using System.Collections.Concurrent;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RepoTcpHost.Domain;

public interface IPersonStore
{
    void Upsert(Person person);
    IReadOnlyList<Person> Take(int count);
}

public sealed class InMemoryPersonStore : IPersonStore
{
    private readonly ConcurrentDictionary<string, Person> _people = new();

    public void Upsert(Person person)
    {
        _people[person.Id] = person;
    }

    public IReadOnlyList<Person> Take(int count)
    {
        if (count <= 0)
        {
            return Array.Empty<Person>();
        }

        return _people.Values
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .Take(count)
            .ToList();
    }
}
