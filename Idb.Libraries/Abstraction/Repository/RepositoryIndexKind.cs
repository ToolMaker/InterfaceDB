namespace Idb.Libraries.Abstractions.Repository;

public enum RepositoryIndexKind
{
    None = 0,
    SortedList,
    SortedDictionary,
    HashBucket,
    Custom
}
