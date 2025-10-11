namespace Idb.Libraries.Abstractions.Repository;

public interface IRepositoryIndexConfiguration
{
    Type ContractType { get; }
    string Name { get; }
    RepositoryIndexKind Kind { get; }
    IReadOnlyDictionary<string, object?> Settings { get; }
}
