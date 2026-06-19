namespace Idb.Libraries.Abstractions.Attributes;

[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class NotSearchIndexedAttribute : Attribute;
