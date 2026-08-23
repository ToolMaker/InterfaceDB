# Why InterfaceDB

## The Architectural Problem

Application cores often begin with clean domain interfaces and gradually acquire persistence concerns: database IDs, ORM annotations, table mappings, connection strings, serialization calls, paths, and provider-specific query APIs. Once those details enter application services, replacing or testing the storage layer becomes expensive.

InterfaceDB keeps the dependency direction explicit:

```text
application service
    -> IRepository<TContract>
        -> IIndexManager<TContract>
            -> IEnvelopeRepository<TContract>
                -> file provider
```

The core works with a contract interface and a repository abstraction. The composition root chooses storage, caching, durability, migration, and encryption behavior.

## Interface-First, Not Relational-First

An ORM begins with a relational store and maps rows to objects. InterfaceDB begins with a versioned C# interface and stores whichever concrete implementation fulfilled that contract. The envelope records the implementation type, version, logical and physical identity, lifecycle state, cache preference, and encrypted-field metadata needed to reconstruct the object.

That difference is useful when:

- several implementations belong in one repository;
- domain code should not know file paths or relational schemas;
- stored object versions must remain explicit;
- local data should work without a server;
- storage behavior should be replaceable below one repository contract.

InterfaceDB should be described as an **interface-first object repository** or **interface-backed object database**. It is not useful to make an unverified “first in the world” claim; the value is the concrete dependency boundary and the implemented behavior.

## What The Abstraction Buys You

An application service can depend only on:

```csharp
public sealed class EnrollmentService(IRepository<IPerson> people)
{
    public ValueTask<IPerson> EnrollAsync(IPerson person, CancellationToken cancellationToken)
        => people.SaveAsync(person, cancellationToken);
}
```

The service does not open files, serialize MessagePack, select a concrete student type during reload, maintain radix/range indexes, coordinate another process, or decrypt storage. Those responsibilities remain below the repository boundary.

This does not make storage behavior disappear. It makes the behavior explicit at the composition root, where infrastructure decisions belong.

## Why Files?

The current provider deliberately stores objects as individual files. That gives it properties a single opaque database file does not provide:

- objects can be inspected, copied, synchronized, or managed independently;
- a damaged object does not automatically make every object inaccessible;
- external file changes can be observed and reconciled;
- storage remains application-owned and serverless;
- simple backup tools can operate on the data directory.

The tradeoff is filesystem metadata and durable file lifecycle cost. A file-per-object provider cannot match every workload served by a page cache or append-only WAL. InterfaceDB exposes Performance and Durable modes and measures both honestly.

## When To Choose InterfaceDB

Choose it when you value abstraction-first application design, polymorphic local objects, explicit contract versions, local operation, per-object files, and repository-level indexing.

Choose SQLite, another embedded database, or a server database when relational joins, mature transactions, persistent query statistics, replication, remote access, or very high durable write rates are the primary requirement.

The models can also coexist: InterfaceDB can own application-local polymorphic state while a relational system owns shared transactional data.
