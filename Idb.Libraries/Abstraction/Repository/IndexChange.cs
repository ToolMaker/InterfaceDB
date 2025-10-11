namespace Idb.Libraries.Abstractions.Repository;

public sealed record IndexChange(
    Type ContractType,
    string IndexName,
    IndexChangeKind Kind,
    object Entity,
    object? Key,
    string Id);
