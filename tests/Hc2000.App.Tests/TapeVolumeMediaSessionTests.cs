using System.Buffers.Binary;
using System.Collections.Immutable;
using Hc2000.App.Media;
using Hc2000.Machine;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.App.Tests;

// App media session's tape-volume lane: the same one session the binding v1 owns
// (mount / commit-unmount / discard), plus the product cap: volumes over 4 MiB are
// refused in the mount lane with the existing contract reason, the driver ceiling untouched.
[CollectionDefinition("tape-paged-session", DisableParallelization = true)]
public sealed class TapePagedSessionCollection { }

[Collection("tape-paged-session")]
public sealed class TapeVolumeMediaSessionTests
{
    internal sealed class Rig
    {
        internal readonly HcMemory Memory;
        internal readonly TapeTransport Tape;
        internal readonly TapeVolumeSession Session;
        internal ITapeDeck Deck => Tape.Deck;
        internal readonly MediaSession Media;
        internal Rig(int imageBytes = 16384, byte fill = 0xe5, bool volumeProtected = false, bool phantomFiveMiB = false)
        {
            // Each real D4 store is 1 GiB: reclaim/compact before reserving another.
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            Memory = new(MachineProfile.Hc2000V2PagedTapeV2.Memory, new byte[0x4000], new byte[0x4000], new byte[0x4000]);
            var scheduler = new DeviceScheduler();
            var ula = new Ula(Memory, new(), scheduler, new FrameClock(scheduler, 1), 2);
            scheduler.InvalidateDevice(1); scheduler.InvalidateDevice(2);
            Tape = new(scheduler, ula, 42);
            Session = new(MachineProfile.Hc2000V2PagedTapeV2, Tape, Memory);
            var data = new byte[imageBytes]; Array.Fill(data, fill);
            var codec = new TapeCatalogCodec();
            if (phantomFiveMiB)
            {
                // A synthesized TAP cannot physically carry a volume over ~450 KiB (C90 capacity at the
                // synthesized baud rate), so put a catalog record with a 5 MiB entry on the deck without
                // its image data: the mount lane refuses from the catalog, before any image is read.
                var record = codec.EncodeRecord(TapeSide.A,
                    [new TapeCatalogEntry("VOL1.DSK", false, 5242880u, 0u, 0u)], new()).Value;
                var block = new byte[record.Length + 2];
                block[0] = 0xff; // TapeCatalogCodec record framing: 0xff header, content, XOR trailer.
                record.CopyTo(block.AsSpan(1));
                byte xor = 0;
                foreach (var b in block.AsSpan(0, block.Length - 1)) xor ^= b;
                block[^1] = xor;
                Insert([ImmutableArray.CreateRange(block)]);
                Media = new(new Hc2000.Machine.Floppy.FloppyDrive(new()),
                    new Hc2000.Machine.Floppy.FloppyDrive(new()),
                    Tape, manualDeck: true, tapeVolume: () => Session);
                return;
            }
            var plan = codec.PlanAppend(new(TapeSide.A, []), null,
                new("VOL1.DSK", ImmutableArray.CreateRange(data), volumeProtected), new(100000, 10, true), new()).Value;
            Insert(plan.AppendedBlocks);
            Media = new(new Hc2000.Machine.Floppy.FloppyDrive(new()),
                new Hc2000.Machine.Floppy.FloppyDrive(new()),
                Tape, manualDeck: true, tapeVolume: () => Session);
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
    }

    private static TapeVolumeView ViewOf(MediaReply reply)
    {
        Assert.NotNull(reply.TapeVolume);
        return reply.TapeVolume!;
    }

    [Fact]
    public void Mount_BindsTheFirstFreeLetterAndReportsTheBoundSession()
    {
        var r = new Rig();
        var reply = r.Media.Apply(new MediaCommand(MediaAction.MountTapeVolume, Name: "VOL1.DSK"));
        Assert.Null(reply.Error);
        var view = ViewOf(reply);
        Assert.True(view.Mounted);
        Assert.Equal('C', view.Letter);
        Assert.Equal("VOL1.DSK", view.Name);
        Assert.Equal(16384, view.ImageBytes);
        Assert.False(view.Dirty);
        var volume = Assert.Single(view.Volumes);
        Assert.Equal("VOL1.DSK", volume.Name);
        Assert.Equal(16384, volume.ImageBytes);
        Assert.True(volume.Mounted); // The bound session is the one the listing marks mounted.
        Assert.True(r.Session.Mounted);
        Assert.Equal('C', r.Session.Letter);
    }

    [Fact]
    public void SecondMount_Refuses_WithTheAlreadyMountedError()
    {
        var r = new Rig();
        Assert.Null(r.Media.Apply(new MediaCommand(MediaAction.MountTapeVolume, Name: "VOL1.DSK")).Error);
        var reply = r.Media.Apply(new MediaCommand(MediaAction.MountTapeVolume, Name: "VOL1.DSK"));
        Assert.Equal("A tape volume is already mounted as C.", reply.Error);
        Assert.Single(r.Session.List().Value);
    }

    [Fact]
    public void Unmount_WritesBackDirtySessionThenUnmounts()
    {
        var r = new Rig();
        Assert.Null(r.Media.Apply(new MediaCommand(MediaAction.MountTapeVolume, Name: "VOL1.DSK")).Error);
        r.Session.Write(0, 0x11);
        var reply = r.Media.Apply(new MediaCommand(MediaAction.UnmountTapeVolume));
        Assert.Null(reply.Error);
        Assert.False(ViewOf(reply).Mounted);
        Assert.False(r.Session.Mounted);
        var fresh = new TapeVolumeSession(MachineProfile.Hc2000V2PagedTapeV2, r.Tape, r.Memory);
        Assert.True(fresh.Mount("VOL1.DSK", 'C').Value); // Read requires a mounted session.
        Assert.Equal(0x11, fresh.Read(0));
    }

    [Fact]
    public void Discard_CancelsWithoutWriteback()
    {
        var r = new Rig();
        Assert.Null(r.Media.Apply(new MediaCommand(MediaAction.MountTapeVolume, Name: "VOL1.DSK")).Error);
        r.Session.Write(0, 0x11);
        var reply = r.Media.Apply(new MediaCommand(MediaAction.DiscardTapeVolume));
        Assert.Null(reply.Error);
        Assert.False(ViewOf(reply).Mounted);
        var fresh = new TapeVolumeSession(MachineProfile.Hc2000V2PagedTapeV2, r.Tape, r.Memory);
        Assert.True(fresh.Mount("VOL1.DSK", 'C').Value); // Read requires a mounted session.
        Assert.Equal((byte)0xe5, fresh.Read(0));
    }

    [Fact]
    public void PlayingDeck_MountRefuses_WithTheInterlockError()
    {
        var r = new Rig();
        Assert.True(r.Deck.RecordPlay().Success);
        var reply = r.Media.Apply(new MediaCommand(MediaAction.MountTapeVolume, Name: "VOL1.DSK"));
        Assert.Equal("Interlock: Mount requires a stopped cassette.", reply.Error);
        Assert.False(r.Session.Mounted);
    }

    [Fact]
    public void UnknownVolume_MountRefuses_WithTheCatalogError()
    {
        var r = new Rig();
        var reply = r.Media.Apply(new MediaCommand(MediaAction.MountTapeVolume, Name: "NOPE.DSK"));
        Assert.Equal("Interlock: Volume is not in the newest valid catalog.", reply.Error);
        Assert.False(r.Session.Mounted);
    }

    [Fact]
    public void VolumeOverFourMiB_MountRefuses_WithTheExistingContractReason()
    {
        var r = new Rig(phantomFiveMiB: true); // 5 MiB catalog entry: over the 4 MiB product cap, under the driver's 8 MiB ceiling.
        var reply = r.Media.Apply(new MediaCommand(MediaAction.MountTapeVolume, Name: "VOL1.DSK"));
        Assert.Equal("InvalidFormat: Invalid volume size.", reply.Error);
        Assert.False(r.Session.Mounted);
        // The lane still reports the catalog: the volume is listed, only the mount is refused.
        var view = ViewOf(reply);
        var overCap = Assert.Single(view.Volumes);
        Assert.Equal(5242880, overCap.ImageBytes);
        Assert.False(overCap.Mounted);
    }

    [Fact]
    public void ProductCapRefusal_EnforcesTheBoundary_AtFourMiB()
    {
        Assert.Null(TapeCatalogCodec.ProductCapRefusal(4194304));
        var justOver = TapeCatalogCodec.ProductCapRefusal(4194305);
        Assert.NotNull(justOver);
        Assert.Equal(TapeError.InvalidFormat, justOver!.Code);
        Assert.Equal("Invalid volume size.", justOver.Detail);
        var fiveMiB = TapeCatalogCodec.ProductCapRefusal(5242880);
        Assert.Equal("Invalid volume size.", fiveMiB!.Detail);
        // The product cap refuses the driver's own 8 MiB ceiling: the mount lane enforces 4 MiB, while the
        // append lane and the driver keep the 8 MiB technical ceiling (proven below by the 9 MiB refusal).
        var ceiling = TapeCatalogCodec.ProductCapRefusal(8388608);
        Assert.NotNull(ceiling);
        Assert.Equal(TapeError.InvalidFormat, ceiling!.Code);
        Assert.Equal("Invalid volume size.", ceiling.Detail);
        // The codec's own append lane keeps its technical 8 MiB contract.
        var data = new byte[9437184]; // 9 MiB: refused by the codec itself, same reason.
        var refusal = new TapeCatalogCodec().PlanAppend(new(TapeSide.A, []), null,
            new("BIG.DSK", ImmutableArray.CreateRange(data), false), new(100000, 10, true), new());
        Assert.False(refusal.Success);
        Assert.Contains("Invalid volume size.", refusal.Failure!.Detail);
    }
}
