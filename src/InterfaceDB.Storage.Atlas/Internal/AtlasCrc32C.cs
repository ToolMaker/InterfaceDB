using System.Buffers.Binary;
using System.Runtime.Intrinsics.X86;
using ArmCrc32 = System.Runtime.Intrinsics.Arm.Crc32;

namespace Idb.Storage.AtlasCore.Internal;

internal static class AtlasCrc32C
{
    private const uint Initial = uint.MaxValue;
    private const uint ReflectedPolynomial = 0x82F63B78u;
    private static readonly uint[] Table = CreateTable();

    public static uint Compute(ReadOnlySpan<byte> source)
    {
        var crc = Initial;
        var offset = 0;
        if (Sse42.IsSupported)
        {
            if (Sse42.X64.IsSupported)
            {
                var wide = (ulong)crc;
                while (source.Length - offset >= sizeof(ulong))
                {
                    wide = Sse42.X64.Crc32(
                        wide,
                        BinaryPrimitives.ReadUInt64LittleEndian(source[offset..]));
                    offset += sizeof(ulong);
                }

                crc = (uint)wide;
            }

            while (source.Length - offset >= sizeof(uint))
            {
                crc = Sse42.Crc32(crc, BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]));
                offset += sizeof(uint);
            }

            while (offset < source.Length)
            {
                crc = Sse42.Crc32(crc, source[offset++]);
            }

            return ~crc;
        }

        if (ArmCrc32.IsSupported)
        {
            if (ArmCrc32.Arm64.IsSupported)
            {
                while (source.Length - offset >= sizeof(ulong))
                {
                    crc = ArmCrc32.Arm64.ComputeCrc32C(
                        crc,
                        BinaryPrimitives.ReadUInt64LittleEndian(source[offset..]));
                    offset += sizeof(ulong);
                }
            }

            while (source.Length - offset >= sizeof(uint))
            {
                crc = ArmCrc32.ComputeCrc32C(crc, BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]));
                offset += sizeof(uint);
            }

            while (offset < source.Length)
            {
                crc = ArmCrc32.ComputeCrc32C(crc, source[offset++]);
            }

            return ~crc;
        }

        return ComputePortable(source);
    }

    internal static uint ComputePortable(ReadOnlySpan<byte> source)
    {
        var crc = Initial;
        foreach (var value in source)
        {
            crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 0
                    ? value >> 1
                    : (value >> 1) ^ ReflectedPolynomial;
            }

            table[index] = value;
        }

        return table;
    }
}
