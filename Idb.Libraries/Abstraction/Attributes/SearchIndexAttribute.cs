using Idb.Libraries.Abstractions.Enums;

namespace Idb.Libraries.Abstractions.Attributes;

[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class SearchIndexAttribute : Attribute
{
    public SearchIndexAttribute(SearchIndexMode mode = SearchIndexMode.Default)
    {
        Mode = mode;
    }

    public SearchIndexMode Mode { get; }
}
