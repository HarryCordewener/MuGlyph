namespace SharpMUTerm.Core.Text;

/// <summary>
/// CRC-32 (IEEE 802.3, reflected) over a record payload. Hand-rolled rather than taking a dependency
/// on <c>System.IO.Hashing</c> for twenty lines of table lookup.
/// <para>
/// Shared by the two byte-framed stores in this namespace — <see cref="FileScrollbackSpill"/>'s
/// ephemeral spill segments and <see cref="RestoreLog"/>'s kept per-window logs. Both frame a
/// <see cref="StyledLineCodec"/> payload as <c>length · payload · checksum</c>, and a second copy of
/// this table would be a second thing to keep identical for no gain: a checksum that disagreed
/// between the writer and the reader is indistinguishable from corruption.
/// </para>
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>The CRC-32 of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (var i = 0u; i < 256u; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
