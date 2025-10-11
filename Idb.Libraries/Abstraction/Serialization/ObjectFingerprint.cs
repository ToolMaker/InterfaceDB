namespace Idb.Libraries.Abstractions.Serialization;

public readonly record struct ObjectFingerprint(ulong SchemaHash, ulong PayloadHash)
{
    public bool IsDefault => SchemaHash == 0 && PayloadHash == 0;
}
