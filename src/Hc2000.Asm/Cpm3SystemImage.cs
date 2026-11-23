namespace Hc2000.Asm;

/// <summary>Links the nonbanked CP/M 3 memory image (doc <c>link-layout</c> v3 on hc2000-cpm3-system).
/// The relocated BDOS sits at <c>B200</c>. The BIOS is the absolute image assembled at <c>D300</c>
/// (no page addition; an origin-0 SPR is a different delivery). Each named <c>0xFE</c> high byte becomes
/// <c>0xD09C + xx</c>, both bytes, where <c>xx</c> is the low byte before the mark. Bytes that are not named
/// are not scanned: <c>0xFE</c> is also the Z80 <c>CP</c> opcode.</summary>
public static class Cpm3SystemImage
{
    public const int Size = 0x10000;

    /// <summary>BIOS image origin.</summary>
    public const int BiosBase = 0xD300;

    public const int BiosMaxLength = 0xC00;

    /// <summary>SCB in memory: image offset <c>0x1E9C</c> plus the BDOS load base.</summary>
    public const int ScbBase = 0xD09C;

    /// <summary>A 64K image: the relocated BDOS at <see cref="Cpm3BdosRelocator.LoadBase"/>, the BIOS at
    /// <see cref="BiosBase"/>, and each named high-byte offset rewritten. Page zero and the TPA
    /// stay zero: the BIOS writes page zero at every boot, and <c>CCP.COM</c> is loaded from disk.</summary>
    public static byte[] Link(ReadOnlySpan<byte> bdos3Spr, ReadOnlySpan<byte> biosAtD300, ReadOnlySpan<int> feHighByteOffsets)
    {
        if (biosAtD300.Length > BiosMaxLength)
            throw new ArgumentException($"BIOS image is {biosAtD300.Length} bytes; the link allows {BiosMaxLength} (D300-DEFF).");

        var image = new byte[Size];
        Cpm3BdosRelocator.Relocate(bdos3Spr).CopyTo(image.AsSpan(Cpm3BdosRelocator.LoadBase));
        biosAtD300.CopyTo(image.AsSpan(BiosBase));

        var seen = new HashSet<int>();
        foreach (var offset in feHighByteOffsets)
        {
            if (!seen.Add(offset))
                throw new ArgumentException($"BIOS 0xFE high byte 0x{offset:X} is listed twice.");
            if (offset < 1 || offset >= biosAtD300.Length)
                throw new ArgumentException($"BIOS 0xFE high byte 0x{offset:X} is outside the image.");
            var address = BiosBase + offset;
            if (image[address] != 0xFE)
                throw new ArgumentException($"BIOS offset 0x{offset:X} is 0x{image[address]:X2}, not the named 0xFE high byte.");
            var field = image[address - 1];
            var rewritten = ScbBase + field;
            image[address - 1] = unchecked((byte)rewritten);
            image[address] = unchecked((byte)(rewritten >> 8));
        }
        return image;
    }
}
