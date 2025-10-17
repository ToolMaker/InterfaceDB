namespace Idb.Libraries.Abstractions.Attributes;

/// <summary>
/// Marks a member that should be excluded from object fingerprint calculations.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true, AllowMultiple = false)]
public sealed class HashIgnoreAttribute : Attribute;
