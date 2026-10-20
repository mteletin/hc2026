using System.Buffers.Binary;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Io;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class HddPortTests
{
#if INSTALLED_HDD_VALIDATION
    public HddPortTests(Xunit.Abstractions.ITestOutputHelper output) =>
        output.WriteLine(InstalledHddBinding.Ensure());
#endif

    private sealed class Rig
    {
        public HcMemory Memory { get; } = new(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        public DeviceScheduler Scheduler { get; } = new();
        public FloppyController Fdc { get; }
        public BlockDiskPort Port { get; }
        public Rig(bool hdd = true, bool instant = false)
        {
            Fdc = new(Scheduler, 100, new(new(), Dsk.Create(new(40, 2, 9, 512))),
                new(new(), Dsk.Create(new(40, 2, 9, 512))), instant: instant);
            Port = new(Memory, Fdc, hddEnabled: hdd);
        }
        public byte[] Dma(ushort at = 0x8100, int count = 512) =>
            Enumerable.Range(0, count).Select(i => Memory.Read((ushort)(at + i))).ToArray();
        public void FillDma(byte value)
        {
            for (var i = 0; i < 512; i++) Memory.Write((ushort)(0x8100 + i), value);
        }
        public void Attach(byte drive = 2, bool writeProtected = false) =>
            Port.AttachHdd(drive, new byte[8_388_608], writeProtected);
        public void AssertRejected(byte status, byte doorbell = 1)
        {
            var ram = Memory.Ram.ToArray(); var shadow = Memory.ShadowRam.ToArray();
            var rom = Memory.Rom.ToArray(); var shadowRom = Memory.ShadowRom.ToArray();
            var a = Fdc.DriveA.ExportMedia(); var b = Fdc.DriveB.ExportMedia();
            var dirtyA = Fdc.DriveA.DirtySectors.ToArray(); var dirtyB = Fdc.DriveB.DirtySectors.ToArray();
            var media = Port.Capture(); var fdc = Fdc.Fdc.Inspect(); var events = Scheduler.PendingEvents.ToArray();
            Port.Write(0x89, doorbell, 0);
            Assert.Equal(status, Port.Read(0x89, 0));
            Assert.Equal(ram, Memory.Ram.ToArray()); Assert.Equal(shadow, Memory.ShadowRam.ToArray());
            Assert.Equal(rom, Memory.Rom.ToArray()); Assert.Equal(shadowRom, Memory.ShadowRom.ToArray());
            Assert.Equal(a, Fdc.DriveA.ExportMedia()); Assert.Equal(b, Fdc.DriveB.ExportMedia());
            Assert.Equal(dirtyA, Fdc.DriveA.DirtySectors.ToArray()); Assert.Equal(dirtyB, Fdc.DriveB.DirtySectors.ToArray());
            var afterFdc = Fdc.Fdc.Inspect();
            Assert.Equal(fdc.Phase, afterFdc.Phase); Assert.Equal(fdc.MainStatus, afterFdc.MainStatus);
            Assert.Equal(fdc.Command.ToArray(), afterFdc.Command.ToArray());
            Assert.Equal(fdc.LastCommand.ToArray(), afterFdc.LastCommand.ToArray());
            Assert.Equal((fdc.LastSt0, fdc.LastSt1, fdc.LastSt2, fdc.LastSt3),
                (afterFdc.LastSt0, afterFdc.LastSt1, afterFdc.LastSt2, afterFdc.LastSt3));
            Assert.Equal(fdc.PresentCylinders.ToArray(), afterFdc.PresentCylinders.ToArray());
            Assert.Equal(fdc.PendingInterrupts?.ToArray(), afterFdc.PendingInterrupts?.ToArray());
            Assert.Equal(events, Scheduler.PendingEvents.ToArray());
            var after = Port.Capture(); Assert.Equal(media.Pointer, after.Pointer);
            for (var i = 0; i < media.Slots.Length; i++) AssertSlot(media.Slots[i], after.Slots[i]);
        }
        public PortDecoder Decoder(MachineProfile profile)
        {
            var idle = new PortDevice();
            return new(HcIoProfile.WithBlockDisk(profile, Memory, idle, idle, idle, idle, idle, idle, Port));
        }
        public byte Command(byte command = 3, byte drive = 2, uint lba = 0, ushort dma = 0x8100,
            uint generation = 0, ushort at = 0x8000, byte reserved = 0, byte doorbell = 1)
        {
            Prepare(command, drive, lba, dma, generation, at, reserved);
            Port.Write(0x89, doorbell, 0);
            return Port.Read(0x89, 0);
        }
        public void Prepare(byte command = 3, byte drive = 2, uint lba = 0, ushort dma = 0x8100,
            uint generation = 0, ushort at = 0x8000, byte reserved = 0, byte reserved10 = 0, byte reserved11 = 0)
        {
            Span<byte> data = stackalloc byte[16];
            data.Clear(); data[0] = 2; data[1] = command; data[2] = drive; data[3] = reserved;
            data[10] = reserved10; data[11] = reserved11;
            BinaryPrimitives.WriteUInt32LittleEndian(data[4..], lba);
            BinaryPrimitives.WriteUInt16LittleEndian(data[8..], dma);
            BinaryPrimitives.WriteUInt32LittleEndian(data[12..], generation);
            for (var i = 0; i < data.Length && at + i <= 0xffff; i++) Memory.Write((ushort)(at + i), data[i]);
            Port.Write(0x81, (byte)at, 0); Port.Write(0x83, (byte)(at >> 8), 0);
        }
    }

    private static void AssertSlot(HddSlotState expected, HddSlotState actual)
    {
        Assert.Equal(expected.Bytes, actual.Bytes); Assert.Equal(expected.Generation, actual.Generation);
        Assert.Equal(expected.WriteProtected, actual.WriteProtected); Assert.Equal(expected.Dirty, actual.Dirty);
    }

    [Fact]
    public void EmptySlotIdentifySucceedsAndClearsItsSixteenByteReply()
    {
        var r = new Rig();
        for (var i = 0; i < 512; i++) r.Memory.Write((ushort)(0x8100 + i), 0xa5);
        Assert.Equal(0, r.Command());
        Assert.All(Enumerable.Range(0, 16), i => Assert.Equal(0, r.Memory.Read((ushort)(0x8100 + i))));
        Assert.Equal(0xa5, r.Memory.Read(0x8110));
        Assert.Equal(5, r.Command(1));
    }

    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(3)] [InlineData(255)]
    public void VersionTwoOnlyExecutesWithDoorbellOne(byte doorbell)
    {
        var r = new Rig();
        Assert.Equal(1, r.Command(doorbell: doorbell));
        Assert.Equal(0, r.Memory.Read(0x8100));
    }

    [Fact]
    public void EmptyIdentifyIgnoresGenerationAndLbaButStillValidatesAll512DmaBytes()
    {
        var r = new Rig();
        Assert.Equal(0, r.Command(lba: uint.MaxValue, generation: uint.MaxValue));
        Assert.Equal(9, r.Command(dma: 0xfe01));
        Assert.Equal(9, r.Command(dma: 0x3fff));
        Assert.Equal(0, r.Command(dma: 0xfe00));
        Assert.Equal(5, r.Command(2, generation: uint.MaxValue, lba: uint.MaxValue, dma: 0xffff));
    }

    [Theory]
    [InlineData(0)] [InlineData(4)] [InlineData(255)]
    public void VersionAndDriveValidationPrecedeDescriptorCommand(byte command)
    {
        var r = new Rig();
        Assert.Equal(2, r.Command(command, drive: 0, reserved: 1));
        Assert.Equal(4, r.Command(command, drive: 0));
        Assert.Equal(1, r.Command(command));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(6)]
    public void VersionTwoNeverTargetsFloppiesOrDriveSix(byte drive)
    {
        var r = new Rig();
        Assert.Equal(4, r.Command(drive: drive));
    }

    [Fact]
    public void VersionTwoRequiresTheEntireSixteenBytePointerWindow()
    {
        var r = new Rig();
        Assert.Equal(3, r.Command(at: 0xfff1));
        Assert.Equal(0, r.Command(at: 0xfff0));
    }

    [Fact]
    public void RawCapacityWriteExportRemountAndGenerationRoundTrip()
    {
        var r = new Rig(); r.Attach();
        Assert.Equal(0, r.Command());
        var identify = r.Dma(count: 16);
        Assert.Equal(16384u, BinaryPrimitives.ReadUInt32LittleEndian(identify));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(identify.AsSpan(4)));
        Assert.Equal(3, identify[8]); Assert.All(identify.Skip(9), b => Assert.Equal(0, b));
        r.FillDma(0x5a);
        Assert.Equal(0, r.Command(2, generation: 1));
        Assert.True(r.Port.CaptureHdd(2).Dirty);
        var exported = r.Port.ExportHdd(2);
        Assert.Equal(8_388_608, exported.Length); Assert.False(r.Port.CaptureHdd(2).Dirty);
        r.Port.AttachHdd(2, exported);
        Assert.Equal(2u, r.Port.CaptureHdd(2).Generation);
        r.FillDma(0); Assert.Equal(0, r.Command(1, generation: 2));
        Assert.All(r.Dma(), b => Assert.Equal(0x5a, b));
        r.Prepare(2, generation: 1); r.AssertRejected(0x0d);
    }

    // Separate decoder proof: existing direct-controller helpers remain unchanged.
    [Fact]
    public void DecoderWriteExportRemountReadPreservesIndependentPayloadAndRefusesStaleGeneration()
    {
        var r = new Rig(); r.Attach();
        var decoder = r.Decoder(MachineProfile.Hc2000V2HddV1);
        byte Execute(byte command, uint generation)
        {
            Span<byte> descriptor = stackalloc byte[16];
            descriptor.Clear(); descriptor[0] = 2; descriptor[1] = command; descriptor[2] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor[8..], 0x8100);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[12..], generation);
            for (var i = 0; i < descriptor.Length; i++) r.Memory.Write((ushort)(0x8000 + i), descriptor[i]);
            decoder.Write(0x81, 0, 0); decoder.Write(0x83, 0x80, 0);
            decoder.Write(0x89, 1, 0);
            return decoder.Read(0x89, 0);
        }

        r.FillDma(0x5a); Assert.Equal(0, Execute(2, 1));
        var exported = r.Port.ExportHdd(2);
        Assert.Equal(8_388_608, exported.Length);
        Assert.All(exported.Take(512), b => Assert.Equal(0x5a, b));
        Assert.All(exported.Skip(512), b => Assert.Equal(0, b));
        r.Port.AttachHdd(2, exported); Assert.Equal(2u, r.Port.CaptureHdd(2).Generation);
        r.FillDma(0); Assert.Equal(0, Execute(1, 2));
        Assert.All(r.Dma(), b => Assert.Equal(0x5a, b));

        // Same descriptor apart from command/generation: status-only refusal.
        r.Memory.Write(0x8001, 2); r.Memory.Write(0x800c, 1);
        var ram = r.Memory.Ram.ToArray(); var shadow = r.Memory.ShadowRam.ToArray();
        var rom = r.Memory.Rom.ToArray(); var shadowRom = r.Memory.ShadowRom.ToArray();
        var before = r.Port.Capture();
        decoder.Write(0x89, 1, 0); Assert.Equal(0x0d, decoder.Read(0x89, 0));
        Assert.Equal(ram, r.Memory.Ram.ToArray()); Assert.Equal(shadow, r.Memory.ShadowRam.ToArray());
        Assert.Equal(rom, r.Memory.Rom.ToArray()); Assert.Equal(shadowRom, r.Memory.ShadowRom.ToArray());
        var after = r.Port.Capture(); Assert.Equal(before.Pointer, after.Pointer);
        for (var i = 0; i < before.Slots.Length; i++) AssertSlot(before.Slots[i], after.Slots[i]);
    }

    [Theory]
    [InlineData(0u)] [InlineData(16383u)]
    public void FirstAndLastLbaTransferExactlyOneSector(uint lba)
    {
        var r = new Rig(); r.Attach(); r.FillDma(0x7a);
        Assert.Equal(0, r.Command(2, lba: lba, generation: 1));
        var bytes = r.Port.ExportHdd(2); var offset = (int)lba * 512;
        Assert.All(bytes.Take(offset), b => Assert.Equal(0, b));
        Assert.All(bytes.Skip(offset).Take(512), b => Assert.Equal(0x7a, b));
        Assert.All(bytes.Skip(offset + 512), b => Assert.Equal(0, b));
        r.FillDma(0); Assert.Equal(0, r.Command(1, lba: lba, generation: 1));
        Assert.All(r.Dma(), b => Assert.Equal(0x7a, b));
    }

    [Theory]
    [InlineData(1, 16384u)] [InlineData(2, 16384u)]
    [InlineData(1, uint.MaxValue)] [InlineData(2, uint.MaxValue)]
    public void OutOfRangeNeverMutatesRamOrMedia(byte command, uint lba)
    {
        var r = new Rig(); r.Attach(); r.FillDma(0x55);
        r.Prepare(command, lba: lba, generation: 1); r.AssertRejected(0x0c);
    }

    [Theory]
    [InlineData(0)] [InlineData(512)] [InlineData(8_388_607)] [InlineData(8_388_609)]
    public void TruncatedOrOversizedAttachLeavesPreviousGenerationAndBytes(int size)
    {
        var r = new Rig(); r.Attach(); var before = r.Port.CaptureHdd(2);
        Assert.Throws<ArgumentException>(() => r.Port.AttachHdd(2, new byte[size], writeProtected: true));
        AssertSlot(before, r.Port.CaptureHdd(2));
    }

    [Fact]
    public void WriteProtectSurvivesResetAndIdentifySkipsReadWriteChecks()
    {
        var r = new Rig(); r.Attach(writeProtected: true); r.FillDma(0x9b);
        r.Prepare(2, generation: 1); r.AssertRejected(8);
        r.Port.Reset(); Assert.True(r.Port.CaptureHdd(2).WriteProtected);
        r.Prepare(2, generation: 1); r.AssertRejected(8);
        Assert.Equal(0, r.Command(lba: uint.MaxValue, generation: uint.MaxValue));
        Assert.Equal(1, r.Dma(count: 16)[8]);
        Assert.Equal(0, r.Command(1, generation: 1));
    }

    [Fact]
    public void WritingOneSlotDoesNotAlterTheOtherThree()
    {
        var r = new Rig(); var original = new byte[8_388_608];
        for (byte drive = 2; drive <= 5; drive++) r.Port.AttachHdd(drive, original);
        var others = Enumerable.Range(3, 3).Select(d => r.Port.CaptureHdd((byte)d)).ToArray();
        r.FillDma(0xe5); Assert.Equal(0, r.Command(2, generation: 1));
        for (byte drive = 3; drive <= 5; drive++) AssertSlot(others[drive - 3], r.Port.CaptureHdd(drive));
        original[0] = 0xfa; Assert.Equal(0xe5, r.Port.CaptureHdd(2).Bytes[0]);
    }

    [Fact]
    public void DirtyDetachAndReplacementRequireExportOrExplicitDiscard()
    {
        var r = new Rig(); r.Attach(); r.FillDma(0x67); Assert.Equal(0, r.Command(2, generation: 1));
        var dirty = r.Port.CaptureHdd(2);
        Assert.Throws<InvalidOperationException>(() => r.Port.DetachHdd(2));
        Assert.Throws<InvalidOperationException>(() => r.Port.AttachHdd(2, new byte[8_388_608]));
        AssertSlot(dirty, r.Port.CaptureHdd(2));
        r.Port.DetachHdd(2, discardDirty: true);
        Assert.Equal(0, r.Command(lba: uint.MaxValue, generation: uint.MaxValue));
        var id = r.Dma(count: 16);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(id));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(id.AsSpan(4)));
        Assert.Equal(0, id[8]);
        r.Attach(); Assert.Equal(2u, r.Port.CaptureHdd(2).Generation);
        r.FillDma(0x22); Assert.Equal(0, r.Command(2, generation: 2));
        r.Port.AttachHdd(2, new byte[8_388_608], discardDirty: true);
        Assert.Equal(3u, r.Port.CaptureHdd(2).Generation);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void AllCommandsValidateFullWritableDmaWithoutMutation(byte command)
    {
        var r = new Rig(); r.Attach();
        foreach (ushort dma in new ushort[] { 0xfe01, 0xffff, 0x3fff, 0x0000 })
        {
            r.Prepare(command, generation: 1, dma: dma); r.AssertRejected(9);
        }
        r.Memory.WritePort7E(2); // Top ROM is now mapped into the last DMA page.
        r.Prepare(command, generation: 1, dma: 0xfe00); r.AssertRejected(9);
    }

    [Theory]
    [InlineData(3)] [InlineData(10)] [InlineData(11)]
    public void EveryReservedByteIsCheckedBeforeDriveOrMedia(int offset)
    {
        var r = new Rig(); r.Attach(); r.Prepare(drive: 0);
        r.Memory.Write((ushort)(0x8000 + offset), 1); r.AssertRejected(2);
    }

    [Fact]
    public void ErrorPrecedenceDoorbellPointerReservedDriveCommandPresentGenerationLbaProtectDma()
    {
        var r = new Rig(); r.Attach(writeProtected: true);
        r.Prepare(0, at: 0xfff1, reserved: 1); r.AssertRejected(3);
        r.Prepare(0, reserved: 1, drive: 0); r.AssertRejected(2);
        r.Prepare(0, drive: 0); r.AssertRejected(4);
        r.Prepare(0); r.AssertRejected(1);
        r.Prepare(1, at: 0xfff1, reserved: 1); r.AssertRejected(3);
        r.Prepare(1, reserved: 1, drive: 0); r.AssertRejected(2);
        r.Prepare(1, drive: 0); r.AssertRejected(4);
        r.Prepare(2, drive: 3, generation: 9, lba: uint.MaxValue, dma: 0xffff); r.AssertRejected(5);
        r.Prepare(2, generation: 9, lba: uint.MaxValue, dma: 0xffff); r.AssertRejected(0x0d);
        r.Prepare(2, generation: 1, lba: uint.MaxValue, dma: 0xffff); r.AssertRejected(0x0c);
        r.Prepare(2, generation: 1, dma: 0xffff); r.AssertRejected(8);
        r.Prepare(1, generation: 1, dma: 0xffff); r.AssertRejected(9);
        r.Prepare(2, generation: 1); r.AssertRejected(1, doorbell: 2);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HddIgnoresFdcBusyButVersionOneFloppyStillRequiresIdle(bool instant)
    {
        var r = new Rig(instant: instant); r.Attach(); r.Fdc.WriteLatch(0x10); r.Fdc.Fdc.WriteData(3);
        var msr = r.Fdc.Fdc.Inspect().MainStatus;
        Assert.Equal(0, r.Command(1, generation: 1));
        Assert.Equal(msr, r.Fdc.Fdc.Inspect().MainStatus);
        byte[] floppy = [1, 0, 0, 0, 1, 0, 0x81, 0];
        for (var i = 0; i < 8; i++) r.Memory.Write((ushort)(0x8000 + i), floppy[i]);
        r.AssertRejected(0x0b);
        r.Fdc.WriteLatch(0); r.Fdc.WriteLatch(0x10); r.Port.Write(0x89, 1, 0); Assert.Equal(0, r.Port.Read(0x89, 0));
        r.FillDma(0x34); r.Port.Write(0x89, 2, 0); Assert.Equal(0, r.Port.Read(0x89, 0));
        r.FillDma(0); r.Port.Write(0x89, 1, 0); Assert.All(r.Dma(), b => Assert.Equal(0x34, b));
    }

    [Fact]
    public void FlagOffVersionTwoRemainsInvalidAndCannotAttachHdd()
    {
        var r = new Rig(hdd: false);
        Assert.Equal(2, r.Command()); Assert.Equal(2, r.Command(2, doorbell: 2));
        Assert.Throws<InvalidOperationException>(() => r.Attach());
        Assert.Throws<ArgumentException>(() => r.Decoder(MachineProfile.Hc2000V2HddV1));
    }

    [Fact]
    public void HddProfileIsExactOnlyBlockportRetainsAllAliasesAndFaithfulRowsAreUnchanged()
    {
        var r = new Rig(); var profile = MachineProfile.Hc2000V2HddV1; profile.Validate();
        Assert.Equal("hc2000-v2-hdd-v1", profile.Name);
        var decoder = r.Decoder(profile); r.Prepare();
        for (var high = 1; high <= 255; high++)
            foreach (ushort low in new ushort[] { 0x81, 0x83, 0x89 })
            {
                var port = (ushort)((high << 8) | low);
                decoder.Write(port, 0xff, 0); Assert.Equal(0xff, decoder.Read(port, 0));
                Assert.Equal(0, r.Port.Read(0x81, 0)); Assert.Equal(0x80, r.Port.Read(0x83, 0));
                Assert.Equal(0, r.Port.Read(0x89, 0));
            }
        decoder.Write(0x89, 1, 0); Assert.Equal(0, decoder.Read(0x89, 0));
        var v1 = new Rig(hdd: false); var legacy = v1.Decoder(MachineProfile.Hc2000V2BlockportV1);
        for (var high = 0; high <= 255; high++)
        {
            legacy.Write((ushort)((high << 8) | 0x81), 0x56, 0);
            legacy.Write((ushort)((high << 8) | 0x83), 0x78, 0);
            Assert.Equal(0x56, legacy.Read((ushort)((high << 8) | 0x81), 0));
            Assert.Equal(0x78, legacy.Read((ushort)((high << 8) | 0x83), 0));
            legacy.Write((ushort)((high << 8) | 0x89), 0, 0);
            Assert.Equal(1, legacy.Read((ushort)((high << 8) | 0x89), 0));
        }
        var idle = new PortDevice();
        var faithful = HcIoProfile.Faithful(r.Memory, idle, idle, idle, idle, idle, idle);
        var extended = HcIoProfile.WithBlockDisk(profile, r.Memory, idle, idle, idle, idle, idle, idle, r.Port);
        Assert.Equal(faithful.Select(row => (row.Match, row.Mask, row.Access)),
            extended.Take(faithful.Count).Select(row => (row.Match, row.Mask, row.Access)));
        Assert.All(extended.Skip(faithful.Count), row => Assert.Equal(0xffff, row.Mask));
        for (var p = 0; p <= ushort.MaxValue; p++)
            foreach (var row in faithful)
                if (((ushort)p & row.Mask) == row.Match)
                    Assert.DoesNotContain(extended.Skip(faithful.Count), extra => ((ushort)p & extra.Mask) == extra.Match);
        Assert.Throws<ArgumentException>(() => (profile with { BlockDisk = BlockDiskPolicy.V1 }).Validate());
        Assert.Throws<ArgumentException>(() => (MachineProfile.Hc2000V2 with { BlockDisk = BlockDiskPolicy.HddV1 }).Validate());
        Assert.Throws<ArgumentException>(() => r.Decoder(MachineProfile.Hc2000V2BlockportV1));
    }

    [Fact]
    public void DescriptorMayOverlapReadDmaBecauseAllFieldsAreCapturedFirst()
    {
        var r = new Rig(); r.Attach(); r.FillDma(0x44);
        Assert.Equal(0, r.Command(2, generation: 1));
        Assert.Equal(0, r.Command(1, generation: 1, dma: 0x8000));
        Assert.All(r.Dma(0x8000), b => Assert.Equal(0x44, b));
    }

    [Fact]
    public void SnapshotRoundTripOwnsSlotBytesFlagsGenerationsAndPortLatches()
    {
        var r = new Rig(); r.Attach(); r.Attach(3, writeProtected: true); r.Attach(4); r.Port.DetachHdd(4);
        r.FillDma(0x7c); Assert.Equal(0, r.Command(2, generation: 1));
        r.Prepare(1, generation: 999); r.AssertRejected(0x0d);
        var captured = r.Port.Capture();
        var loaded = new Rig(); loaded.Port.Restore(captured);
        Assert.Equal(0x0d, loaded.Port.Read(0x89, 0)); Assert.Equal(0x80, loaded.Port.Read(0x83, 0));
        for (byte d = 2; d <= 5; d++) AssertSlot(r.Port.CaptureHdd(d), loaded.Port.CaptureHdd(d));
        captured.Slots[0].Bytes[0] = 0xff;
        Assert.Equal(0x7c, loaded.Port.CaptureHdd(2).Bytes[0]);
        Assert.Equal(0x7c, r.Port.CaptureHdd(2).Bytes[0]);
        var exported = loaded.Port.ExportHdd(2); exported[0] = 0xff;
        Assert.Equal(0x7c, loaded.Port.CaptureHdd(2).Bytes[0]);
    }

    [Theory]
    [InlineData("short")] [InlineData("empty-protected")] [InlineData("empty-dirty")]
    [InlineData("present-zero-generation")] [InlineData("slots")] [InlineData("null-bytes")]
    public void FailedSnapshotRestoreDoesNotChangeAnyLiveSlotOrLatch(string defect)
    {
        var r = new Rig(); r.Attach(); r.Prepare(0); r.AssertRejected(1);
        var before = r.Port.Capture(); var bad = r.Port.Capture();
        // Corrupt the last slot so validation must not adopt earlier valid slots.
        var corrupt = defect switch
        {
            "short" => new HddSlotState(new byte[512], 1, false, false),
            "empty-protected" => new([], 0, true, false),
            "empty-dirty" => new([], 0, false, true),
            "present-zero-generation" => new(new byte[8_388_608], 0, false, false),
            "null-bytes" => new(null!, 0, false, false),
            _ => new HddSlotState([], 0, false, false)
        };
        bad.Slots[3] = corrupt;
        bad = defect == "slots" ? bad with { Slots = bad.Slots[..3] } : bad with { Pointer = 123, Status = 7 };
        Assert.ThrowsAny<ArgumentException>(() => r.Port.Restore(bad));
        var after = r.Port.Capture(); Assert.Equal(before.Pointer, after.Pointer); Assert.Equal(before.Status, after.Status);
        for (var i = 0; i < 4; i++) AssertSlot(before.Slots[i], after.Slots[i]);
    }

    [Fact]
    public void AbsentSnapshotIsEmptyOnHddAndHddSnapshotIsRejectedOnBlockport()
    {
        var r = new Rig(); r.Attach(); r.Prepare(0); r.AssertRejected(1);
        var captured = r.Port.Capture();
        var blockport = new Rig(hdd: false);
        Assert.Throws<InvalidOperationException>(() => blockport.Port.Restore(captured));
        Assert.Throws<InvalidOperationException>(() => blockport.Port.Capture());
        r.Port.Restore(null);
        var empty = r.Port.Capture(); Assert.Equal(0, empty.Pointer); Assert.Equal(0, empty.Status);
        Assert.All(empty.Slots, s => { Assert.Empty(s.Bytes); Assert.Equal(0u, s.Generation); Assert.False(s.Dirty); Assert.False(s.WriteProtected); });
    }

    [Fact]
    public void AttachGenerationOverflowIsRefusedWithoutAliasingAnOldIdentity()
    {
        var r = new Rig(); var state = r.Port.Capture();
        state.Slots[0] = new([], uint.MaxValue, false, false); r.Port.Restore(state);
        Assert.Throws<OverflowException>(() => r.Attach()); Assert.Equal(uint.MaxValue, r.Port.CaptureHdd(2).Generation);
        Assert.Empty(r.Port.CaptureHdd(2).Bytes);
    }

    [Fact]
    public void HostMediaAndSnapshotCallsRequireTheEmulationOwner()
    {
        var r = new Rig();
        Exception? attachError = null, captureError = null;
        var thread = new Thread(() =>
        {
            attachError = Record.Exception(() => r.Attach());
            captureError = Record.Exception(() => r.Port.Capture());
        });
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(attachError);
        Assert.IsType<InvalidOperationException>(captureError);
    }

    [Fact]
    public void Full512ByteDmaEndingAtFfffIsValidForReadWriteAndIdentify()
    {
        var r = new Rig(); r.Attach();
        for (var i = 0; i < 512; i++) r.Memory.Write((ushort)(0xfe00 + i), (byte)i);
        Assert.Equal(0, r.Command(2, generation: 1, dma: 0xfe00));
        var expected = r.Dma(0xfe00);
        Assert.Equal(0, r.Command(1, generation: 1)); Assert.Equal(expected, r.Dma());
        Assert.Equal(0, r.Command(dma: 0xfe00));
        Assert.Equal(16384u, BinaryPrimitives.ReadUInt32LittleEndian(r.Dma(0xfe00, 16)));
        Assert.Equal(expected.Skip(16), r.Dma(0xfe10, 496));
    }

    [Fact]
    public void V1AtFff8StillWorksOnHddAndV1CannotTargetHddSlots()
    {
        var r = new Rig(); r.Attach();
        byte[] descriptor = [1, 1, 0, 0, 1, 0, 0x81, 0];
        for (var i = 0; i < 8; i++) r.Memory.Write((ushort)(0xfff8 + i), descriptor[i]);
        r.Port.Write(0x81, 0xf8, 0); r.Port.Write(0x83, 0xff, 0);
        r.Port.Write(0x89, 1, 0); Assert.Equal(0, r.Port.Read(0x89, 0));
        foreach (byte drive in new byte[] { 2, 3, 4, 5 })
        {
            r.Memory.Write(0xfff9, drive); r.AssertRejected(4);
        }
    }
}
