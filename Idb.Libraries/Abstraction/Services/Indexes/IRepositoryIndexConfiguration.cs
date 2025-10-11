namespace Idb.Libraries.Abstractions.Services.Indexes;

public interface IRepositoryIndexConfiguration
{
    Type ContractType { get; }
    string Name { get; }
    RepositoryIndexKind Kind { get; }
    IReadOnlyDictionary<string, object?> Settings { get; }
}
