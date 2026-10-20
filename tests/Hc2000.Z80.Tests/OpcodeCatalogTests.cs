using Xunit;

namespace Hc2000.Z80.Tests;

public class OpcodeCatalogTests
{
    [Fact]
    public void EveryConcreteKeyIsUniqueAndEveryPatternHasOneCanonicalEncoding()
    {
        var table = new Opcodes();
        Assert.Equal(1780, table.Entries.Count);
        Assert.Equal(table.Entries.Count, table.Entries.Select(e => e.DecoderKey).Distinct().Count());
        foreach (var group in table.Entries.GroupBy(e => e.Pattern))
            Assert.Single(group, e => e.CanonicalForAssembly);
        foreach (var family in Enum.GetValues<PrefixFamily>())
            Assert.Equal(family is PrefixFamily.Base or PrefixFamily.DD or PrefixFamily.FD ? 252 : 256,
                table.Entries.Count(e => e.DecoderKey.PrefixFamily == family));
    }

    [Fact]
    public void DenseLookupReturnsTheSameImmutableEntriesAndRejectsInvalidKeys()
    {
        var catalog = new Opcodes();
        foreach (var entry in catalog.Entries)
        {
            Assert.Same(entry, catalog[entry.DecoderKey]);
            Assert.True(catalog.TryGet(entry.DecoderKey, out var found));
            Assert.Same(entry, found);
        }
        foreach (var key in new[] { new DecoderKey((PrefixFamily)(-1), 0), new DecoderKey((PrefixFamily)7, 0), new DecoderKey(PrefixFamily.Base, 0xcb) })
        {
            Assert.False(catalog.TryGet(key, out var absent)); Assert.Null(absent);
            Assert.Throws<KeyNotFoundException>(() => catalog[key]);
        }
    }

    [Fact]
    public void SlotsAreDisjointZeroFilledAndWithinTheirTemplates()
    {
        foreach (var e in new Opcodes().Entries)
        {
            var occupied = new HashSet<int>();
            foreach (var slot in e.Operands)
            {
                Assert.Contains(slot.PatternToken, e.Pattern);
                var width = slot.Kind == OperandKind.WordLE ? 2 : 1;
                for (var i = slot.ByteOffset; i < slot.ByteOffset + width; i++)
                {
                    Assert.InRange(i, 0, e.Length - 1);
                    Assert.True(occupied.Add(i), $"Overlapping slot in {e.Pattern}");
                    Assert.Equal((byte)0, e.Encoding[i]);
                }
            }
        }
    }

    [Theory]
    [InlineData(PrefixFamily.Base, 0x00, "NOP", "00", "", true)]
    [InlineData(PrefixFamily.Base, 0x01, "LD BC,nn", "010000", "WordLE:1:nn", true)]
    [InlineData(PrefixFamily.Base, 0x18, "JR e", "1800", "Relative:1:e", true)]
    [InlineData(PrefixFamily.Base, 0x20, "JR NZ,e", "2000", "Relative:1:e", true)]
    [InlineData(PrefixFamily.Base, 0xd3, "OUT (n),A", "D300", "Byte:1:n", true)]
    [InlineData(PrefixFamily.Base, 0xff, "RST 38h", "FF", "", true)]
    [InlineData(PrefixFamily.CB, 0x30, "SLL B", "CB30", "", true)]
    [InlineData(PrefixFamily.CB, 0x5e, "BIT 3,(HL)", "CB5E", "", true)]
    [InlineData(PrefixFamily.DD, 0x36, "LD (IX+d),n", "DD360000", "Displacement:2:d,Byte:3:n", true)]
    [InlineData(PrefixFamily.FD, 0x21, "LD IY,nn", "FD210000", "WordLE:2:nn", true)]
    [InlineData(PrefixFamily.DD, 0x66, "LD H,(IX+d)", "DD6600", "Displacement:2:d", true)]
    [InlineData(PrefixFamily.DD, 0x74, "LD (IX+d),H", "DD7400", "Displacement:2:d", true)]
    [InlineData(PrefixFamily.FD, 0x65, "LD IYH,IYL", "FD65", "", true)]
    [InlineData(PrefixFamily.DD, 0xeb, "EX DE,HL", "DDEB", "", false)]
    [InlineData(PrefixFamily.DD, 0x76, "HALT", "DD76", "", false)]
    [InlineData(PrefixFamily.DD, 0x18, "JR e", "DD1800", "Relative:2:e", false)]
    [InlineData(PrefixFamily.DDCB, 0x00, "RLC (IX+d),B", "DDCB0000", "Displacement:2:d", true)]
    [InlineData(PrefixFamily.FDCB, 0x86, "RES 0,(IY+d)", "FDCB0086", "Displacement:2:d", true)]
    [InlineData(PrefixFamily.DDCB, 0x40, "BIT 0,(IX+d)", "DDCB0040", "Displacement:2:d", false)]
    [InlineData(PrefixFamily.DDCB, 0x46, "BIT 0,(IX+d)", "DDCB0046", "Displacement:2:d", true)]
    [InlineData(PrefixFamily.ED, 0x00, "NOP", "ED00", "", false)]
    [InlineData(PrefixFamily.ED, 0x44, "NEG", "ED44", "", true)]
    [InlineData(PrefixFamily.ED, 0x4c, "NEG", "ED4C", "", false)]
    [InlineData(PrefixFamily.ED, 0x4d, "RETI", "ED4D", "", true)]
    [InlineData(PrefixFamily.ED, 0x55, "RETN", "ED55", "", false)]
    [InlineData(PrefixFamily.ED, 0x63, "LD (nn),HL", "ED630000", "WordLE:2:nn", false)]
    [InlineData(PrefixFamily.ED, 0x70, "IN F,(C)", "ED70", "", true)]
    [InlineData(PrefixFamily.ED, 0x71, "OUT (C),0", "ED71", "", true)]
    [InlineData(PrefixFamily.ED, 0xb3, "OTIR", "EDB3", "", true)]
    public void IndependentEncodingExamples(PrefixFamily family, int opcode, string pattern, string hex, string slots, bool canonical)
    {
        var table = new Opcodes();
        var key = new DecoderKey(family, (byte)opcode);
        Assert.True(table.TryGet(key, out var found));
        var entry = table[key];
        Assert.Same(entry, found);
        Assert.Equal(pattern, entry.Pattern);
        Assert.Equal(hex, Convert.ToHexString(entry.Encoding.ToArray()));
        Assert.Equal(slots, string.Join(',', entry.Operands.Select(s => $"{s.Kind}:{s.ByteOffset}:{s.PatternToken}")));
        Assert.Equal(canonical, entry.CanonicalForAssembly);
    }

    [Fact]
    public void PrefixBytesAreNotCompleteBaseInstructions()
    {
        var table = new Opcodes();
        foreach (var prefix in new byte[] { 0xcb, 0xdd, 0xed, 0xfd })
            Assert.False(table.TryGet(new DecoderKey(PrefixFamily.Base, prefix), out _));
    }

    [Fact]
    public void CollectionsCannotBeMutatedAndInstancesHaveIndependentEntries()
    {
        var first = new Opcodes();
        var second = new Opcodes();
        var key = new DecoderKey(PrefixFamily.Base, 0x01);
        Assert.NotSame(first[key], second[key]);
        Assert.Throws<NotSupportedException>(() => ((IList<byte>)first[key].Encoding)[0] = 0xff);
        Assert.Throws<NotSupportedException>(() => ((IList<OperandSlot>)first[key].Operands).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<OpcodeEntry>)first.Entries).Clear());
    }
}
