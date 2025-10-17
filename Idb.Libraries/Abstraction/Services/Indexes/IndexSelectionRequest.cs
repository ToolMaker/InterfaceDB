namespace Idb.Libraries.Abstractions.Services.Indexes;

/// <summary>
/// Describes the context required to select an index for a repository operation.
/// </summary>
/// <param name="ContractType">The storage contract type.</param>
/// <param name="Probe">The object providing candidate values for index members.</param>
/// <param name="Operation">The intended operation (read/write).</param>
/// <param name="PreferredIndex">An optional preferred index name.</param>
public sealed record IndexSelectionRequest(
    Type ContractType,
    object? Probe,
    IndexOperationKind Operation = IndexOperationKind.Read,
    string? PreferredIndex = null);

