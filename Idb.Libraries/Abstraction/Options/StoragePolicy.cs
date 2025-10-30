using System;

namespace Idb.Libraries.Abstractions.Options;

/// <summary>
/// Defines tunable settings that influence how storage repositories persist payloads.
/// </summary>
public sealed record StoragePolicy
{
    /// <summary>
    /// Singleton instance representing the default policy (sequential writes, notifications enabled).
    /// </summary>
    public static StoragePolicy Default { get; } = new();

    /// <summary>
    /// Maximum number of concurrent write operations. A value of 1 preserves sequential behavior.
    /// Values less than 1 are coerced to 1.
    /// </summary>
    public int MaxConcurrency { get; init; } = 12;

    /// <summary>
    /// When false, change notifications such as <c>ContractAdded</c> are skipped during writes.
    /// </summary>
    public bool RaiseNotifications { get; init; } = true;

    /// <summary>
    /// Buffer size (in bytes) used when streaming payloads to disk.
    /// Values less than or equal to zero fall back to <see cref="DefaultBufferSize"/>.
    /// </summary>
    public int FileWriteBufferSize { get; init; } = DefaultBufferSize;

    /// <summary>
    /// Default buffer size applied when <see cref="FileWriteBufferSize"/> is not specified.
    /// </summary>
    public const int DefaultBufferSize = 8 * 1024;

    internal int NormalizedBufferSize
    {
        get
        {
            var candidate = FileWriteBufferSize;
            return candidate <= 0 ? DefaultBufferSize : candidate;
        }
    }

    internal int NormalizedConcurrency
    {
        get
        {
            var candidate = MaxConcurrency;
            return candidate < 1 ? 1 : candidate;
        }
    }
}
