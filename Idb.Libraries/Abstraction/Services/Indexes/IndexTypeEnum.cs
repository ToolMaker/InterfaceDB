namespace Idb.Libraries.Abstractions.Services.Indexes;

/// <summary>
/// Identifies the backing structure for a repository index.
/// </summary>
public enum IndexTypeEnum
{
    /// <summary>
    /// Indexing is disabled for the given configuration entry.
    /// </summary>
    None = 0,

    /// <summary>
    /// Index entries are maintained in a sorted list.
    /// </summary>
    SortedList,

    /// <summary>
    /// Index entries are maintained in a sorted dictionary keyed by an identifier.
    /// </summary>
    SortedDictionary,

    /// <summary>
    /// Index uses hash buckets to partition entries.
    /// </summary>
    HashBucket,

    /// <summary>
    /// Custom index implementation supplied by the consumer.
    /// </summary>
    Custom
}

