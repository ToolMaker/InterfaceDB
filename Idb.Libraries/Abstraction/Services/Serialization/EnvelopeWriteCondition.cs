namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>Explicit infrastructure semantics. Every mode conditionally checks the destination's expected revision.</summary>
public enum EnvelopeRevisionAction { Advance, Import, MetadataOnly }

public sealed record EnvelopeWriteCondition(long ExpectedRevision, EnvelopeRevisionAction Action = EnvelopeRevisionAction.Advance,
    long SourceRevision = 0, byte[]? ExpectedExtensionMetadata = null, bool ExpectedMissing = false);
