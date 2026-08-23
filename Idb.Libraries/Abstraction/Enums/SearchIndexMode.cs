namespace Idb.Libraries.Abstractions.Enums;

[Flags]
public enum SearchIndexMode
{
    Default = 0,
    Exact = 1,
    Range = 2,
    Prefix = 4,
    Suffix = 8,
    Contains = 16,
    All = Exact | Range | Prefix | Suffix | Contains
}
