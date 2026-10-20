using Hc2000.Machine.Expansion;
using Hc2000.Machine.Memory;
using Xunit;

namespace Hc2000.Machine.Tests;

// Serial with existing GiB-backed paging suites. Only the selected private pages are touched;
// these are synthetic memory fixtures, not ROM boot or snapshot-residency evidence.
[Collection("paged-ram")]
public sealed class ExpansionArenaTests
{
    private static readonly MachineProfile Profile = MachineProfile.Hc2000V2PagedExpansionV1;
    private static HcMemory Memory() => new(Profile.Memory, new byte[0x4000], new byte[0x4000], new byte[0x4000]);
    private static int Physical(ushort page, int offset = 0) => 0x10000 + page * 0x4000 + offset;
    private static void Bank(HcMemory memory, ushort bank)
    {
        // Each byte is independently admitted. Route through an unclaimed low byte,
        // rather than accidentally asking for private page 512 on the high write.
        memory.WritePagedPort(0x7efd, 0xff);
        memory.WritePagedPort(0x7dfd, (byte)(bank >> 8));
        memory.WritePagedPort(0x7efd, (byte)bank);
    }

    [Fact]
    public void SixteenDisjointFourPageLeasesUseTheExistingArenaExactly()
    {
        var memory = Memory();
        var arena = memory.EnableExpansionArena(Profile);
        var pages = new HashSet<ushort>();
        for (ushort id = 1; id <= 16; id++)
        {
            var grant = arena.GetGrant(id);
            Assert.Equal(id, grant.UnitId);
            Assert.Equal(1ul, grant.ResourceEpoch);
            Assert.Equal(1ul, grant.MapEpoch);
            var lease = arena.Acquire(id);
            for (var index = 0; index < 4; index++)
            {
                var page = checked((ushort)(ExpansionArena.FirstComputePage + 4 * (id - 1) + index));
                Assert.Equal(page, grant.Pages[index]);
                Assert.True(pages.Add(page));
                var first = (ushort)(index * 0x4000);
                var last = (ushort)(first + 0x3fff);
                lease.Write(first, (byte)id);
                lease.Write(last, (byte)(id + 0x40));
                Assert.Equal((byte)id, memory.Ram.Span[Physical(page)]);
                Assert.Equal((byte)(id + 0x40), memory.Ram.Span[Physical(page, 0x3fff)]);
                Assert.Equal((byte)id, lease.Read(first));
            }
        }
        Assert.Equal(64, pages.Count);
        Assert.Equal(0x40010000, memory.Ram.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => arena.Acquire(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => arena.Acquire(17));
        Assert.Throws<InvalidOperationException>(() => memory.EnableExpansionArena(Profile));
    }

    [Fact]
    public void MainLatchRefusalPreservesTheWholeMapIncludingLockAndInactiveTransfer()
    {
        var memory = Memory();
        var arena = memory.EnableExpansionArena(Profile);
        Assert.False(memory.Latches.PagedTransfer);
        Bank(memory, 255);
        var before = memory.Latches;
        var map = memory.Pages;
        memory.WritePagedPort(0x7dfd, 1); // candidate 511 is unclaimed and admitted
        Assert.Equal(511, memory.Latches.PagedBank);
        memory.WritePagedPort(0x7dfd, 2); // candidate 767 is unclaimed and admitted
        Assert.Equal(767, memory.Latches.PagedBank);
        memory.WritePagedPort(0x7efd, 0); // candidate 512 is private, even while transfer is off
        Assert.Equal(767, memory.Latches.PagedBank);
        Assert.False(memory.Latches.PagedTransfer);
        Bank(memory, 0);
        before = memory.Latches;
        map = memory.Pages;
        memory.WritePagedPort(0x7dfd, 2); // refuse entire update; cannot expose page 512 later
        Assert.Equal(before, memory.Latches);
        Assert.Same(map, memory.Pages);
        // Claim a page that the exact 7FFD low-bit update would select, and reject its lock too.
        Assert.True(arena.TryRepurpose(arena.GetGrant(1), new(577, 600, 601, 602)));
        Bank(memory, 576);
        before = memory.Latches;
        map = memory.Pages;
        memory.WritePagedPort(0x7ffd, 0x39); // page577 plus screen/ROM/lock bits
        Assert.Equal(before, memory.Latches);
        Assert.Same(map, memory.Pages);
        memory.SetCpm(true);
        memory.WritePagedPort(0x7cfd, 1);
        Assert.Equal(576, memory.Latches.PagedBank);
        Assert.True(memory.Latches.PagedTransfer);
    }

    [Fact]
    public void InvalidRepurposePublishesNothingAndCannotClaimLatchedOrPeerPages()
    {
        var memory = Memory();
        var arena = memory.EnableExpansionArena(Profile);
        var grant = arena.GetGrant(1);
        var lease = arena.Acquire(1);
        lease.Write(0x1234, 0x5a);
        Bank(memory, 500); // still reserved when the transient transfer window is off
        ExpansionPageMap[] invalid = [new(600, 600, 601, 602), new(255, 600, 601, 602),
            new(516, 600, 601, 602), new(500, 600, 601, 602), new(600, 601, 602, 500)];
        memory.Ram.Span[Physical(600)] = 0x76;
        foreach (var pages in invalid)
        {
            Assert.False(arena.TryRepurpose(grant, pages));
            Assert.Equal(grant, arena.GetGrant(1));
            Assert.Equal(0x5a, lease.Read(0x1234));
            Assert.Equal(0x76, memory.Ram.Span[Physical(600)]);
            Assert.Equal(500, memory.Latches.PagedBank);
        }
        var forged = grant with { Pages = new(400, 401, 402, 403) };
        Assert.False(arena.TryRepurpose(forged, new(600, 601, 602, 603)));
        Assert.Equal(grant, arena.GetGrant(1));
    }

    [Fact]
    public void RepurposeZerosOnlyTheNewImageAndRevokesEveryOldLeaseAccess()
    {
        var memory = Memory();
        var arena = memory.EnableExpansionArena(Profile);
        var old = arena.GetGrant(1);
        var lease = arena.Acquire(1);
        var peer = arena.Acquire(2);
        lease.Write(0, 0xa1);
        peer.Write(0, 0xb2);
        var next = new ExpansionPageMap(65535, 600, 601, 602);
        for (var page = 0; page < 4; page++) memory.Ram.Span.Slice(Physical(next[page]), 0x4000).Fill(0x8f);
        Assert.True(arena.TryRepurpose(old, next));
        var current = arena.GetGrant(1);
        Assert.Equal(next, current.Pages);
        Assert.Equal(2ul, current.ResourceEpoch);
        Assert.Equal(2ul, current.MapEpoch);
        Assert.Throws<InvalidOperationException>(() => lease.Read(0));
        Assert.Throws<InvalidOperationException>(() => lease.Write(0, 0xc3));
        Assert.Throws<InvalidOperationException>(() => lease.Copy());
        Assert.False(arena.TryRepurpose(old, old.Pages));
        Assert.Equal(0xa1, memory.Ram.Span[Physical(512)]); // release is not an unrequested erase
        Assert.Equal(0xb2, peer.Read(0));
        Assert.All(arena.Acquire(1).Copy(), value => Assert.Equal(0, value));
        Bank(memory, 512); // released grant returns to the main pool
        Assert.Equal(512, memory.Latches.PagedBank);
        var fresh = arena.Acquire(1);
        fresh.Write(0xffff, 0xef);
        Assert.Equal(0xef, memory.Ram.Span[Physical(602, 0x3fff)]);
        var copy = fresh.Copy();
        copy[0xffff] = 0;
        Assert.Equal(0xef, fresh.Read(0xffff));
    }

    [Fact]
    public void ExistingMPolicyWithoutExplicitCompositionStillAdmitsItsPrivateLookingBank()
    {
        var memory = Memory(); // the memory policy is intentionally shared by M and expansion
        Bank(memory, 512);
        Assert.Equal(512, memory.Latches.PagedBank);
        Assert.Throws<ArgumentException>(() => memory.EnableExpansionArena(MachineProfile.Hc2000V2PagedMTransferV1));
        // Initial roster admission must reject a conflicting main latch, not move it or zero data.
        memory.Ram.Span[Physical(512)] = 0x74;
        Assert.Throws<InvalidOperationException>(() => memory.EnableExpansionArena(Profile));
        Assert.Equal(512, memory.Latches.PagedBank);
        Assert.Equal(0x74, memory.Ram.Span[Physical(512)]);
        Bank(memory, 0);
        memory.EnableExpansionArena(Profile);
    }

    [Fact]
    public void FaithfulCompositionCannotAllocateOrActivateAnExpansionArena()
    {
        var memory = new HcMemory(MachineProfile.Default.Memory, new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        Assert.Throws<ArgumentException>(() => memory.EnableExpansionArena(MachineProfile.Default));
        Assert.Throws<ArgumentException>(() => memory.EnableExpansionArena(Profile));
        Assert.Equal(0x10000, memory.Ram.Length);
    }
}
