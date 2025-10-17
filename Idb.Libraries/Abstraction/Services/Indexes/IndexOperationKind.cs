namespace Idb.Libraries.Abstractions.Services.Indexes;

/// <summary>
/// Identifies the type of work an index selection request is performing.
/// </summary>
public enum IndexOperationKind
{
    Unknown = 0,
    Read = 1,
    Write = 2
}

