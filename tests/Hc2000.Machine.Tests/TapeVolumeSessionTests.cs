using System.Buffers.Binary;
using System.Collections.Immutable;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

[CollectionDefinition("tape-paged-session", DisableParallelization = true)]
public sealed class TapePagedSessionCollection { }

[Collection("tape-paged-session")]
public sealed class TapeVolumeSessionTests
{
    internal sealed class Rig
    {
        internal readonly HcMemory Memory;
        internal readonly TapeTransport Tape;
        internal readonly TapeVolumeSession Session;
        internal ITapeDeck Deck => Tape.Deck;
        internal Rig(bool volumeProtected = false)
        {
            // Each real D4 store is 1 GiB: reclaim/compact the preceding case's
            // snapshot buffers before reserving another contiguous LOH object.
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            Memory = new(MachineProfile.Hc2000V2PagedTapeV2.Memory, new byte[0x4000], new byte[0x4000], new byte[0x4000]);
            var scheduler = new DeviceScheduler();
            var ula = new Ula(Memory, new(), scheduler, new FrameClock(scheduler, 1), 2);
            scheduler.InvalidateDevice(1); scheduler.InvalidateDevice(2);
            Tape = new(scheduler, ula, 42);
            Session = new(MachineProfile.Hc2000V2PagedTapeV2, Tape, Memory);
            var data = new byte[16384]; Array.Fill(data, (byte)0xe5);
            var codec = new TapeCatalogCodec();
            var plan = codec.PlanAppend(new(TapeSide.A, []), null,
                new("VOL1.DSK", ImmutableArray.CreateRange(data), volumeProtected), new(100000, 10, true), new()).Value;
            Insert(plan.AppendedBlocks);
        }
        internal void Insert(ImmutableArray<ImmutableArray<byte>> blocks)
        {
            using var wire = new MemoryStream();
            foreach (var b in blocks) { wire.WriteByte((byte)b.Length); wire.WriteByte((byte)(b.Length >> 8)); wire.Write(b.AsSpan()); }
            var read = new TapCodec().Read(wire.ToArray(), new(TapeFormat.Tap, TapeSide.A, TapeCapacity.C60)).Value;
            Deck.Blank(TapeCapacity.C60, true);
            var blank = Deck.Export(TapeSide.B).Value.Image;
            Assert.True(Deck.Insert(new(TapeCapacity.C60, read.Image, blank, read.SourceAssets), true).Success);
        }
        internal void Map()
        {
            Memory.SetCpm(true);
            Memory.WritePagedPort(0x7efd, (byte)Session.FirstPage);
            Memory.WritePagedPort(0x7dfd, (byte)(Session.FirstPage >> 8));
        }
    }

    [Theory]
    [InlineData("unmount")]
    [InlineData("epoch")]
    [InlineData("write-protect")]
    public void DescriptorAndAliasAreRevokedTogether(string change)
    {
        var r = new Rig(); Assert.True(r.Session.Mount("VOL1.DSK", 'G').Success); r.Map();
        var descriptor = r.Memory.Ram.Span.Slice(0x7400, 20);
        Assert.Equal(1, descriptor[0]); Assert.Equal(1, descriptor[1]);
        Assert.Equal(r.Session.FirstPage, BinaryPrimitives.ReadUInt16LittleEndian(descriptor[2..]));
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(descriptor[4..]));
        Assert.Equal(6, descriptor[6]); Assert.Equal(0, descriptor[7]);
        Assert.Equal(r.Tape.MediaEpoch, BinaryPrimitives.ReadUInt64LittleEndian(descriptor[8..]));
        Assert.Equal(r.Session.ImageBytes, BinaryPrimitives.ReadInt32LittleEndian(descriptor[16..]));
        r.Memory.Ram.Span[0x8000] = 0x37;
        if (change == "unmount") Assert.True(r.Session.Unmount().Success);
        else if (change == "epoch") Assert.True(r.Deck.StopEject(true).Success);
        else Assert.True(r.Deck.SetTab(TapeSide.A, false).Success);
        Assert.Equal(0, descriptor[0]);
        Assert.False(r.Memory.Latches.PagedEnabled);
        Assert.Equal(0x37, r.Memory.Read(0x8000)); // Bypasses the installed BIOS.
    }

    [Fact]
    public void GuestWindowAliasesTheCanonicalImageAndSameValueStoreIsDirty()
    {
        var r = new Rig(); Assert.True(r.Session.Mount("VOL1.DSK", 'G').Success); r.Map();
        Assert.InRange(r.Session.FirstPage, 256, 65535);
        Assert.Equal(0xe5, r.Memory.Read(0x8000));
        r.Memory.Write(0x8000, 0xe5); Assert.True(r.Session.Dirty);
        r.Memory.Write(0x8100, 0x42); Assert.Equal(0x42, r.Session.Read(256));
        Assert.Equal(0x42, r.Memory.Ram.Span[0x10000 + r.Session.FirstPage * 0x4000 + 256]);
        Assert.False(r.Session.Unmount().Success);
        r.Session.Cancel(); Assert.False(r.Session.Mounted);
    }

    [Theory]
    [InlineData('A')][InlineData('B')][InlineData('M')][InlineData('Q')][InlineData('@')]
    public void IllegalLetterClaimsNoPages(char letter)
    {
        var r = new Rig(); Assert.False(r.Session.Mount("VOL1.DSK", letter).Success);
        Assert.False(r.Session.Mounted); Assert.Empty(r.Memory.ClaimedPagedRanges);
    }

    [Fact]
    public void ProtectedGuestStoresAreDroppedAndStayClean()
    {
        var r = new Rig(true); Assert.True(r.Session.Mount("VOL1.DSK", 'G').Success); r.Map();
        r.Memory.Write(0x8000, 0x42); Assert.Equal(0xe5, r.Memory.Read(0x8000)); Assert.False(r.Session.Dirty);
    }

    [Fact]
    public void BoundWindowRequiresAnInRangeBankAndLeavesBankZeroNative()
    {
        var r = new Rig(); Assert.True(r.Session.Mount("VOL1.DSK", 'G').Success);
        r.Memory.SetCpm(true); r.Memory.Ram.Span[0x8000] = 0x37;
        Assert.Equal(0x37, r.Memory.Read(0x8000));
        r.Map(); Assert.Equal(0xe5, r.Memory.Read(0x8000));
        r.Memory.WritePagedPort(0x7efd, (byte)(r.Session.FirstPage + 1));
        Assert.Equal(0x37, r.Memory.Read(0x8000));
        r.Memory.Write(0x8000, 0x38);
        Assert.False(r.Session.Dirty); Assert.Equal(0xe5, r.Session.Read(0));
    }

    [Fact]
    public void BoundBasicMapKeepsNativeWorkspaceRomVideoAndQuiescentPorts()
    {
        var r = new Rig(); Assert.True(r.Session.Mount("VOL1.DSK", 'G').Success); r.Map();
        r.Memory.WritePagedPort(0x7ffd, 8);
        r.Memory.WritePort7E(1);
        r.Memory.Ram.Span[0xdb0d] = 0x97;
        r.Memory.Ram.Span[0xdb0e] = 0xe1;
        r.Memory.SetCpm(false);
        Assert.Equal(new MemoryLocation(MemoryStore.Ram, 0xdb0d, true), r.Memory.Resolve(0xdb0d));
        Assert.Equal(0x97, r.Memory.Read(0xdb0d));
        Assert.Equal(0x4000, r.Memory.Resolve(0).Offset);
        Assert.Equal(0x4000, r.Memory.VideoBase);
        var before = r.Memory.Latches;
        foreach (var port in new ushort[] { 0x7ffd, 0x7efd, 0x7dfd })
        {
            Assert.Equal(0xff, r.Memory.ReadPagedPort(port));
            r.Memory.WritePagedPort(port, 0xff);
        }
        Assert.Equal(before, r.Memory.Latches);
        r.Memory.SetCpm(true);
        Assert.Equal(0xe5, r.Memory.Read(0x8000));
    }

    [Fact]
    public void UnboundVolumeIsNotReadableThroughPriorPagedWindow()
    {
        var r = new Rig(); Assert.True(r.Session.Mount("VOL1.DSK", 'G').Success); r.Map();
        var latches = r.Memory.Latches;
        r.Memory.Ram.Span[0x8000] = 0x37;
        Assert.Equal(0xe5, r.Memory.Read(0x8000));
        Assert.True(r.Session.Unmount().Success);
        Assert.Empty(r.Memory.ClaimedPagedRanges);
        Assert.Equal(latches with { PagedEnabled = false }, r.Memory.Latches);
        // Bypass the resident BIOS and any common-RAM validity descriptor.
        Assert.Equal(0x37, r.Memory.Read(0x8000));
        Assert.Equal(0xff, r.Memory.ReadPagedPort(0x7efd));
        r.Memory.WritePagedPort(0x7efd, 5);
        Assert.Equal(latches with { PagedEnabled = false }, r.Memory.Latches);
    }

    [Fact]
    public void SessionPowerOnAndResetLeaveBaseMapAndPagedPortsQuiescent()
    {
        var r = new Rig();
        Assert.False(r.Memory.Latches.PagedEnabled);
        r.Memory.SetCpm(true); r.Memory.Ram.Span[0x8000] = 0x37;
        Assert.Equal(0x37, r.Memory.Read(0x8000));
        Assert.Equal(0xff, r.Memory.ReadPagedPort(0x7dfd));
        Assert.True(r.Session.Mount("VOL1.DSK", 'G').Success); r.Map();
        Assert.True(r.Memory.Latches.PagedEnabled);
        r.Session.Cancel(); r.Memory.ResetPaging();
        Assert.False(r.Memory.Latches.PagedEnabled);
        r.Memory.SetCpm(true); Assert.Equal(0x37, r.Memory.Read(0x8000));
    }

    [Fact]
    public void EpochRevokesAliasWithoutDiscardingDirtyImage()
    {
        var r = new Rig(); Assert.True(r.Session.Mount("VOL1.DSK", 'G').Success); r.Map();
        r.Memory.Ram.Span[0x8000] = 0x37;
        r.Memory.Write(0x8000, 0x42);
        r.Deck.StopEject(true);
        Assert.False(r.Memory.Latches.PagedEnabled);
        Assert.Equal(0x37, r.Memory.Read(0x8000));
        Assert.True(r.Session.Dirty); Assert.Equal(0x42, r.Session.Read(0));
    }

    [Fact]
    public void SideRoundTripRecoversButEjectReloadInvalidatesEpoch()
    {
        var r = new Rig(); Assert.True(r.Session.Mount("VOL1.DSK", 'G').Success); r.Map();
        var epoch = r.Tape.MediaEpoch;
        r.Deck.Flip(); r.Memory.Write(0x8000, 0x41);
        Assert.False(r.Session.MediaLive); Assert.False(r.Session.Dirty); Assert.Equal(epoch, r.Tape.MediaEpoch);
        r.Deck.Flip(); Assert.True(r.Session.MediaLive);
        r.Memory.Write(0x8000, 0x42); Assert.True(r.Session.Dirty);
        r.Deck.StopEject(true); r.Deck.Blank(TapeCapacity.C60, true);
        Assert.False(r.Session.MediaLive); Assert.True(r.Tape.MediaEpoch > epoch);
        r.Memory.Write(0x8000, 0x43); Assert.Equal(0x42, r.Session.Read(0));
        Assert.False(r.Session.Writeback().Success); r.Session.Cancel();
    }

    [Fact]
    public void MetadataRestoreKeepsDirtyBytesAndStaleMediaWithoutAnotherImage()
    {
        var r = new Rig(); Assert.True(r.Session.Mount("VOL1.DSK", 'G').Success); r.Map();
        r.Memory.Write(0x8100, 0x42); r.Deck.Flip();
        var state = r.Session.CaptureState();
        var json = System.Text.Json.JsonSerializer.Serialize(state);
        Assert.DoesNotContain("229,229", json); Assert.True(json.Length < 800);
        r.Session.Cancel();
        r.Session.RestoreState(System.Text.Json.JsonSerializer.Deserialize<TapeVolumeState>(json)!);
        Assert.True(r.Session.Dirty); Assert.False(r.Session.MediaLive); Assert.Equal(0x42, r.Session.Read(256));
        r.Memory.Write(0x8100, 0x43); Assert.Equal(0x42, r.Session.Read(256));
        r.Deck.Flip(); Assert.True(r.Session.MediaLive);
        Assert.True(r.Session.Writeback().Success);
    }

    [Fact]
    public void ResetCancellationDropsDirtyImageWithoutChangingCassetteOrEpoch()
    {
        var r = new Rig(); Assert.True(r.Session.Mount("VOL1.DSK", 'G').Success); r.Map();
        r.Memory.Write(0x8100, 0x42);
        var captured = r.Tape.Capture().Value;
        r.Session.Cancel();
        var next = new Rig(); Assert.True(next.Tape.Restore(captured).Success);
        Assert.Equal(captured.MediaEpoch, next.Tape.MediaEpoch);
        Assert.False(next.Session.Mounted); Assert.False(next.Session.Dirty);
        Assert.True(next.Session.Mount("VOL1.DSK", 'G').Success);
        Assert.Equal(0xe5, next.Session.Read(256));
        Assert.False(r.Session.Mounted); Assert.Empty(r.Memory.ClaimedPagedRanges);
    }

    [Theory]
    [InlineData(0)][InlineData(255)][InlineData(65536)]
    public void InvalidSnapshotRangeIsRefusedBeforeClaiming(int firstPage)
    {
        var r = new Rig(); Assert.True(r.Session.Mount("VOL1.DSK", 'G').Success);
        var state = r.Session.CaptureState(); r.Session.Cancel();
        Assert.Throws<ArgumentException>(() => r.Session.RestoreState(state with { Mounted = state.Mounted! with { FirstPage = firstPage } }));
        Assert.Empty(r.Memory.ClaimedPagedRanges);
    }

    [Fact]
    public void WritebackPublishesImageAndCatalogThenReloadReadsTheChangedByte()
    {
        var r = new Rig(); Assert.True(r.Session.Mount("VOL1.DSK", 'G').Success); r.Map();
        r.Memory.Write(0x8100, 0x42); Assert.True(r.Session.Writeback().Success); Assert.False(r.Session.Dirty);
        Assert.True(r.Session.Unmount().Success); Assert.Empty(r.Memory.ClaimedPagedRanges);
        Assert.True(r.Session.Mount("VOL1.DSK", 'G').Success); Assert.Equal(0x42, r.Session.Read(256));
    }
}
