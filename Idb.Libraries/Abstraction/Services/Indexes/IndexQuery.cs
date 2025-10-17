namespace Idb.Libraries.Abstractions.Services.Indexes;

public sealed record IndexQuery(Type ContractType, string? IndexName, object? Key = null);

