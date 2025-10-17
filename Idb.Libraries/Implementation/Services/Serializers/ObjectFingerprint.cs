namespace Idb.Libraries.Abstractions.Services.Serialization;

public readonly record struct ObjectFingerprint(ulong SchemaHash, ulong PayloadHash)
{
    public bool IsDefault => SchemaHash == 0 && PayloadHash == 0;
}

