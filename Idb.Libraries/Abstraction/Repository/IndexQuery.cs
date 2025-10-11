namespace Idb.Libraries.Abstractions.Repository;

public sealed record IndexQuery(Type ContractType, string IndexName, object? Key = null);
