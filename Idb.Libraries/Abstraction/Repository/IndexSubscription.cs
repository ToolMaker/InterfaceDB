namespace Idb.Libraries.Abstractions.Repository;

public sealed record IndexSubscription(Type ContractType, string IndexName, object? Key = null);
