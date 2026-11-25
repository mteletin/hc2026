namespace Hc2000.Asm;

/// <summary>Builds the nonbanked system RAM image. CCP.COM is supplied on the boot drive,
/// not installed into RAM by the linker. Platform initialization belongs to the loader.</summary>
public static class Cpm3SystemBuilder
{
    public const int ResidentBase = 0x7800;
    public const int BufferBase = 0x8000;
    public const string ImageFileName = "CPM3-RAM.BIN";

    public static byte[] Build(string repositoryRoot, ReadOnlySpan<byte> bdos3Spr)
    {
        var upgrade = Path.Combine(repositoryRoot, "bios", "src", "hc2000", "upgrade");
        var target = LibraryTargets.Find("cpm3")!;
        AssemblyResult Assemble(string name)
        {
            var path = Path.Combine(upgrade, name);
            var result = Assembler.Assemble(path, LibraryTargets.Options(path, target));
            if (!result.Success)
                throw new FormatException($"{name}: {string.Join("; ", result.Diagnostics)}");
            return result;
        }

        var bios = Assemble("cpm3.bios.asm");
        var biosBytes = new byte[Cpm3SystemImage.BiosMaxLength];
        Place(bios, biosBytes, Cpm3SystemImage.BiosBase, "BIOS");
        string[] names = ["Date", "Hour", "Min", "Sec"];
        var marks = names.Select(name => Symbol(bios, "ScbWord_" + name) - Cpm3SystemImage.BiosBase + 2).ToArray();

        byte[] image;
        try { image = Cpm3SystemImage.Link(bdos3Spr, biosBytes, marks); }
        catch (ArgumentException ex) { throw new FormatException($"CP/M 3 link: {ex.Message}", ex); }

        var resident = Assemble("cpm3.boot.asm");
        var residentBytes = image.AsSpan(ResidentBase, BufferBase - ResidentBase);
        Place(resident, residentBytes, ResidentBase, "resident BOOT");
        var mark = Symbol(resident, "ScbWord_Mxtpa") - ResidentBase;
        if (mark < 0 || mark > residentBytes.Length - 3 ||
            !residentBytes.Slice(mark, 3).SequenceEqual(new byte[] { 0x2a, 0x62, 0xfe }))
            throw new FormatException("The named resident @MXTPA operand must be LD HL,(FE62h).");
        var mxtpa = Cpm3SystemImage.ScbBase + 0x62;
        residentBytes[mark + 1] = unchecked((byte)mxtpa);
        residentBytes[mark + 2] = (byte)(mxtpa >> 8);

        Place(Assemble("cpm3.buf.asm"), image.AsSpan(BufferBase, Cpm3BdosRelocator.LoadBase - BufferBase),
            BufferBase, "buffers");
        return image;
    }

    private static int Symbol(AssemblyResult result, string name) => result.Symbols.TryGetValue(name, out var address)
        ? address : throw new FormatException($"Missing CP/M 3 link symbol {name}.");

    private static void Place(AssemblyResult result, Span<byte> region, int origin, string name)
    {
        var end = origin;
        foreach (var segment in result.Segments.OrderBy(segment => segment.Address))
        {
            var offset = segment.Address - origin;
            if (segment.Address < end || (long)offset + segment.Bytes.Length > region.Length)
                throw new FormatException($"CP/M 3 {name} segment at {segment.Address:X4} overlaps or leaves its region.");
            segment.Bytes.CopyTo(region[offset..]);
            end = segment.Address + segment.Bytes.Length;
        }
        if (result.Segments.Count == 0 || result.Segments.Min(segment => segment.Address) != origin)
            throw new FormatException($"CP/M 3 {name} must start at {origin:X4}.");
    }
}
