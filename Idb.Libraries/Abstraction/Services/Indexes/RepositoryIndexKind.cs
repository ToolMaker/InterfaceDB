namespace Idb.Libraries.Abstractions.Services.Indexes;

public enum RepositoryIndexKind
{
    None = 0,
    SortedList,
    SortedDictionary,
    HashBucket,
    Custom
}
