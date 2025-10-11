using System;

namespace Idb.Libraries.Abstractions.Attributes;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class HashIgnoreAttribute : Attribute;
