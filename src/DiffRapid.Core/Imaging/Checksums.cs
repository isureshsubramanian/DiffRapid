namespace DiffRapid.Core.Imaging;

/// <summary>The CRC-32 used by PNG chunks and the Adler-32 used by zlib streams.</summary>
internal static class Checksums
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Continues a CRC-32 over more bytes; start with 0xFFFFFFFF and invert the final value.</summary>
    public static uint UpdateCrc32(uint crc, byte[] data, int offset, int count)
    {
        for (var index = offset; index < offset + count; index++)
        {
            crc = CrcTable[(crc ^ data[index]) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    /// <summary>Computes Adler-32 over a whole buffer.</summary>
    public static uint Adler32(byte[] data, int offset, int count)
    {
        const uint Modulus = 65521;
        uint low = 1;
        uint high = 0;
        var index = offset;
        var remaining = count;
        while (remaining > 0)
        {
            // 5552 is the longest run that cannot overflow 32 bits before reducing.
            var run = remaining < 5552 ? remaining : 5552;
            remaining -= run;
            for (var end = index + run; index < end; index++)
            {
                low += data[index];
                high += low;
            }

            low %= Modulus;
            high %= Modulus;
        }

        return (high << 16) | low;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint value = 0; value < 256; value++)
        {
            var crc = value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }

            table[value] = crc;
        }

        return table;
    }
}
