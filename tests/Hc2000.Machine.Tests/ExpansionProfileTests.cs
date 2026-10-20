using Hc2000.Machine.Memory;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class ExpansionProfileTests
{
    private static MachineProfile Expansion => MachineProfile.Hc2000V2PagedExpansionV1;

    [Fact]
    public void ExplicitExpansionIdentityAdmitsOnlyTheExistingMCompatibilityMap()
    {
        var profile = Expansion;
        profile.Validate();
        Assert.Equal("hc2000-v2-paged-expansion-v1", profile.Name);
        Assert.Equal(1, profile.Version);
        Assert.Equal(0x40010000, profile.Memory.RamBytes);
        Assert.Equal(PagedRamPolicy.MTransferPolicyId, profile.Memory.RamPaged!.PolicyId);
        Assert.False(profile.Memory.RamPaged.BoundCpmWindow);
        Assert.Null(profile.Memory.RamBanks);
        Assert.Null(profile.BlockDisk);
        Assert.False(profile.TapeControl);
        Assert.Equal(17_734_475, profile.MasterHz);
        Assert.Equal(5, profile.CpuMasterTicksNumerator);
        Assert.Equal(1, profile.CpuMasterTicksDenominator);
    }

    [Fact]
    public void ExpansionRejectsUnpinnedMemoryClockAndComposites()
    {
        var p = Expansion;
        MachineProfile[] invalid = [
            p with { Version = 2 },
            p with { CpuTurboMultiplier = 2 },
            p with { TapeControl = true },
            p with { BlockDisk = BlockDiskPolicy.HddV1 },
            p with { Memory = p.Memory with { RamBytes = 0x10000 } },
            p with { Memory = p.Memory with { RamPaged = null } },
            p with { Memory = p.Memory with { RamPaged = new() } },
            p with { Memory = p.Memory with { RamPaged = new() { BoundCpmWindow = true } } },
            p with { Memory = p.Memory with { RamBanks = new(BankPolicy.PolicyId, 128, 8192) } },
            p with { Memory = p.Memory with { RomLayout = HcRomLayout.V1 } }
        ];
        foreach (var invalidProfile in invalid)
            Assert.ThrowsAny<ArgumentException>(() => invalidProfile.Validate());
    }

    [Fact]
    public void ExistingIdentitiesRemainSeparateAndExpansionIsNeverTheDefault()
    {
        Assert.Equal("hc2000-v2", MachineProfile.Default.Name);
        Assert.Null(MachineProfile.Default.Memory.RamPaged);
        MachineProfile.Hc2000V2PagedV1.Validate();
        MachineProfile.Hc2000V2PagedMTransferV1.Validate();
        MachineProfile.Hc2000V2HddV1.Validate();
        Assert.Throws<ArgumentException>(() => (Expansion with { Name = "hc2000-v2" }).Validate());
        Assert.Throws<ArgumentException>(() => (Expansion with { Name = "hc2000-v2-paged-v1" }).Validate());
    }
}
