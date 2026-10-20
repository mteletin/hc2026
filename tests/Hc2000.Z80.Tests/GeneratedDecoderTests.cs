using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Hc2000.Z80.Tests;

public class GeneratedDecoderTests
{
    [Fact]
    public void GeneratedKeyFactoryMatchesEveryConcreteEntryAndCatalogSnapshot()
    {
        var cpu = new Z80(new Bus());
        var catalog = new Opcodes();
        var entries = catalog.Entries.OrderBy(e => e.DecoderKey.PrefixFamily).ThenBy(e => e.DecoderKey.Opcode).ToArray();
        foreach (var entry in entries)
            Assert.Equal(entry.DecoderKey, cpu.DecodeExecutionKey(entry.DecoderKey.PrefixFamily, entry.DecoderKey.Opcode));
        var signature = string.Join('\n', entries.Select(e => $"{e.DecoderKey.PrefixFamily}|{e.DecoderKey.Opcode:X2}|{e.Pattern}|{Convert.ToHexString(e.Encoding.ToArray())}|{e.CanonicalForAssembly}|{string.Join(';', e.Operands.Select(s => $"{s.Kind}:{s.ByteOffset}:{s.PatternToken}"))}"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
        Assert.True(hash == Z80.ExecutionCatalogSha256,
            "Catalog changed: regenerate with dotnet run --project tools/Z80DecoderGenerator -c Release -- src/Hc2000.Z80/Z80.Decoder.g.cs");
        var baseEntries = entries.Where(e => e.DecoderKey.PrefixFamily == PrefixFamily.Base).ToArray();
        Assert.Equal(252, baseEntries.Length);
        Assert.True(string.Join(',', baseEntries.Select(e => e.DecoderKey.Opcode.ToString("X2"))) == Z80.GeneratedBaseOpcodes);
        Assert.DoesNotContain(baseEntries, e => e.DecoderKey.Opcode is 0xcb or 0xdd or 0xed or 0xfd);
        var nop = catalog[new DecoderKey(PrefixFamily.Base, 0)];
        Assert.Equal("NOP", nop.Pattern); Assert.Equal(1, nop.Length); Assert.Empty(nop.Operands);
    }

    private sealed class Bus : IBus
    {
        public byte Read(ushort address) => 0;
        public void Write(ushort address, byte value) { }
        public byte In(ushort port) => 0xff;
        public void Out(ushort port, byte value) { }
        public void Contend(ushort address, int count) { }
        public byte IntVector() => 0xff;
    }
}
