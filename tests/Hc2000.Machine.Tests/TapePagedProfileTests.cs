using Hc2000.Machine.Memory;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class TapePagedProfileTests
{
    private static MachineProfile Combined => MachineProfile.Hc2000V2PagedTapeV2;

    [Fact]
    public void CombinedIdentityComposesOnlyPlainPagingAndTapeControl()
    {
        Combined.Validate();
        Assert.Equal("hc2000-v2-paged-tape-v2", Combined.Name);
        Assert.Equal(PagedRamPolicy.PolicyId, Combined.Memory.RamPaged!.PolicyId);
        Assert.True(Combined.Memory.RamPaged.BoundCpmWindow);
        Assert.Throws<ArgumentException>(() => (Combined with { Memory = Combined.Memory with { RamPaged = new() } }).Validate());
        Assert.Throws<ArgumentException>(() => (Combined with { TapeControl = false }).Validate());
        Assert.Throws<ArgumentException>(() => (Combined with { Memory = Combined.Memory with { RamPaged = null } }).Validate());
        Assert.Throws<ArgumentException>(() => (Combined with { Memory = Combined.Memory with { RamPaged = new(PagedRamPolicy.MTransferPolicyId) } }).Validate());
        Assert.Throws<ArgumentException>(() => (Combined with { BlockDisk = BlockDiskPolicy.HddV1 }).Validate());
        Assert.Throws<ArgumentException>(() => (Combined with { CpuTurboMultiplier = 2 }).Validate());
        Assert.Throws<ArgumentException>(() => (Combined with { Memory = Combined.Memory with { If1Traps = new(1, 2, 3) } }).Validate());
    }

    [Theory]
    [InlineData("hc2000-v2")]
    [InlineData("hc2000-v2-tape-control-v1")]
    [InlineData("hc2000-v2-paged-v1")]
    [InlineData("hc2000-v2-paged-m-v1")]
    [InlineData("hc2000-v2-paged-tape-v1")]
    public void ExistingIdentitiesDoNotAcquireTheComposition(string name) =>
        Assert.Throws<ArgumentException>(() => (Combined with { Name = name }).Validate());
}
