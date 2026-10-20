using Hc2000.Machine.Memory;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class MachineProfileTests
{
    [Theory]
    [InlineData("hc2000-v2", HcRomLayout.V2, "v2", "cpm-hc2kzip.bin", 0x2000, 0x2000, false)]
    [InlineData("hc2000-v1", HcRomLayout.V1, "v1", "cpm.rom", 0, 0x4000, true)]
    public void FaithfulProfilesPinExistingDefaults(string name, HcRomLayout layout, string folder,
        string cpm, int offset, int bytes, bool complete)
    {
        var profile = name == "hc2000-v2" ? MachineProfile.Hc2000V2 : MachineProfile.Hc2000V1;
        profile.Validate();
        Assert.Equal(name, profile.Name);
        Assert.Equal(1, profile.Version);
        Assert.Equal(layout, profile.Memory.RomLayout);
        Assert.Equal(0x10000, profile.Memory.RamBytes);
        Assert.Equal(0x8000, profile.Memory.RomBytes);
        Assert.Equal(0x4000, profile.Memory.ShadowRomBytes);
        Assert.Equal(0x400, profile.Memory.ShadowRamBytes);
        Assert.Equal("unverified supplied images", profile.Memory.RomProvenance);
        Assert.Equal(MachineIoPolicy.FaithfulOddFdcV1, profile.Io);
        Assert.Equal(17_734_475, profile.MasterHz);
        Assert.Equal(5, profile.CpuMasterTicksNumerator);
        Assert.Equal(1, profile.CpuMasterTicksDenominator);
        Assert.Equal(17_734_475, profile.Video.FlashPeriodNumerator);
        Assert.Equal(2, profile.Video.FlashPeriodDenominator);
        Assert.False(profile.Video.FullRaster);
        Assert.Equal(new uint[] {
            0xff000000, 0xff0000cd, 0xffcd0000, 0xffcd00cd,
            0xff00cd00, 0xff00cdcd, 0xffcdcd00, 0xffcdcdcd,
            0xff000000, 0xff0000ff, 0xffff0000, 0xffff00ff,
            0xff00ff00, 0xff00ffff, 0xffffff00, 0xffffffff
        }, profile.Video.Palette);
        ProfileRomRegion[] expected = [
            new(ProfileRomRole.Basic, $"roms/hc2000/{folder}/basic.rom", 0x4000, 0, ProfileRomStore.MainRom, 0, ProfileRomAvailability.SuppliedUnverified),
            new(complete ? ProfileRomRole.Cpm : ProfileRomRole.CpmUpper, $"roms/hc2000/{folder}/{cpm}", bytes, 0, ProfileRomStore.MainRom, 0x4000 + offset, ProfileRomAvailability.SuppliedUnverified),
            .. (complete ? Array.Empty<ProfileRomRegion>() : [new(ProfileRomRole.CpmLower, null, 0x2000, 0, ProfileRomStore.MainRom, 0x4000, ProfileRomAvailability.MissingLowerHalf)]),
            new(ProfileRomRole.If1, $"roms/hc2000/{folder}/if1.rom", 0x4000, 0, ProfileRomStore.ShadowRom, 0, ProfileRomAvailability.SuppliedUnverified)
        ];
        Assert.Equal(expected, profile.RomRegions);
        Assert.Equal(complete, profile.HasCompleteRomDescription);
    }

    [Theory]
    [InlineData("empty-name")]
    [InlineData("null-name")]
    [InlineData("uppercase-name")]
    [InlineData("whitespace-name")]
    [InlineData("version-zero")]
    [InlineData("version-negative")]
    [InlineData("version-unsupported")]
    [InlineData("unknown-name")]
    [InlineData("null-memory")]
    [InlineData("null-video")]
    [InlineData("null-roms")]
    [InlineData("invalid-layout")]
    [InlineData("small-ram")]
    [InlineData("unaligned-rom")]
    [InlineData("small-shadow-rom")]
    [InlineData("bad-shadow-ram")]
    [InlineData("empty-provenance")]
    [InlineData("flash-zero")]
    [InlineData("flash-denominator-zero")]
    [InlineData("flash-unresolvable")]
    [InlineData("null-palette")]
    [InlineData("wrong-palette-size")]
    [InlineData("unknown-io")]
    [InlineData("name-layout-mismatch")]
    [InlineData("basic-wrong-version")]
    [InlineData("if1-wrong-version")]
    [InlineData("shifted-cpm")]
    [InlineData("invented-lower-half")]
    [InlineData("wrong-cpm-offset")]
    public void InvalidProfileChoicesRejectWithoutResolvingAssets(string defect)
    {
        var p = MachineProfile.Default;
        p = defect switch
        {
            "empty-name" => p with { Name = "" },
            "null-name" => p with { Name = null! },
            "uppercase-name" => p with { Name = "HC2000-v2" },
            "whitespace-name" => p with { Name = " hc2000-v2" },
            "version-zero" => p with { Version = 0 },
            "version-negative" => p with { Version = -1 },
            "version-unsupported" => p with { Version = 2 },
            "unknown-name" => p with { Name = "hc2000-v3" },
            "null-memory" => p with { Memory = null! },
            "null-video" => p with { Video = null! },
            "null-roms" => p with { RomRegions = null! },
            "invalid-layout" => p with { Memory = p.Memory with { RomLayout = (HcRomLayout)99 } },
            "small-ram" => p with { Memory = p.Memory with { RamBytes = 0x8000 } },
            "unaligned-rom" => p with { Memory = p.Memory with { RomBytes = 0x8001 } },
            "small-shadow-rom" => p with { Memory = p.Memory with { ShadowRomBytes = 0x2000 } },
            "bad-shadow-ram" => p with { Memory = p.Memory with { ShadowRamBytes = 1 } },
            "empty-provenance" => p with { Memory = p.Memory with { RomProvenance = " " } },
            "flash-zero" => p with { Video = p.Video with { FlashPeriodNumerator = 0 } },
            "flash-denominator-zero" => p with { Video = p.Video with { FlashPeriodDenominator = 0 } },
            "flash-unresolvable" => p with { Video = p.Video with { FlashPeriodNumerator = 1, FlashPeriodDenominator = 2 } },
            "null-palette" => p with { Video = p.Video with { Palette = null! } },
            "wrong-palette-size" => p with { Video = p.Video with { Palette = new uint[15] } },
            "unknown-io" => p with { Io = (MachineIoPolicy)99 },
            "name-layout-mismatch" => p with { Name = "hc2000-v1" },
            "basic-wrong-version" => ChangeRegion(p, ProfileRomRole.Basic, r => r with { SourcePath = "roms/hc2000/v1/basic.rom" }),
            "if1-wrong-version" => ChangeRegion(p, ProfileRomRole.If1, r => r with { SourcePath = "roms/hc2000/v1/if1.rom" }),
            "shifted-cpm" => ChangeRegion(p, ProfileRomRole.CpmUpper, r => r with { SourcePath = "roms/hc2000/v2/cpm.rom" }),
            "invented-lower-half" => ChangeRegion(p, ProfileRomRole.CpmLower, r => r with { Availability = ProfileRomAvailability.SuppliedUnverified }),
            _ => ChangeRegion(p, ProfileRomRole.CpmUpper, r => r with { StoreOffset = 0x4000 })
        };
        Assert.ThrowsAny<ArgumentException>(() => p.Validate());
    }

    [MemoryRomAssetsTheory]
    [InlineData("hc2000-v1", "fcb2da8daa8e1698b898a13b8baf9556d7fe2c7f2039bd1b7a6033185e0798b8",
        "4e5a52e98cbe5f9d42bcb2dcdd38cc6095f42dc0fb437be218dacac3da3830fa", "40fb91d5a42ca4eb0982966c26144e9136b8c333cc886d864cebcaddd9267ec9")]
    [InlineData("hc2000-v2", "e3deac5e43d36ff6bc91050c604e8ae94010d7c2c75f1992baad786a39d986fd",
        "eede2ed1a608602bd5314a3e877f9f032a38ff1435b4a24317ab307a48686dc4", "d93d799036971fa5960dd6ef3787ba4aa3e97941ab5e5714783df1ec3c8f2475")]
    [Trait("hardware", "unverified")]
    public void SuppliedProfileRomPathsPinFixtureIdentitiesNotBootCompatibility(string name, string basic, string cpm, string if1)
    {
        var profile = name == "hc2000-v1" ? MachineProfile.Hc2000V1 : MachineProfile.Hc2000V2;
        foreach (var region in profile.RomRegions.Where(r => r.SourcePath is not null))
        {
            var hash = region.Role switch { ProfileRomRole.Basic => basic, ProfileRomRole.If1 => if1, _ => cpm };
            var bytes = File.ReadAllBytes(Path.Combine(StorageAssetTests.Root(), region.SourcePath!));
            Assert.Equal(region.Length, bytes.Length);
            Assert.Equal(hash, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        }
        Assert.Equal(name == "hc2000-v1", profile.HasCompleteRomDescription);
    }

    private static MachineProfile ChangeRegion(MachineProfile p, ProfileRomRole role,
        Func<ProfileRomRegion, ProfileRomRegion> change) =>
        p with { RomRegions = p.RomRegions.Select(r => r.Role == role ? change(r) : r).ToArray() };

    [Fact]
    public void InitializersAndRecordClonesDeepOwnPaletteAndDescriptors()
    {
        var palette = new VideoProfile().Palette.ToArray();
        var video = new VideoProfile { Palette = palette };
        var regions = MachineProfile.Default.RomRegions.ToArray();
        var profile = new MachineProfile { Video = video, RomRegions = regions };
        var clone = profile with { };
        var expected = profile.Video.Palette[1];
        var originalRegion = profile.RomRegions[0];
        palette[1] = 0;
        regions[0] = regions[0] with { SourcePath = "not-a-ROM" };
        Assert.Equal(expected, profile.Video.Palette[1]);
        Assert.Equal(expected, clone.Video.Palette[1]);
        Assert.Equal(originalRegion, profile.RomRegions[0]);
        Assert.Equal(originalRegion, clone.RomRegions[0]);
        Assert.NotSame(profile.Memory, clone.Memory);
        Assert.NotSame(profile.Video, clone.Video);
        Assert.NotSame(profile.Video.Palette, clone.Video.Palette);
        Assert.NotSame(profile.RomRegions, clone.RomRegions);
        Assert.NotSame(profile.RomRegions[0], clone.RomRegions[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<uint>)profile.Video.Palette)[0] = 0);
        Assert.Throws<NotSupportedException>(() => ((IList<ProfileRomRegion>)profile.RomRegions)[0] = originalRegion);
        profile.Validate();
        clone.Validate();
    }

    [Fact]
    public void DefaultAndNewRecordSelectV2AndCatalogNeverSharesPaletteStorage()
    {
        var first = MachineProfile.Default;
        var second = MachineProfile.Default;
        Assert.Equal("hc2000-v2", new MachineProfile().Name);
        Assert.Equal(first.Name, second.Name);
        Assert.NotSame(first, second);
        Assert.NotSame(first.Video.Palette, second.Video.Palette);
        Assert.Throws<NotSupportedException>(() => ((IList<uint>)first.Video.Palette)[1] = 0);
        Assert.Equal(0xff0000cdU, second.Video.Palette[1]);
        Assert.Equal("hc2000-v1", MachineProfile.Hc2000V1.Name);
    }
}
