using Hc2000.Machine.Snapshots;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class HcsProfileLimitsTests
{
    [Fact]
    public void OnlyAValidatedPagedProfileSelectsTheExistingLargeContainerAllowance()
    {
        foreach (var profile in new[] { MachineProfile.Hc2000V1, MachineProfile.Hc2000V2,
            MachineProfile.Hc2000V2RamBanksV1, MachineProfile.Hc2000V2HddV1 })
            Assert.Equal(new HcsLimits(), HcsLimits.ForProfile(profile));
        foreach (var profile in new[] { MachineProfile.Hc2000V2PagedV1, MachineProfile.Hc2000V2PagedMTransferV1,
            MachineProfile.Hc2000V2PagedMTransferV2, MachineProfile.Hc2000V2PagedExpansionV1 })
            Assert.Equal(new HcsLimits(TotalBytes: 0x44000000), HcsLimits.ForProfile(profile));
        Assert.Throws<ArgumentNullException>(() => HcsLimits.ForProfile(null!));
        Assert.Throws<ArgumentException>(() => HcsLimits.ForProfile(MachineProfile.Hc2000V2 with { Name = "untrusted-paged-name" }));
    }

    [Theory]
    [InlineData(false, 268435456)]
    [InlineData(true, 0x44000000)]
    public void FileLengthBoundariesArePureAndDoNotAllocateTheRamArena(bool paged, int maximum)
    {
        var limits = HcsLimits.ForProfile(paged ? MachineProfile.Hc2000V2PagedExpansionV1 : MachineProfile.Hc2000V2);
        Assert.True(limits.AllowsFileLength(0)); // Empty still fails codec/queue validity, not size.
        Assert.True(limits.AllowsFileLength(maximum - 1));
        Assert.True(limits.AllowsFileLength(maximum));
        Assert.False(limits.AllowsFileLength((long)maximum + 1));
        Assert.False(limits.AllowsFileLength(long.MaxValue));
        Assert.False(limits.AllowsFileLength(-1));
        Assert.Equal(64 * 1024 * 1024, limits.BlobBytes); // Exact unique paged RAM schema exception stays separate.
        Assert.Equal(256 * 1024 * 1024, HcsWavPolicy.Default.MaxInputBytes);
    }
}
