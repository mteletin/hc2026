using System.Buffers.Binary;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class HddSavedAcknowledgementTests
{
    private const int ImageBytes = 8_388_608;

    // Independent private media only: no originals, host save, Export/Restore or caller activation.
    private sealed class Rig
    {
        public HcMemory Memory { get; } = new(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        public DeviceScheduler Scheduler { get; } = new();
        public BlockDiskPort Port { get; }
        public FloppyController Fdc { get; }
        public byte[][] Originals { get; } = new byte[4][];

        public Rig()
        {
            Fdc = new FloppyController(Scheduler, 100,
                new(new(), Dsk.Create(new(40, 2, 9, 512))),
                new(new(), Dsk.Create(new(40, 2, 9, 512))));
            Port = new(Memory, Fdc, hddEnabled: true);
            for (byte unit = 2; unit <= 5; unit++)
            {
                var image = new byte[ImageBytes];
                image[1024] = unit; image[^1] = (byte)(0xa0 + unit);
                Originals[unit - 2] = image;
                Port.AttachHdd(unit, image);
                WriteSector(unit, (byte)(0x30 + unit));
            }
            // Sentinel DMA not associated with the last write.
            for (var i = 0; i < 512; i++) Memory.Write((ushort)(0x9000 + i), (byte)(i ^ 0x5a));
        }

        private void WriteSector(byte unit, byte value)
        {
            for (var i = 0; i < 512; i++) Memory.Write((ushort)(0x8100 + i), value);
            Span<byte> descriptor = stackalloc byte[16];
            descriptor.Clear(); descriptor[0] = 2; descriptor[1] = 2; descriptor[2] = unit;
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor[8..], 0x8100);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[12..], Port.CaptureHdd(unit).Generation);
            for (var i = 0; i < descriptor.Length; i++) Memory.Write((ushort)(0x8000 + i), descriptor[i]);
            Port.Write(0x81, 0, 0); Port.Write(0x83, 0x80, 0);
            Port.Write(0x89, 1, 0);
            Assert.Equal(0, Port.Read(0x89, 0));
        }

        public Observation Observe() => new(Port.Capture(), Memory.Ram.ToArray(), Memory.ShadowRam.ToArray(),
            Memory.Rom.ToArray(), Memory.ShadowRom.ToArray(), Scheduler.PendingEvents.ToArray(), Fdc.Fdc.Inspect());
    }

    private sealed record Observation(BlockDiskPortState Port, byte[] Ram, byte[] ShadowRam,
        byte[] Rom, byte[] ShadowRom, DeviceEvent[] Events, FdcDiagnosticSnapshot Fdc);

    private static void AssertUnchanged(Observation expected, Observation actual, params int[] acknowledged)
    {
        Assert.Equal(expected.Port.Pointer, actual.Port.Pointer);
        Assert.Equal(expected.Port.Status, actual.Port.Status);
        Assert.Equal(expected.Port.Slots.Length, actual.Port.Slots.Length);
        for (var i = 0; i < expected.Port.Slots.Length; i++)
        {
            var before = expected.Port.Slots[i]; var after = actual.Port.Slots[i];
            Assert.True(before.Bytes.AsSpan().SequenceEqual(after.Bytes), $"slot {i + 2} image changed");
            Assert.Equal(before.Generation, after.Generation);
            Assert.Equal(before.WriteProtected, after.WriteProtected);
            Assert.Equal(acknowledged.Contains(i + 2) ? false : before.Dirty, after.Dirty);
        }
        Assert.Equal(expected.Ram, actual.Ram); Assert.Equal(expected.ShadowRam, actual.ShadowRam);
        Assert.Equal(expected.Rom, actual.Rom); Assert.Equal(expected.ShadowRom, actual.ShadowRom);
        Assert.Equal(expected.Events, actual.Events);
        Assert.Equal(expected.Fdc.Phase, actual.Fdc.Phase); Assert.Equal(expected.Fdc.MainStatus, actual.Fdc.MainStatus);
        Assert.Equal(expected.Fdc.Command.ToArray(), actual.Fdc.Command.ToArray());
        Assert.Equal(expected.Fdc.LastCommand.ToArray(), actual.Fdc.LastCommand.ToArray());
        Assert.Equal((expected.Fdc.LastSt0, expected.Fdc.LastSt1, expected.Fdc.LastSt2, expected.Fdc.LastSt3),
            (actual.Fdc.LastSt0, actual.Fdc.LastSt1, actual.Fdc.LastSt2, actual.Fdc.LastSt3));
        Assert.Equal(expected.Fdc.PresentCylinders.ToArray(), actual.Fdc.PresentCylinders.ToArray());
        Assert.Equal(expected.Fdc.PendingInterrupts?.ToArray(), actual.Fdc.PendingInterrupts?.ToArray());
    }

    [Fact]
    public void PrivateFixtureHasFourDistinctDirtyImagesWithoutChangingOriginalCopies()
    {
        var r = new Rig();
        var before = r.Observe();
        for (var i = 0; i < 4; i++)
        {
            var slot = before.Port.Slots[i];
            Assert.True(slot.Present); Assert.True(slot.Dirty); Assert.False(slot.WriteProtected);
            Assert.Equal(1u, slot.Generation);
            Assert.All(slot.Bytes.Take(512), b => Assert.Equal((byte)(0x32 + i), b));
            Assert.All(r.Originals[i].Take(512), b => Assert.Equal(0, b));
            Assert.Equal((byte)(i + 2), slot.Bytes[1024]);
            Assert.Equal((byte)(0xa2 + i), slot.Bytes[^1]);
        }
        AssertUnchanged(before, r.Observe());
    }

    [Fact]
    public void CaptureAndSavedImageCopiesCannotMutateAnyLiveSlotOrDma()
    {
        var r = new Rig(); var before = r.Observe();
        var capture = r.Port.Capture();
        for (var i = 0; i < 4; i++)
        {
            var image = capture.Slots[i].Bytes.ToArray();
            image[0] ^= 0xff; image[^1] ^= 0xff;
            capture.Slots[i].Bytes[512] ^= 0xff;
            r.Originals[i][1024] ^= 0xff;
        }
        AssertUnchanged(before, r.Observe());
    }

    private static (byte Unit, HddSlotState Expected)[] Expectations(Rig r, params byte[] units) =>
        units.Select(unit => (unit, r.Port.CaptureHdd(unit))).ToArray();

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void AcknowledgesOnlySelectedDirtyFlagsWithoutChangingImagesOrDma(int count)
    {
        var r = new Rig();
        byte[] order = [5, 2, 4, 3];
        var units = order.Take(count).ToArray(); var saved = Expectations(r, units);
        var before = r.Observe();
        Assert.True(r.Port.TryAcknowledgeHddSaved(saved));
        AssertUnchanged(before, r.Observe(), units.Select(u => (int)u).ToArray());
        // Expectations remain independent dirty captures; acknowledgement must not rewrite them.
        Assert.All(saved, entry => Assert.True(entry.Expected.Dirty));
        for (var i = 0; i < 4; i++) Assert.All(r.Originals[i].Take(512), b => Assert.Equal(0, b));
        var clean = Expectations(r, units); var after = r.Observe();
        Assert.True(r.Port.TryAcknowledgeHddSaved(clean));
        AssertUnchanged(after, r.Observe());
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("low-unit")] [InlineData("high-unit")]
    [InlineData("unmounted")] [InlineData("stale")] [InlineData("protected")]
    [InlineData("protection-mismatch")] [InlineData("dirty-mismatch")]
    [InlineData("image-first")] [InlineData("image-last")] [InlineData("image-short")]
    [InlineData("null-state")] [InlineData("null-bytes")]
    public void LaterInvalidMemberPreservesAllEarlierValidAndUnrelatedState(string fault)
    {
        var r = new Rig();
        var saved = Expectations(r, 2, 3, 4, 5);
        var bad = saved[3].Expected;
        switch (fault)
        {
            case "duplicate": saved[3] = saved[0]; break;
            case "low-unit": saved[3].Unit = 1; break;
            case "high-unit": saved[3].Unit = 6; break;
            case "unmounted": r.Port.DetachHdd(5, discardDirty: true); break;
            case "stale": saved[3].Expected = bad with { Generation = bad.Generation - 1 }; break;
            case "protected":
                r.Port.AttachHdd(5, r.Originals[3], writeProtected: true, discardDirty: true);
                saved[3].Expected = r.Port.CaptureHdd(5);
                break;
            case "protection-mismatch": saved[3].Expected = bad with { WriteProtected = true }; break;
            case "dirty-mismatch": saved[3].Expected = bad with { Dirty = false }; break;
            case "image-first": bad.Bytes[0] ^= 0xff; break;
            case "image-last": bad.Bytes[^1] ^= 0xff; break;
            case "image-short": saved[3].Expected = bad with { Bytes = bad.Bytes[..^1] }; break;
            case "null-state": saved[3].Expected = null!; break;
            case "null-bytes": saved[3].Expected = bad with { Bytes = null! }; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        // Setup changes are finished before the baseline; refusal itself may change nothing.
        var before = r.Observe();
        Assert.False(r.Port.TryAcknowledgeHddSaved(saved));
        AssertUnchanged(before, r.Observe());
    }

    [Fact]
    public void EmptyAndOversizeBatchesPreserveEverySlot()
    {
        var r = new Rig(); var saved = Expectations(r, 2, 3, 4, 5);
        var before = r.Observe();
        Assert.False(r.Port.TryAcknowledgeHddSaved([]));
        AssertUnchanged(before, r.Observe());
        Assert.False(r.Port.TryAcknowledgeHddSaved([.. saved, saved[0]]));
        AssertUnchanged(before, r.Observe());
    }

    [Fact]
    public void WrongOwnerRefusesWithoutExceptionOrMutation()
    {
        var r = new Rig(); var saved = Expectations(r, 2, 3, 4, 5); var before = r.Observe();
        bool? result = null; Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = r.Port.TryAcknowledgeHddSaved(saved); }
            catch (Exception ex) { error = ex; }
        });
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(error); Assert.Equal(false, result);
        AssertUnchanged(before, r.Observe());
    }

    [Fact]
    public void DisabledControllerRefusesWithoutChangingDma()
    {
        var memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var scheduler = new DeviceScheduler();
        var fdc = new FloppyController(scheduler, 100, new(new(), Dsk.Create(new(40, 2, 9, 512))),
            new(new(), Dsk.Create(new(40, 2, 9, 512))));
        var port = new BlockDiskPort(memory, fdc);
        memory.Write(0x9000, 0x5a); var ram = memory.Ram.ToArray();
        Assert.False(port.TryAcknowledgeHddSaved([(2, new(new byte[ImageBytes], 1, false, true))]));
        Assert.Equal(ram, memory.Ram.ToArray()); Assert.Equal(0, port.Read(0x89, 0));
    }
}
