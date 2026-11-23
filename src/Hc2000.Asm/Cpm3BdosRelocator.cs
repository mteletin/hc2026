namespace Hc2000.Asm;

/// <summary>Relocates DRI's nonbanked <c>BDOS3.SPR</c> onto the load base the link layout fixes
/// (docs <c>spr-format</c> v3 and <c>link-layout</c> v3 on hc2000-cpm3-system). The mark bitmap is MSB-first,
/// the same order <see cref="SprBuilder"/> writes: bit <c>0x80 &gt;&gt; (index &amp; 7)</c> of byte <c>index &gt;&gt; 3</c>
/// marks image offset <c>index</c>, and a mark means the high byte of an address.</summary>
public static class Cpm3BdosRelocator
{
    /// <summary>Where the relocated image sits. <c>B200 + 0x20E7 = D2E7</c>, just below the BIOS at <c>D300</c>.</summary>
    public const int LoadBase = 0xB200;

    /// <summary>The BIOS jump-vector page the 47 marked <c>0xFF</c> words are rewritten to.</summary>
    public const int BiosVectorPage = 0xD3;

    public const int ImageLength = 0x20E7;
    public const int ExpectedMarks = 1597;
    public const int ExpectedVectorWords = 47;

    /// <summary>Image offset of the <c>@MXTPA</c> word. The SPR holds the placeholder <c>0006</c>; the build writes the
    /// BDOS entry, because the BIOS copies this word to page zero 6.</summary>
    public const int MxtpaOffset = 0x1EFE;

    /// <summary>The dispatcher is at image offset 6, so the entry in memory is the load base plus 6. The offset is the
    /// SPR's own: the <c>@MXTPA</c> placeholder is 0006, and the entry sequence starts at offset 6 (the canonical
    /// XCHG / SHLD / XCHG / MOV A,C, which enters with the caller's return address on top). Offset 5 holds the last
    /// byte of the serial-number area; entering there pushes an extra word the functions never pop, so a function
    /// returns through it instead of its caller.</summary>
    public const int BdosEntry = LoadBase + 6;

    /// <summary>The origin-0 image of <paramref name="spr"/> relocated to <see cref="LoadBase"/>: the 47 marked
    /// <c>0xFF</c> high bytes become <see cref="BiosVectorPage"/>, every other marked byte gains the load page, and
    /// the <c>@MXTPA</c> word is the BDOS entry. Unmarked bytes are copied. Refuses an image that is not the pinned
    /// nonbanked BDOS, including one that marks an <c>0xFE</c> byte: those are SCB references and belong to the BIOS.</summary>
    public static byte[] Relocate(ReadOnlySpan<byte> spr)
    {
        if (spr.Length < 258)
            throw new ArgumentException("BDOS3.SPR is shorter than its header.");
        var length = spr[1] | (spr[2] << 8);
        if (length != ImageLength)
            throw new ArgumentException($"BDOS3.SPR image length is 0x{length:X4}; expected 0x{ImageLength:X4}.");
        var bitmapBytes = (length + 7) / 8;
        if (spr.Length < 256 + length + bitmapBytes)
            throw new ArgumentException("BDOS3.SPR has no mark bitmap for its image.");

        var image = spr.Slice(256, length).ToArray();
        var bitmap = spr.Slice(256 + length, bitmapBytes).ToArray();
        bool Marked(int index) => (bitmap[index >> 3] & (0x80 >> (index & 7))) != 0;

        if (image[MxtpaOffset] != 0x06 || image[MxtpaOffset + 1] != 0x00
            || Marked(MxtpaOffset) || !Marked(MxtpaOffset + 1))
            throw new ArgumentException("@MXTPA is not the placeholder word 0006 with its high byte marked.");

        var marks = 0;
        var vectors = 0;
        for (var index = 0; index < length; index++)
        {
            if (!Marked(index)) continue;
            marks++;
            if (image[index] == 0xFE)
                throw new ArgumentException($"BDOS3.SPR marks an 0xFE byte at 0x{index:X4}; the BDOS has no SCB references.");
            if (image[index] == 0xFF)
            {
                // The marked byte is the high byte; the low byte is the one before it, a BIOS vector offset.
                if (index == 0 || Marked(index - 1) || image[index - 1] % 3 != 0)
                    throw new ArgumentException($"Marked 0xFF byte at 0x{index:X4} is not a BIOS vector word.");
                image[index] = BiosVectorPage;
                vectors++;
                continue;
            }
            image[index] = unchecked((byte)(image[index] + (LoadBase >> 8)));
        }
        if (marks != ExpectedMarks || vectors != ExpectedVectorWords)
            throw new ArgumentException(
                $"BDOS3.SPR has {marks} marks and {vectors} vector words; expected {ExpectedMarks} and {ExpectedVectorWords}.");

        // Page addition turned the placeholder 0006 into B206: the entry the BIOS stores at page zero 6. The word is
        // rewritten to make it explicit that the build wrote it, not the page addition.
        image[MxtpaOffset] = unchecked((byte)BdosEntry);
        image[MxtpaOffset + 1] = unchecked((byte)(BdosEntry >> 8));
        return image;
    }
}
