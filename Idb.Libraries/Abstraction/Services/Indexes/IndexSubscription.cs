namespace Idb.Libraries.Abstractions.Services.Indexes;

public sealed record IndexSubscription(Type ContractType, string IndexName, object? Key = null);

