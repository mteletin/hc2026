using System.Security.Cryptography;

namespace Hc2000.Machine.Storage;

public sealed record RomSlice(string Name, int Offset, byte[] Data);
public sealed class RomTools
{
    public IReadOnlyList<string> Identify(ReadOnlySpan<byte> bytes, string manifest)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return manifest.Split('\n').Where(l => l.Length > 66 && l[64..66] == "  " && l[..64].Equals(hash, StringComparison.OrdinalIgnoreCase))
            .Select(l => l[66..].TrimEnd('\r')).ToArray();
    }
    public IReadOnlyList<RomSlice> Split(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 65536) throw new InvalidDataException("M27C512 dump must be exactly 64 KiB.");
        // Exact board dump map from roms/README.md. Similar is not an identity claim.
        return new[]
        {
            new RomSlice("if1-old.bin", 0, bytes[..0x4000].ToArray()),
            new RomSlice("if1-v1-like.bin", 0x4000, bytes.Slice(0x4000,0x4000).ToArray()),
            new RomSlice("basic-v2.bin", 0x8000, bytes.Slice(0x8000,0x4000).ToArray()),
            new RomSlice("cpm-v1-like.bin", 0xc000, bytes.Slice(0xc000,0x2000).ToArray()),
            new RomSlice("cpm-v2-hc2kzip.bin", 0xe000, bytes[0xe000..].ToArray())
        };
    }
}
