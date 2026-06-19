namespace Idb.Libraries.Abstractions.Attributes;

/// <summary>
/// Marks a concrete storage implementation as eligible for retaining object instances in the envelope cache.
/// Envelope metadata remains cached regardless of this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class StorageCachedAttribute : Attribute;
