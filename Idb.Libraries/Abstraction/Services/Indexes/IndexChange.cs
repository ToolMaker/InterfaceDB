namespace Idb.Libraries.Abstractions.Services.Indexes;

public sealed record IndexChange(
    Type ContractType,
    string IndexName,
    IndexChangeKind Kind,
    object Entity,
    object? Key,
    string Id);
