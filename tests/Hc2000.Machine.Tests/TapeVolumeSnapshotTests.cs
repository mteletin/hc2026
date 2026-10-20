using System.Collections.Immutable;
using Hc2000.Machine.Input;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Snapshots;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

[Collection("tape-paged-session")]
public sealed class TapeVolumeSnapshotTests
{
    private sealed class Cpu : ICpuSnapshotSeam
    {
        private CpuLogicalState state = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, false, false, false, false, false, false, false, 1, 0, 0);
        public CpuLogicalState Capture() => state;
        public void Restore(CpuLogicalState value) => state = value;
    }

    [Theory]
    [InlineData("engaged")]
    [InlineData("revoked")]
    [InlineData("empty")]
    public void ComposedSnapshotRestoresCanonicalBytesDescriptorAndEngagement(string mode)
    {
        var profile = MachineProfile.Hc2000V2PagedTapeV2;
        var scheduler = new DeviceScheduler();
        var clock = new FrameClock(scheduler, 1);
        var mapper = CpuTimeMapper.FromProfile(profile);
        var memory = new HcMemory(profile.Memory, new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var ula = new Ula(memory, new KeyboardMatrix(), scheduler, clock, 2, profile.Video);
        var machine = new Machine(scheduler, clock, new(scheduler, mapper), ula, () => { }, () => 0);
        var tape = machine.EnableTape(42);
        var session = machine.EnableTapeVolume(profile, memory);
        var snapshot = machine.AttachSnapshotState(profile, clock, memory, mapper, new Cpu(), 2);
        InsertVolume(tape);
        Assert.True(session.Mount("VOL1.DSK", 'G').Success);
        var firstPage = session.FirstPage;
        memory.SetCpm(true);
        memory.Ram.Span[0x8100] = 0x37;
        memory.WritePagedPort(0x7efd, (byte)firstPage);
        memory.WritePagedPort(0x7dfd, (byte)(firstPage >> 8));
        memory.Write(0x8100, 0x42);
        if (mode == "revoked") Assert.True(tape.Deck.SetTab(TapeSide.A, false).Success);
        if (mode == "empty") session.Cancel();
        var expected = session.CaptureState();
        var path = Path.Combine(Path.GetTempPath(), "tape-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var file = File.Create(path)) snapshot.Save(file);
            // Release capture's detached GiB before the detached-load copy plan.
            GC.Collect();
            session.Cancel();
            // Snapshot adoption requires an empty deck; discard only the live copy.
            Assert.True(tape.Deck.StopEject(true).Success);
            memory.Ram.Span[0x10000 + firstPage * 0x4000 + 256] = 0x19;
            using (var file = File.OpenRead(path)) snapshot.Load(file);
            Assert.Equal(expected, session.CaptureState());
            Assert.Equal(mode == "engaged", memory.Latches.PagedEnabled);
            Assert.Equal(mode == "engaged" ? 1 : 0, memory.Ram.Span[TapeVolumeSession.DescriptorAddress]);
            if (mode == "empty")
            {
                Assert.Empty(memory.ClaimedPagedRanges);
                Assert.Equal(0x37, memory.Read(0x8100));
            }
            else
            {
                Assert.True(session.Dirty);
                Assert.Equal(0x42, session.Read(256));
                Assert.Single(memory.ClaimedPagedRanges);
                Assert.Equal(mode == "engaged" ? 0x42 : 0x37, memory.Read(0x8100));
                memory.Write(0x8100, 0x43);
                Assert.Equal(mode == "engaged" ? 0x43 : 0x42, session.Read(256));
            }
        }
        finally { File.Delete(path); }
    }

    private static void InsertVolume(TapeTransport tape)
    {
        var data = new byte[16384];
        Array.Fill(data, (byte)0xe5);
        var plan = new TapeCatalogCodec().PlanAppend(new(TapeSide.A, []), null,
            new("VOL1.DSK", ImmutableArray.CreateRange(data), false), new(100000, 10, true), new()).Value;
        using var wire = new MemoryStream();
        foreach (var block in plan.AppendedBlocks)
        {
            wire.WriteByte((byte)block.Length); wire.WriteByte((byte)(block.Length >> 8)); wire.Write(block.AsSpan());
        }
        var read = new TapCodec().Read(wire.ToArray(), new(TapeFormat.Tap, TapeSide.A, TapeCapacity.C60)).Value;
        tape.Deck.Blank(TapeCapacity.C60, true);
        var blank = tape.Deck.Export(TapeSide.B).Value.Image;
        Assert.True(tape.Deck.Insert(new(TapeCapacity.C60, read.Image, blank, read.SourceAssets), true).Success);
    }
}
