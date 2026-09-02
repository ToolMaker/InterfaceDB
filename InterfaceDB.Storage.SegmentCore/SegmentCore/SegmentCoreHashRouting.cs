using System.Buffers;
using System.Globalization;
using System.IO.Hashing;
using System.Runtime.InteropServices;

namespace Idb.Storage.SegmentCore;

/// <summary>
/// Selects the stable, non-cryptographic hash used to place object keys across
/// SegmentCore's append stripes. The selected algorithm is part of the durable
/// store format and cannot be changed after store creation without migration.
/// </summary>
public enum SegmentCoreHashAlgorithm
{
    /// <summary>
    /// The original SegmentCore FNV-1a 32-bit hash over canonical UTF-16LE bytes.
    /// </summary>
    Fnv1a32 = 0,

    /// <summary>
    /// FNV-1a with a 64-bit accumulator over canonical UTF-16LE bytes.
    /// </summary>
    Fnv1a64 = 1,

    /// <summary>
    /// The 64-bit xxHash algorithm over canonical UTF-16LE bytes.
    /// </summary>
    XxHash64 = 2,

    /// <summary>
    /// The XXH3 64-bit algorithm over canonical UTF-16LE bytes.
    /// </summary>
    XxHash3 = 3
}

internal readonly record struct SegmentCoreHashRoutingDescriptor(
    SegmentCoreHashAlgorithm Algorithm,
    long Seed)
{
    public string RouterId => SegmentCoreHashRouter.GetRouterId(Algorithm, Seed);
}

internal static class SegmentCoreHashRouter
{
    private const uint Fnv32Offset = 2166136261u;
    private const uint Fnv32Prime = 16777619u;
    private const ulong Fnv64Offset = 14695981039346656037ul;
    private const ulong Fnv64Prime = 1099511628211ul;
    private const int StackByteLimit = 512;

    private const string Fnv1a32Prefix = "fnv1a32-u16le-hash-v1-s";
    private const string Fnv1a64Prefix = "fnv1a64-u16le-hash-v1-s";
    private const string XxHash64Prefix = "xxhash64-u16le-hash-v1-s";
    private const string XxHash3Prefix = "xxhash3-u16le-hash-v1-s";

    public static int SelectStripe(
        string key,
        int stripeCount,
        SegmentCoreHashAlgorithm algorithm,
        long seed)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (stripeCount is < 1 or > 64
            || !System.Numerics.BitOperations.IsPow2((uint)stripeCount))
        {
            throw new ArgumentOutOfRangeException(nameof(stripeCount));
        }

        var hash = ComputeHash(key, algorithm, seed);
        return (int)(hash & (uint)(stripeCount - 1));
    }

    public static ulong ComputeHash(
        string key,
        SegmentCoreHashAlgorithm algorithm,
        long seed)
    {
        ArgumentNullException.ThrowIfNull(key);
        return algorithm switch
        {
            SegmentCoreHashAlgorithm.Fnv1a32 => ComputeFnv1a32(key, seed),
            SegmentCoreHashAlgorithm.Fnv1a64 => ComputeFnv1a64(key, seed),
            SegmentCoreHashAlgorithm.XxHash64 => ComputeXxHash(key, seed, useXxHash3: false),
            SegmentCoreHashAlgorithm.XxHash3 => ComputeXxHash(key, seed, useXxHash3: true),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
        };
    }

    public static string GetRouterId(SegmentCoreHashAlgorithm algorithm, long seed)
    {
        if (algorithm == SegmentCoreHashAlgorithm.Fnv1a32 && seed == 0)
        {
            return SegmentCoreStorageFormat.RouterId;
        }

        var prefix = algorithm switch
        {
            SegmentCoreHashAlgorithm.Fnv1a32 => Fnv1a32Prefix,
            SegmentCoreHashAlgorithm.Fnv1a64 => Fnv1a64Prefix,
            SegmentCoreHashAlgorithm.XxHash64 => XxHash64Prefix,
            SegmentCoreHashAlgorithm.XxHash3 => XxHash3Prefix,
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
        };
        return prefix + unchecked((ulong)seed).ToString("X16", CultureInfo.InvariantCulture);
    }

    public static bool TryParseRouterId(
        string routerId,
        out SegmentCoreHashRoutingDescriptor descriptor)
    {
        if (string.Equals(routerId, SegmentCoreStorageFormat.RouterId, StringComparison.Ordinal))
        {
            descriptor = new SegmentCoreHashRoutingDescriptor(SegmentCoreHashAlgorithm.Fnv1a32, 0);
            return true;
        }

        if (TryParseSeededRouter(routerId, Fnv1a32Prefix, SegmentCoreHashAlgorithm.Fnv1a32, out descriptor)
            || TryParseSeededRouter(routerId, Fnv1a64Prefix, SegmentCoreHashAlgorithm.Fnv1a64, out descriptor)
            || TryParseSeededRouter(routerId, XxHash64Prefix, SegmentCoreHashAlgorithm.XxHash64, out descriptor)
            || TryParseSeededRouter(routerId, XxHash3Prefix, SegmentCoreHashAlgorithm.XxHash3, out descriptor))
        {
            return true;
        }

        descriptor = default;
        return false;
    }

    private static bool TryParseSeededRouter(
        string routerId,
        string prefix,
        SegmentCoreHashAlgorithm algorithm,
        out SegmentCoreHashRoutingDescriptor descriptor)
    {
        if (routerId.Length == prefix.Length + 16
            && routerId.StartsWith(prefix, StringComparison.Ordinal)
            && ulong.TryParse(
                routerId.AsSpan(prefix.Length),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var seed))
        {
            descriptor = new SegmentCoreHashRoutingDescriptor(algorithm, unchecked((long)seed));
            return true;
        }

        descriptor = default;
        return false;
    }

    private static uint ComputeFnv1a32(string key, long seed)
    {
        var hash = Fnv32Offset;
        if (seed != 0)
        {
            var seedBits = unchecked((ulong)seed);
            for (var index = 0; index < sizeof(long); index++)
            {
                hash = (hash ^ (byte)seedBits) * Fnv32Prime;
                seedBits >>= 8;
            }
        }

        foreach (var character in key)
        {
            hash = (hash ^ (byte)character) * Fnv32Prime;
            hash = (hash ^ (byte)(character >> 8)) * Fnv32Prime;
        }

        return hash;
    }

    private static ulong ComputeFnv1a64(string key, long seed)
    {
        var hash = Fnv64Offset;
        if (seed != 0)
        {
            var seedBits = unchecked((ulong)seed);
            for (var index = 0; index < sizeof(long); index++)
            {
                hash = (hash ^ (byte)seedBits) * Fnv64Prime;
                seedBits >>= 8;
            }
        }

        foreach (var character in key)
        {
            hash = (hash ^ (byte)character) * Fnv64Prime;
            hash = (hash ^ (byte)(character >> 8)) * Fnv64Prime;
        }

        return hash;
    }

    private static ulong ComputeXxHash(string key, long seed, bool useXxHash3)
    {
        if (BitConverter.IsLittleEndian)
        {
            var bytes = MemoryMarshal.AsBytes(key.AsSpan());
            return useXxHash3
                ? XxHash3.HashToUInt64(bytes, seed)
                : XxHash64.HashToUInt64(bytes, seed);
        }

        var byteLength = checked(key.Length * sizeof(char));
        byte[]? rented = null;
        Span<byte> canonical = byteLength <= StackByteLimit
            ? stackalloc byte[byteLength]
            : (rented = ArrayPool<byte>.Shared.Rent(byteLength)).AsSpan(0, byteLength);
        try
        {
            for (var index = 0; index < key.Length; index++)
            {
                var character = key[index];
                canonical[index * 2] = (byte)character;
                canonical[(index * 2) + 1] = (byte)(character >> 8);
            }

            return useXxHash3
                ? XxHash3.HashToUInt64(canonical, seed)
                : XxHash64.HashToUInt64(canonical, seed);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
            }
        }
    }
}
