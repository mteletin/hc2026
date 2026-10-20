using Hc2000.Machine.Floppy;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class BlockDiskPortTests
{
    private sealed class Rig
    {
        public HcMemory Memory { get; } = new(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        public DeviceScheduler Scheduler { get; } = new();
        public FloppyDrive A { get; } = new(new(), Dsk.Create(new(40, 2, 9, 512)));
        public FloppyDrive B { get; } = new(new(), Dsk.Create(new(40, 2, 9, 512)));
        public FloppyController Controller { get; }
        public BlockDiskPort Device { get; }
        public PortDecoder Ports { get; }
        public Rig(bool enabled = true, bool instant = false)
        {
            Controller = new(Scheduler, 100, A, B, instant: instant);
            Device = new(Memory, Controller);
            var idle = new PortDevice();
            var faithful = HcIoProfile.Faithful(Memory, idle, idle, idle, idle, idle, idle);
            // Isolated synthetic test composition, NOT a registered or executable product profile.
            Ports = new(enabled ? faithful.Concat(Device.Mappings()) : faithful);
            Command();
            Pointer(0x8000);
        }
        public byte Status => Ports.Read(0x89, Scheduler.Now);
        public void Pointer(ushort value, int high = 0)
        {
            Ports.Write((ushort)(high | 0x81), (byte)value, Scheduler.Now);
            Ports.Write((ushort)(high | 0x83), (byte)(value >> 8), Scheduler.Now);
        }
        public void Command(byte version = 1, byte drive = 0, byte cylinder = 0, byte side = 0,
            byte sector = 1, ushort dma = 0x8100, byte reserved = 0, ushort at = 0x8000)
        {
            byte[] data = [version, drive, cylinder, side, sector, (byte)dma, (byte)(dma >> 8), reserved];
            for (var i = 0; i < data.Length; i++) Memory.Write((ushort)(at + i), data[i]);
        }
        public void Ring(byte operation = 1, int high = 0) => Ports.Write((ushort)(high | 0x89), operation, Scheduler.Now);
        public void Reject(byte expected, byte operation = 1)
        {
            var ram = Memory.Ram.ToArray(); var shadow = Memory.ShadowRam.ToArray();
            var rom = Memory.Rom.ToArray(); var shadowRom = Memory.ShadowRom.ToArray();
            var a = A.Image is null ? null : A.ExportMedia(); var b = B.Image is null ? null : B.ExportMedia();
            var dirtyA = A.DirtySectors.ToArray(); var dirtyB = B.DirtySectors.ToArray();
            var fdc = Controller.Fdc.Inspect().MainStatus; var latch = Controller.Latch;
            var latches = Memory.Latches; var time = Scheduler.Now;
            var lo = Ports.Read(0x81, time); var hi = Ports.Read(0x83, time);
            Ring(operation);
            Assert.Equal(expected, Status); Assert.Equal(expected, Status);
            Assert.Equal(ram, Memory.Ram.ToArray()); Assert.Equal(shadow, Memory.ShadowRam.ToArray());
            Assert.Equal(rom, Memory.Rom.ToArray()); Assert.Equal(shadowRom, Memory.ShadowRom.ToArray());
            Assert.Equal(a, A.Image is null ? null : A.ExportMedia());
            Assert.Equal(b, B.Image is null ? null : B.ExportMedia());
            Assert.Equal(dirtyA, A.DirtySectors.ToArray()); Assert.Equal(dirtyB, B.DirtySectors.ToArray());
            Assert.Equal(fdc, Controller.Fdc.Inspect().MainStatus); Assert.Equal(latch, Controller.Latch);
            Assert.Equal(latches, Memory.Latches); Assert.Equal(time, Scheduler.Now);
            Assert.Equal(lo, Ports.Read(0x81, time)); Assert.Equal(hi, Ports.Read(0x83, time));
            Assert.Equal(0, A.HeadPosition); Assert.Equal(0, B.HeadPosition);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EnabledVectorUsesEveryHighAliasAndPreservesController(bool instant)
    {
        var r = new Rig(instant: instant);
        var sector = r.A.Image!.Tracks[0].Sectors[0].Data;
        for (var i = 0; i < sector.Length; i++) sector[i] = (byte)i;
        r.Device.Reset();
        for (var high = 0; high < 0x10000; high += 0x100)
        {
            Assert.Equal(0, r.Ports.Read((ushort)(high | 0x81), 0));
            Assert.Equal(0, r.Ports.Read((ushort)(high | 0x83), 0));
            Assert.Equal(0, r.Ports.Read((ushort)(high | 0x89), 0));
        }
        var command = r.Memory.Ram.Slice(0x8000, 8).ToArray();
        var exportedB = r.B.ExportMedia(); var fdc = r.Controller.Fdc.ReadStatus();
        for (var high = 0; high < 0x10000; high += 0x100)
        {
            r.Pointer(0x8000, high);
            Assert.Equal(0x80, r.Ports.Read((ushort)(high | 0x83), 0));
            r.Ring(high: high);
            Assert.Equal(0, r.Status);
            Assert.Equal(sector, r.Memory.Ram.Slice(0x8100, 512).ToArray());
            Assert.Equal(command, r.Memory.Ram.Slice(0x8000, 8).ToArray());
        }
        r.Memory.Ram.Span[0x8100..0x8300].Fill(0x3a);
        r.Ring(2);
        Assert.Equal(0, r.Status);
        Assert.All(sector, b => Assert.Equal(0x3a, b));
        Assert.Equal(new DirtySector(0, 0, 0), Assert.Single(r.A.DirtySectors));
        Assert.Equal(r.A.ExportMedia(), Dsk.Load(r.A.ExportMedia()).Save());
        Assert.Equal(exportedB, r.B.ExportMedia());
        Assert.Equal(fdc, r.Controller.Fdc.ReadStatus());
        Assert.Equal(0, r.Controller.Latch); Assert.False(r.A.MotorOn); Assert.False(r.A.HeadLoaded);
        Assert.Equal(0, r.A.HeadPosition); Assert.Equal(0, r.Scheduler.Now);
    }

    [Theory]
    [InlineData("operation", 1)] [InlineData("pointer-wrap", 3)] [InlineData("pointer-rom", 3)]
    [InlineData("pointer-cross-rom", 3)] [InlineData("version", 2)] [InlineData("reserved", 2)]
    [InlineData("drive", 4)] [InlineData("busy", 11)] [InlineData("media", 5)]
    [InlineData("cylinder", 6)] [InlineData("side", 6)] [InlineData("sector", 6)]
    [InlineData("size", 7)] [InlineData("protected", 8)] [InlineData("media-error", 10)]
    [InlineData("dma-wrap", 9)] [InlineData("dma-rom", 9)] [InlineData("dma-cross-rom", 9)]
    public void ErrorsHaveFixedWireCodesAndNoSideEffects(string defect, int expected)
    {
        var r = new Rig(); byte operation = 1;
        switch (defect)
        {
            case "operation": operation = 0; r.Pointer(0xffff); break; // operation wins over pointer.
            case "pointer-wrap": r.Pointer(0xfff9); break;
            case "pointer-rom": r.Pointer(0); break;
            case "pointer-cross-rom": r.Memory.WritePort7E(2); r.Pointer(0xdff9); break;
            case "version": r.Command(version: 2, drive: 2); break;
            case "reserved": r.Command(reserved: 1, drive: 2); break;
            case "drive": r.Command(drive: 2); r.Controller.WriteLatch(0x10); r.Controller.Fdc.WriteData(3); break;
            case "busy": r.Controller.WriteLatch(0x10); r.Controller.Fdc.WriteData(3); r.A.Mount(null); break;
            case "media": r.A.Mount(null); break;
            case "cylinder": r.Command(cylinder: 40); break;
            case "side": r.Command(side: 2); break;
            case "sector": r.Command(sector: 0); break;
            case "size": r.A.Mount(Dsk.Create(new(40, 2, 16, 256))); break;
            case "protected": r.A.WriteProtected = true; r.Command(dma: 0xffff); operation = 2; break;
            case "media-error":
                var image = r.A.ExportMedia(); image[0x100 + 0x18 + 4] = 0x20; r.A.Mount(Dsk.Load(image));
                r.Command(dma: 0xffff); break;
            case "dma-wrap": r.Command(dma: 0xfe01); break;
            case "dma-rom": r.Command(dma: 0); break;
            case "dma-cross-rom": r.Memory.WritePort7E(2); r.Command(dma: 0xdf00); break;
        }
        r.Reject((byte)expected, operation);
    }

    [Fact]
    public void PointerChangesAndReadsDoNotClearStatusResetDoesNotTouchRamMediaOrFdc()
    {
        var r = new Rig(); r.Reject(1, 0);
        r.Pointer(0x1234);
        Assert.Equal(0x34, r.Ports.Read(0x81, 0)); Assert.Equal(0x12, r.Ports.Read(0x83, 0));
        Assert.Equal(1, r.Status);
        var ram = r.Memory.Ram.ToArray(); var disk = r.A.ExportMedia();
        r.Controller.WriteLatch(0x10); r.Controller.Fdc.WriteData(3);
        var busy = r.Controller.Fdc.ReadStatus();
        r.Device.Reset();
        Assert.Equal(0, r.Status); Assert.Equal(0, r.Ports.Read(0x81, 0)); Assert.Equal(0, r.Ports.Read(0x83, 0));
        Assert.Equal(busy, r.Controller.Fdc.ReadStatus()); Assert.Equal(0x10, r.Controller.Latch);
        Assert.Equal(ram, r.Memory.Ram.ToArray()); Assert.Equal(disk, r.A.ExportMedia());
    }

    [Fact]
    public void DescriptorAndDmaOverlapUsesCapturedCommandIncludingTopValidPointer()
    {
        var r = new Rig();
        r.Command(drive: 1, dma: 0x8000);
        r.Ring(); // READ overwrites descriptor as ordinary destination bytes.
        Assert.Equal(0, r.Status);
        Assert.All(r.Memory.Ram.Slice(0x8000, 512).ToArray(), b => Assert.Equal(0xe5, b));
        r.Reject(2); // Next transaction sees overwritten invalid version, not cached descriptor.
        r.Command(drive: 1, dma: 0xfe00, at: 0xfff8); r.Pointer(0xfff8);
        r.Ring(2);
        Assert.Equal(0, r.Status);
        Assert.Equal(r.Memory.Ram.Slice(0xfe00, 512).ToArray(), r.B.Image!.Tracks[0].Sectors[0].Data);
        Assert.Empty(r.A.DirtySectors);
    }

    [Fact]
    public void MappedShadowDescriptorAndDmaUseMirrorsAndCrossingShadowRomRejects()
    {
        var r = new Rig(); r.Memory.SetShadow(true);
        r.Command(dma: 0x3800, at: 0x2800); r.Pointer(0x2800);
        r.Ring();
        Assert.Equal(0, r.Status);
        Assert.All(r.Memory.ShadowRam.Slice(0, 512).ToArray(), b => Assert.Equal(0xe5, b));
        r.Command(dma: 0x2f00, at: 0x2b00); r.Pointer(0x2b00);
        r.Reject(9);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FaithfulProfilesStayAbsentAndOnlyIsolatedDeviceCompositionEnables(bool v1)
    {
        var faithful = v1 ? MachineProfile.Hc2000V1 : MachineProfile.Hc2000V2;
        faithful.Validate();
        var r = new Rig(enabled: false);
        for (var high = 0; high < 0x10000; high += 0x100)
        foreach (var low in new[] { 0x81, 0x83, 0x89 })
        {
            r.Ports.Write((ushort)(high | low), 1, 0);
            Assert.Equal(0xff, r.Ports.Read((ushort)(high | low), 0));
        }
        Assert.Empty(r.A.DirtySectors);
        Assert.Equal(v1 ? "hc2000-v1" : "hc2000-v2", faithful.Name);
        Assert.Throws<ArgumentException>(() => (faithful with { Name = faithful.Name + "-blockport-v1" }).Validate());
        var live = new Rig(); live.Ring(); Assert.Equal(0, live.Status);
        Assert.Throws<ArgumentException>(() => new PortDecoder(live.Device.Mappings()
            .Concat(live.Device.Mappings())));
    }

    [Fact]
    public void ExplicitV2BlockportProfileEnablesOnlyItsTypedPolicy()
    {
        var profile = MachineProfile.Hc2000V2BlockportV1;
        profile.Validate();
        Assert.Equal("hc2000-v2-blockport-v1", profile.Name);
        Assert.Equal(BlockDiskPolicy.V1, profile.BlockDisk);
        Assert.Throws<ArgumentException>(() => (profile with { BlockDisk = null }).Validate());
        Assert.Throws<ArgumentException>(() => (MachineProfile.Hc2000V2 with { BlockDisk = BlockDiskPolicy.V1 }).Validate());

        var rig = new Rig(enabled: false);
        var idle = new PortDevice();
        var mappings = HcIoProfile.WithBlockDisk(profile, rig.Memory, idle, idle, idle, idle, idle, idle, rig.Device);
        var decoder = new PortDecoder(mappings);
        Assert.Equal(0, decoder.Read(0x0081, 0));
        Assert.Equal(0, decoder.Read(0x0083, 0));
        Assert.Equal(0, decoder.Read(0x0089, 0));
    }

    [Theory]
    [InlineData("partial", false)] [InlineData("result", false)] [InlineData("seek", false)]
    [InlineData("partial", true)] [InlineData("result", true)]
    public void AllNonIdleControllerStatesAreRejectedWithoutConsumingResultsOrEvents(string state, bool instant)
    {
        var r = new Rig(instant: instant); r.Controller.WriteLatch(0x10);
        switch (state)
        {
            case "partial": r.Controller.Fdc.WriteData(3); break;
            case "result": r.Controller.Fdc.WriteData(0xff); break;
            case "seek": FdcTests.Send(r.Controller.Fdc, 15, 0, 3); break;
        }
        var events = r.Scheduler.PendingEvents.ToArray();
        var before = r.Controller.Fdc.Inspect();
        r.Reject(0x0b);
        Assert.Equal(before.MainStatus, r.Controller.Fdc.Inspect().MainStatus);
        Assert.Equal(events, r.Scheduler.PendingEvents.ToArray());
        if (state == "result") Assert.Equal(0x80, r.Controller.Fdc.ReadData());
    }

    [Fact]
    public void FaithfulExecutionRejectsWithoutConsumingDataOrChangingScheduledEvents()
    {
        var r = new Rig(); r.Controller.WriteLatch(0x1a);
        FdcTests.Send(r.Controller.Fdc, 0x46, 0, 0, 0, 1, 2, 1, 0x2a, 0xff);
        FdcTests.AdvanceUntilReady(r.Scheduler, r.Controller.Fdc);
        Assert.Equal(0xf0, r.Controller.Fdc.Inspect().MainStatus);
        var events = r.Scheduler.PendingEvents.ToArray();
        r.Reject(0x0b);
        Assert.Equal(events, r.Scheduler.PendingEvents.ToArray());
        Assert.Equal(0xe5, r.Controller.Fdc.ReadData()); // Guard did not steal the ready byte.
    }

    [Fact]
    public void GuardDoesNotTriggerInstantMsrPollingSideEffects()
    {
        var r = new Rig(instant: true); r.Controller.WriteLatch(0x1a);
        FdcTests.Send(r.Controller.Fdc, 0x46, 0, 0, 0, 1, 2, 1, 0x2a, 0xff);
        Assert.Equal(0xf0, r.Controller.Fdc.ReadStatus());
        Assert.Equal(0xe5, r.Controller.Fdc.ReadData());
        Assert.Equal(0x70, r.Controller.Fdc.Inspect().MainStatus);
        var events = r.Scheduler.PendingEvents.ToArray();
        r.Reject(0x0b);
        Assert.Equal(0x70, r.Controller.Fdc.Inspect().MainStatus); // No next byte was offered.
        Assert.Equal(events, r.Scheduler.PendingEvents.ToArray());
    }

    [Fact]
    public void BusTickMustAlreadyBeDrainedAndAccessIsOwnerOnly()
    {
        var r = new Rig();
        Assert.Throws<InvalidOperationException>(() => r.Device.Write(0x89, 1, 1));
        Exception? error = null;
        var thread = new Thread(() => error = Record.Exception(r.Device.Reset));
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
        Assert.IsType<InvalidOperationException>(error);
    }
}
