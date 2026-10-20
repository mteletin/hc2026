using Hc2000.Machine.Memory;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class MemoryRomAssetTests
{
    [MemoryRomAssetsTheory]
    [InlineData(HcRomLayout.V1)] [InlineData(HcRomLayout.V2)]
    [Trait("hardware", "unverified")]
    public void SuppliedRomBytesMapWithoutSubstitutionV2LowerHalfIsExplicitlySynthetic(HcRomLayout layout)
    {
        var folder = Path.Combine(StorageAssetTests.Root(), "roms", "hc2000", layout == HcRomLayout.V1 ? "v1" : "v2");
        var basic = File.ReadAllBytes(Path.Combine(folder, "basic.rom"));
        var if1 = File.ReadAllBytes(Path.Combine(folder, "if1.rom"));
        byte[] cpm;
        string provenance;
        if (layout == HcRomLayout.V1)
        {
            cpm = File.ReadAllBytes(Path.Combine(folder, "cpm.rom"));
            provenance = "repo hc2000/v1 full supplied BASIC/CPM/IF1 images; not boot-verified";
        }
        else
        {
            var upper = File.ReadAllBytes(Path.Combine(folder, "cpm-hc2kzip.bin"));
            Assert.Equal(0x2000, upper.Length);
            cpm = Enumerable.Repeat((byte)0xff, 0x4000).ToArray();
            upper.CopyTo(cpm, 0x2000);
            provenance = "repo hc2000/v2 BASIC/IF1 and canonical upper CPM; SYNTHETIC FF lower CPM (unverified hardware)";
        }
        var memory = new HcMemory(new(layout, RomProvenance: provenance), basic, cpm, if1);
        Assert.Equal(provenance, memory.Profile.RomProvenance);
        for (var address = 0; address < 0x4000; address++) Assert.Equal(basic[address], memory.Read((ushort)address));
        memory.WritePort7E(1);
        for (var address = 0; address < 0x4000; address++) Assert.Equal(cpm[address], memory.Read((ushort)address));
        memory.WritePort7E(3);
        for (var address = 0xe000; address < 0x10000; address++) Assert.Equal(cpm[address - 0xc000], memory.Read((ushort)address));
        memory.WritePort7E(0); memory.SetShadow(true);
        for (var address = 0; address < 0x4000; address++)
            if (address is not (>= 0x2800 and < 0x3000 or >= 0x3800 and < 0x4000))
                Assert.Equal(if1[address], memory.Read((ushort)address));
    }
}

public sealed class MemoryRomAssetsTheoryAttribute : TheoryAttribute
{
    public MemoryRomAssetsTheoryAttribute()
    {
        var root = Path.Combine(StorageAssetTests.Root(), "roms", "hc2000");
        foreach (var version in new[] { "v1", "v2" })
        foreach (var file in new[] { "basic.rom", "if1.rom", version == "v1" ? "cpm.rom" : "cpm-hc2kzip.bin" })
        {
            var path = Path.Combine(root, version, file);
            var expectedBytes = file == "cpm-hc2kzip.bin" ? 0x2000 : 0x4000;
            if (!File.Exists(path) || new FileInfo(path).Length != expectedBytes)
                Skip = "Copyrighted ROM fixture absent or not LFS-materialized: " + version + "/" + file;
        }
    }
}
