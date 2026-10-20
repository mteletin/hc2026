using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>Spec 14 section 7 examples through the real port decoder, on the same deck the buttons drive.</summary>
public sealed class TapeControlPortTests
{
    private sealed class Rig
    {
        internal readonly DeviceScheduler Scheduler = new();
        internal readonly TapeTransport Tape;
        internal readonly ITapeDeck Deck;
        internal readonly TapeControlPort Port = new();
        internal readonly PortDecoder Ports;
        internal Rig(bool tapeControl = true)
        {
            var memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
            var ula = new Ula(memory, new(), Scheduler, new FrameClock(Scheduler, 1), 2);
            Scheduler.InvalidateDevice(1); Scheduler.InvalidateDevice(2);
            Tape = new TapeTransport(Scheduler, ula, 42); Deck = Tape.Deck; Port.Attach(Tape);
            var idle = new PortDevice();
            Ports = new(tapeControl
                ? HcIoProfile.WithTapeControl(MachineProfile.Hc2000V2TapeControlV1, memory, ula, idle, idle, idle, idle, idle, Port)
                : HcIoProfile.Faithful(memory, ula, idle, idle, idle, idle, idle));
        }
        internal void Out(byte value, ushort port = TapeControlPort.Address) => Ports.Write(port, value, Scheduler.Now);
        internal byte In(byte field) { Out((byte)(0x20 + field)); return Ports.Read(TapeControlPort.Address, Scheduler.Now); }
        internal ushort Generation => (ushort)(In(4) | In(5) << 8);
        internal uint Block => (uint)(In(10) | In(11) << 8 | In(12) << 16 | In(13) << 24);
        internal void Seconds(double s) => Scheduler.AdvanceTo(Scheduler.Now + (long)(s * FrameTiming.MasterHz));
        internal void UntilStopped(int maxSeconds = 400)
        {
            for (int i = 0; i < maxSeconds && Deck.Observe().Mode != TapeDeckMode.Stopped; i++) Seconds(1);
            Assert.Equal(TapeDeckMode.Stopped, Deck.Observe().Mode);
        }
        /// <summary>Header (flag 00) then data (flag FF), synthesized by the TAP reader with its gaps.</summary>
        internal void InsertTwoBlocks()
        {
            byte[] tap = [4, 0, 0x00, 1, 2, 3, 3, 0, 0xff, 7, 0xf8];
            var read = new TapCodec().Read(tap, new(TapeFormat.Tap, TapeSide.A, TapeCapacity.C60)).Value;
            Assert.True(Deck.Blank(TapeCapacity.C60, false).Success);
            var blankB = Deck.Export(TapeSide.B).Value.Image;
            Assert.True(Deck.Insert(new(TapeCapacity.C60, read.Image, blankB, read.SourceAssets), true).Success);
        }
    }

    [Fact]
    public void ProfileOffReadsFfAndAWriteMovesNothing()
    {
        var rig = new Rig(tapeControl: false); rig.Deck.Blank(TapeCapacity.C60, false);
        Assert.Equal(0xff, rig.Ports.Read(TapeControlPort.Address, rig.Scheduler.Now));
        rig.Out(0x01);
        Assert.Equal(TapeDeckMode.Stopped, rig.Deck.Observe().Mode);
        Assert.Throws<ArgumentException>(() => HcIoProfile.WithTapeControl(MachineProfile.Hc2000V2, new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]),
            new PortDevice(), new PortDevice(), new PortDevice(), new PortDevice(), new PortDevice(), new PortDevice(), new TapeControlPort()));
    }

    [Fact]
    public void OnlyTheExactSixteenBitAddressDecodes()
    {
        var rig = new Rig(); rig.Deck.Blank(TapeCapacity.C60, false);
        rig.Out(0x01, 0x0191); rig.Out(0x01, 0x0090);
        Assert.Equal(TapeDeckMode.Stopped, rig.Deck.Observe().Mode);
        Assert.Equal(0xff, rig.Ports.Read(0x0191, rig.Scheduler.Now));
    }

    [Fact]
    public void PlayIsTheButtonAndCountsOneGeneration()
    {
        var rig = new Rig(); rig.Deck.Blank(TapeCapacity.C60, false);
        ushort before = rig.Generation;
        rig.Out(0x01);
        Assert.Equal(TapeDeckMode.Playing, rig.Deck.Observe().Mode);
        Assert.Equal(1, rig.In(1)); Assert.Equal(0, rig.In(3)); Assert.Equal(before + 1, rig.Generation);
        Assert.Equal(0x01 | 0x04 | 0x10, rig.In(0)); // media, tab in, not stopped
        rig.Seconds(5); Assert.Equal(before + 1, rig.Generation); // head motion is not a deck change
        rig.Deck.Pause(); Assert.Equal(before + 2, rig.Generation); // a manual button is
        rig.Out(0x06); Assert.False(rig.Deck.Observe().Paused); Assert.Equal(0, rig.In(3));
        rig.Out(0x05); Assert.True(rig.Deck.Observe().Paused); rig.Out(0x05); Assert.Equal(0, rig.In(3));
        rig.Out(0x00); Assert.Equal(TapeDeckMode.Stopped, rig.Deck.Observe().Mode);
        rig.Out(0x00); Assert.True(rig.Deck.Observe().Present); Assert.Equal(0, rig.In(3)); // STOP never ejects
    }

    [Fact]
    public void RecWithTheTabOutIsWriteProtectAndChangesNothing()
    {
        var rig = new Rig(); rig.Deck.Blank(TapeCapacity.C60, false); rig.Deck.SetTab(TapeSide.A, false);
        ushort before = rig.Generation;
        rig.Out(0x02);
        Assert.Equal(3, rig.In(3)); Assert.Equal(0, rig.In(1)); Assert.Equal(before, rig.Generation);
    }

    [Fact]
    public void UnsupportedNoMediaAndInterlockAreResultsNotMotion()
    {
        var rig = new Rig();
        rig.Out(0x01); Assert.Equal(2, rig.In(3)); Assert.False(rig.Deck.Observe().Present);
        rig.Deck.Blank(TapeCapacity.C60, false);
        rig.Out(0x09); Assert.Equal(5, rig.In(3)); Assert.Equal(TapeDeckMode.Stopped, rig.Deck.Observe().Mode);
        rig.Out(0x30); Assert.Equal(5, rig.In(3));
        rig.Out(0x01); rig.Out(0x03);
        Assert.Equal(1, rig.In(3)); Assert.Equal(TapeDeckMode.Playing, rig.Deck.Observe().Mode);
    }

    [Fact]
    public void SelectorReadsPastTheRecordAsZeroAndResetClearsIt()
    {
        var rig = new Rig(); rig.Deck.Blank(TapeCapacity.C60, false);
        ushort before = rig.Generation;
        Assert.Equal(0x01 | 0x04, rig.In(0)); Assert.Equal(before, rig.Generation);
        Assert.Equal(0, rig.In(14)); Assert.Equal(0, rig.In(15));
        rig.Out(0x23); rig.Port.Reset();
        Assert.Equal(0x01 | 0x04, rig.Ports.Read(TapeControlPort.Address, rig.Scheduler.Now));
        Assert.Equal(uint.MaxValue, rig.Block);
    }

    [Fact]
    public void SkipStopsWhereEachBlocksGapBeginsAndReportsTheDataBlockIndex()
    {
        var rig = new Rig(); rig.InsertTwoBlocks();
        var blocks = TapCodec.Blocks(rig.Tape.ActiveIndex!);
        Assert.Equal(2, blocks.Length); Assert.Equal(0x00, blocks[0].Flag); Assert.Equal(0xff, blocks[1].Flag);
        rig.Out(0x07); Assert.Equal(0, rig.In(3)); Assert.Equal(default, rig.Deck.Observe().Position); // already at the start
        rig.Out(0x08); rig.UntilStopped();
        Assert.Equal(blocks[0].End, rig.Deck.Observe().Position); Assert.Equal(0, rig.In(2)); Assert.Equal(uint.MaxValue, rig.Block);
        rig.Out(0x08); rig.UntilStopped();
        Assert.Equal(blocks[1].End, rig.Deck.Observe().Position); Assert.Equal(0, rig.In(2));
        rig.Out(0x07); rig.UntilStopped();
        Assert.Equal(blocks[0].End, rig.Deck.Observe().Position);
        // PLAY into the data block: it is data block 0 (the header has flag 00).
        rig.Out(0x01);
        for (int i = 0; i < 20 && rig.Deck.Observe().Position < blocks[1].Start; i++) rig.Seconds(0.25);
        rig.Seconds(0.05); Assert.Equal(0u, rig.Block);
        rig.Out(0x00); rig.Out(0x08); rig.UntilStopped(); // from inside the data block: its own gap
        Assert.Equal(blocks[1].End, rig.Deck.Observe().Position);
        rig.Out(0x08); rig.UntilStopped(); // no later gap: the plain wind to the physical end
        Assert.Equal(2, rig.In(2)); Assert.Equal(0, rig.In(3));
    }

    [Fact]
    public void ADeckSnapshotTakenMidSkipKeepsItsTarget()
    {
        var rig = new Rig(); rig.InsertTwoBlocks();
        var blocks = TapCodec.Blocks(rig.Tape.ActiveIndex!);
        rig.Out(0x08); rig.Seconds(0.01);
        var state = rig.Tape.Capture().Value;
        rig.UntilStopped();
        Assert.True(rig.Tape.Restore(state).Success);
        Assert.Equal(TapeDeckMode.FastForwarding, rig.Deck.Observe().Mode);
        rig.UntilStopped();
        Assert.Equal(blocks[0].End, rig.Deck.Observe().Position); Assert.Equal(TapeHoldReason.None, rig.Deck.Observe().HoldReason);
    }

    [Fact]
    public void PortSnapshotRestoresOnlyOntoTheDeckStateItCounted()
    {
        var rig = new Rig(); rig.Deck.Blank(TapeCapacity.C60, false);
        rig.Out(0x09); rig.Out(0x22);
        var state = rig.Port.Capture();
        Assert.True(rig.Port.Restore(state));
        var other = new TapeControlPort(); other.Attach(rig.Tape);
        // Matching deck key alone cannot transplant an unmatched live generation.
        Assert.False(other.Restore(state));
        var otherState = other.Capture(); Assert.True(other.Restore(otherState));
        rig.Deck.Play();
        Assert.False(rig.Port.Restore(state)); Assert.False(other.Restore(otherState));
    }

    [Fact]
    public void ReviewedGenerationCountsEveryManualPauseBetweenReads()
    {
        var rig = new Rig(); rig.Deck.Blank(TapeCapacity.C60, false); rig.Out(0x01);
        ushort before = rig.Generation;
        Assert.True(rig.Deck.Pause().Success); Assert.True(rig.Deck.Pause().Success);
        Assert.False(rig.Deck.Observe().Paused);
        Assert.Equal(unchecked((ushort)(before + 2)), rig.Generation);
    }

    [Fact]
    public void ReviewedGenerationCountsTabsSidesAndSpeedEvenWhenTheKeyReturns()
    {
        var rig = new Rig(); rig.Deck.Blank(TapeCapacity.C60, false);
        ushort before = rig.Generation;
        Assert.True(rig.Deck.SetTab(TapeSide.A, false).Success); Assert.True(rig.Deck.SetTab(TapeSide.A, true).Success);
        Assert.True(rig.Deck.Flip().Success); Assert.True(rig.Deck.Flip().Success);
        Assert.True(rig.Deck.SetFastWind(true).Success); Assert.True(rig.Deck.SetFastWind(false).Success);
        Assert.Equal(unchecked((ushort)(before + 6)), rig.Generation);
    }

    [Fact]
    public void ReviewedGenerationCountsManualWindAndEachAutomaticPhysicalStop()
    {
        var rig = new Rig(); rig.Deck.Blank(TapeCapacity.C60, false);
        ushort before = rig.Generation;
        Assert.True(rig.Deck.FastForward().Success); rig.Seconds(180);
        Assert.Equal(TapeHoldReason.PhysicalEnd, rig.Deck.Observe().HoldReason);
        Assert.True(rig.Deck.Rewind().Success); rig.Seconds(180);
        Assert.Equal(default, rig.Deck.Observe().Position);
        Assert.Equal(unchecked((ushort)(before + 4)), rig.Generation);
    }

    [Fact]
    public void ReviewedGenerationIgnoresIdempotentCommandsRefusalsAndHeadMotion()
    {
        var rig = new Rig(); rig.Deck.Blank(TapeCapacity.C60, false);
        ushort before = rig.Generation;
        rig.Out(0x00); rig.Out(0x06); rig.Out(0x05);
        Assert.Equal(1, rig.In(3)); Assert.Equal(before, rig.Generation);
        rig.Out(0x01); ushort playing = rig.Generation; rig.Seconds(5);
        Assert.False(rig.Deck.Rewind().Success); Assert.Equal(playing, rig.Generation);
        rig.Out(0x05); ushort paused = rig.Generation;
        rig.Out(0x05); Assert.Equal(paused, rig.Generation);
        rig.Out(0x06); ushort resumed = rig.Generation;
        rig.Out(0x06); Assert.Equal(resumed, rig.Generation);
    }

    [Fact]
    public void ReviewedRestoreRejectsWrongLiveGenerationEvenWithTheSameKey()
    {
        var rig = new Rig(); rig.Deck.Blank(TapeCapacity.C60, false);
        var state = rig.Port.Capture();
        Assert.False(rig.Port.Restore(state with { Generation = unchecked((ushort)(state.Generation + 1)) }));
        Assert.Equal(state, rig.Port.Capture());
        Assert.True(rig.Port.Restore(state));
    }

    [Fact]
    public void ReviewedRestoreRejectsAnOldGenerationAfterManualKeyRoundtrip()
    {
        var rig = new Rig(); rig.Deck.Blank(TapeCapacity.C60, false); rig.Out(0x01);
        var state = rig.Port.Capture();
        rig.Deck.Pause(); rig.Deck.Pause();
        Assert.False(rig.Port.Restore(state));
        Assert.Equal(unchecked((ushort)(state.Generation + 2)), rig.Generation);
    }

    [Fact]
    public void ReviewedGenerationCountsImmediateManualPlayAndAutomaticFormatHold()
    {
        var rig = new Rig(); rig.Deck.Blank(TapeCapacity.C60, false);
        var blankA = rig.Deck.Export(TapeSide.A).Value.Image;
        var blankB = rig.Deck.Export(TapeSide.B).Value.Image;
        var marked = new TapeSideImage(blankA.Duration, blankA.SourceDuration, blankA.Chunks,
            [new(default, 0, "stop", null, null)], [], [], blankA.ImportOptions);
        Assert.True(rig.Deck.Insert(new(TapeCapacity.C60, marked, blankB, []), true).Success);
        ushort before = rig.Generation; rig.Out(0x01);
        Assert.Equal(TapeDeckMode.Playing, rig.Deck.Observe().Mode);
        Assert.Equal(TapeHoldReason.FormatStop, rig.Deck.Observe().HoldReason);
        Assert.Equal(unchecked((ushort)(before + 2)), rig.Generation);
    }

    [Fact]
    public void ReviewedRestoredDeckStillPublishesEveryLiveTransition()
    {
        var rig = new Rig(); rig.Deck.Blank(TapeCapacity.C60, false); rig.Out(0x01);
        var state = rig.Tape.Capture().Value;
        Assert.True(rig.Deck.Pause().Success);
        Assert.True(rig.Tape.Restore(state).Success);
        ushort before = rig.Generation;
        Assert.True(rig.Deck.Pause().Success); Assert.True(rig.Deck.Pause().Success);
        Assert.Equal(unchecked((ushort)(before + 2)), rig.Generation);
    }

    // Fixed literal flag/data/checksum and independent timings; not TapCodec.Read/Write output.
    private static TapeSideImage ReviewedSignal(int doubledExit, int? highTicks, bool highGap = true,
        byte checksum = 252, bool padToCapacity = false)
    {
        var runs = new List<TapeRun>(); bool level = false;
        void Pulse(int ticks) { runs.Add(new(level, new(ticks, TapeTransport.ReferenceHz))); level = !level; }
        for (int i = 0; i < (highGap ? 3200 : 3201); i++) Pulse(2168);
        Pulse(667); Pulse(735);
        foreach (byte b in new byte[] { 255, 3, checksum })
            for (int bit = 7; bit >= 0; bit--)
            {
                int width = (b & (1 << bit)) == 0 ? 855 : 1710;
                Pulse(width); Pulse(width);
            }
        runs.Add(new(level, new(doubledExit, 2 * TapeTransport.ReferenceHz))); level = !level;
        if (highTicks is int gap) runs.Add(new(level, new(gap, TapeTransport.ReferenceHz)));
        var extent = runs.Aggregate(default(TapeTime), (sum, run) => sum + run.Duration);
        var duration = padToCapacity ? new TapeTime(1800, 1) : extent;
        if (padToCapacity) runs.Add(new(false, duration - extent));
        return new(duration, extent, [new(1, runs)], [], [], [], new(TapeFormat.Blank, null, true, [], null));
    }

    [Theory]
    [InlineData(1538, 3479836, true, 252)]
    [InlineData(1882, 3479836, true, 252)]
    [InlineData(3420, 3479836, true, 252)]
    [InlineData(1866, null, true, 252)]
    [InlineData(1866, 2799, true, 252)]
    [InlineData(1866, 3479836, false, 252)]
    [InlineData(1866, 3479836, true, 253)]
    public void ReviewedBlockScanDoesNotInventGapsForRefusedSignal(int exit, int? gap, bool highGap, byte checksum)
    {
        var image = ReviewedSignal(exit, gap, highGap, checksum);
        var signal = new TapeSignalIndexFactory().Build(image, new()).Value;
        Assert.False(new TapCodec().Write(image, new(TapeFormat.Tap, TapeSide.A)).Success);
        Assert.Empty(TapCodec.Blocks(signal));
    }

    [Theory]
    [InlineData(1539, 2800)]
    [InlineData(1866, 3479836)]
    [InlineData(1881, 2800)]
    public void ReviewedBlockScanSharesAcceptedExitAndExactGapBoundary(int exit, int gap)
    {
        var image = ReviewedSignal(exit, gap);
        Assert.True(new TapCodec().Write(image, new(TapeFormat.Tap, TapeSide.A)).Success);
        var block = Assert.Single(TapCodec.Blocks(new TapeSignalIndexFactory().Build(image, new()).Value));
        Assert.Equal((byte)255, block.Flag);
        // 3200*2168 + 667 + 735 + 16*2*1710 + 8*2*855, then the exit edge.
        Assert.Equal(new TapeTime(2L * 7_007_402 + exit, 2 * TapeTransport.ReferenceHz), block.End);
    }

    [Fact]
    public void ReviewedSkipIgnoresRefusedBlockAndUsesPhysicalEnd()
    {
        var rig = new Rig(); rig.Deck.Blank(TapeCapacity.C60, false);
        var blankB = rig.Deck.Export(TapeSide.B).Value.Image;
        var image = ReviewedSignal(3420, 3479836, padToCapacity: true);
        Assert.True(rig.Deck.Insert(new(TapeCapacity.C60, image, blankB, []), true).Success);
        rig.Out(0x08); rig.UntilStopped();
        Assert.Equal(new TapeTime(1800, 1), rig.Deck.Observe().Position);
        Assert.Equal(2, rig.In(2)); Assert.Equal(0, rig.In(3));
    }

    [Fact]
    public void TheTapeControlIdentityIsItsOwnAndFaithfulProfilesRefuseTheFlag()
    {
        MachineProfile.Hc2000V2TapeControlV1.Validate();
        Assert.Throws<ArgumentException>(() => (MachineProfile.Hc2000V2 with { TapeControl = true }).Validate());
        Assert.Throws<ArgumentException>(() => (MachineProfile.Hc2000V2HddV1 with { TapeControl = true }).Validate());
        Assert.Throws<ArgumentException>(() => (MachineProfile.Hc2000V2TapeControlV1 with { TapeControl = false }).Validate());
    }
}
