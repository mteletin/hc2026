using Hc2000.Machine.Input;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class UlaTests
{
    private static (HcMemory Memory, DeviceScheduler Scheduler, FrameClock Clock, Ula Ula) Machine(VideoProfile? profile = null)
    {
        var memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var scheduler = new DeviceScheduler();
        var clock = new FrameClock(scheduler, 1);
        return (memory, scheduler, clock, new Ula(memory, new(), scheduler, clock, 2, profile));
    }

    private static int Bitmap(int y, int column = 0) => ((y & 0xc0) << 5) | ((y & 7) << 8) | ((y & 0x38) << 2) | column;
    private static uint Pixel(VideoFrame frame, int x, int y) => frame.Pixels.Span[y * frame.Width + x];

    [Fact]
    public void SpectrumBitmapAddressingPaletteBrightAndCroppedDimensions()
    {
        var (memory, scheduler, _, ula) = Machine();
        var palette = new VideoProfile().Palette;
        for (var y = 0; y < 192; y++)
        {
            var column = y % 32;
            memory.Ram.Span[0x4000 + Bitmap(y, column)] = 0x81;
            memory.Ram.Span[0x5800 + (y >> 3) * 32 + column] = 0x40 | 2 | (4 << 3);
        }
        scheduler.AdvanceTo(FrameTiming.FrameTicks);
        var frame = Assert.IsType<VideoFrame>(ula.LastFrame);
        Assert.Equal((352, 296, FrameTiming.FrameTicks), (frame.Width, frame.Height, frame.MasterTick));
        for (var y = 0; y < 192; y++)
        {
            var x = 48 + y % 32 * 8;
            Assert.Equal(palette[10], Pixel(frame, x, y + 40));
            Assert.Equal(palette[12], Pixel(frame, x + 1, y + 40));
            Assert.Equal(palette[10], Pixel(frame, x + 7, y + 40));
        }
    }

    [Fact]
    public void MidLineRamAttributeAndPagingChangesAreSampledAtCharacterBoundaries()
    {
        var (memory, scheduler, clock, ula) = Machine();
        var ram = memory.Ram.Span;
        ram[0x4000] = 0xff;
        ram[0x5800] = 1;
        ram[0xc001] = 0xff;
        ram[0xd801] = 2;
        memory.SetCpm(true); // C000 CPU alias must not change physical video fetches.
        scheduler.AdvanceTo(clock.Timing.FirstPixelTick); // old 4000 first cell sampled
        memory.WritePort7E(8);
        scheduler.AdvanceTo(clock.Timing.FirstPixelTick + 20); // new C000 second cell sampled
        memory.Ram.Span[0xd802] = 4; // attribute before third cell
        memory.Ram.Span[0xc002] = 0xff;
        scheduler.AdvanceTo(FrameTiming.FrameTicks);
        var frame = ula.LastFrame!;
        var palette = new VideoProfile().Palette;
        Assert.Equal(palette[1], Pixel(frame, 48, 40));
        Assert.Equal(palette[2], Pixel(frame, 56, 40));
        Assert.Equal(palette[4], Pixel(frame, 64, 40));
        Assert.Equal(palette[0], Pixel(frame, 72, 40));
    }

    [Fact]
    public void CpuWriteAtVideoDeadlineAffectsNextSampleNotTheAlreadyDrainedOne()
    {
        var (memory, scheduler, clock, ula) = Machine();
        memory.Ram.Span[0x5800] = 2;
        memory.Ram.Span[0x5801] = 2;
        scheduler.AdvanceTo(clock.Timing.FirstPixelTick);
        memory.Ram.Span[0x4000] = 0xff;
        memory.Ram.Span[0x4001] = 0xff;
        scheduler.AdvanceTo(FrameTiming.FrameTicks);
        var frame = ula.LastFrame!;
        Assert.Equal(0xff000000u, Pixel(frame, 48, 40));
        Assert.Equal(0xffcd0000u, Pixel(frame, 56, 40));
    }

    [Fact]
    public void BorderWritesBlankingFullRasterAndPublishedBufferOwnership()
    {
        var (_, scheduler, _, ula) = Machine(new() { FullRaster = true });
        ula.Write(0xfe, 1, 0);
        var boundary = 24 * FrameTiming.LineTicks + 20;
        scheduler.AdvanceTo(boundary);
        ula.Write(0xfe, 2, boundary);
        scheduler.AdvanceTo(FrameTiming.FrameTicks);
        var first = ula.LastFrame!;
        Assert.Equal((456, 312), (first.Width, first.Height));
        Assert.Equal(0xff000000u, Pixel(first, 8, 23));
        Assert.Equal(0xff0000cdu, Pixel(first, 8, 24));
        Assert.Equal(0xffcd0000u, Pixel(first, 16, 24));
        scheduler.AdvanceTo(FrameTiming.FrameTicks * 2);
        Assert.Equal(0xff0000cdu, Pixel(first, 8, 24)); // later raster did not overwrite prior published frame
        Assert.Equal(0xffcd0000u, Pixel(ula.LastFrame!, 8, 24));
    }

    [Fact]
    [Trait("hardware", "unverified")]
    public void ProvisionalFlashIsFreeRunningAndSwapsOnlyFlashCells()
    {
        var (memory, scheduler, _, ula) = Machine(new() { FlashPeriodNumerator = FrameTiming.FrameTicks + 10, FlashPeriodDenominator = 1 });
        // Deliberately non-frame-locked profile period, to isolate continuation and swap timing.
        memory.Ram.Span[0x4000] = memory.Ram.Span[0x4001] = 0xff;
        memory.Ram.Span[0x5800] = 0x80 | 2 | (4 << 3);
        memory.Ram.Span[0x5801] = 2 | (4 << 3);
        scheduler.AdvanceTo(FrameTiming.FrameTicks);
        Assert.False(ula.FlashInverted);
        Assert.Equal(0xffcd0000u, Pixel(ula.LastFrame!, 48, 40));
        scheduler.AdvanceTo(FrameTiming.FrameTicks + 10);
        Assert.True(ula.FlashInverted);
        scheduler.AdvanceTo(FrameTiming.FrameTicks * 2);
        Assert.Equal(0xff00cd00u, Pixel(ula.LastFrame!, 48, 40));
        Assert.Equal(0xffcd0000u, Pixel(ula.LastFrame!, 56, 40)); // nonflash cell unchanged
        scheduler.AdvanceTo(FrameTiming.FrameTicks * 3);
        Assert.False(ula.FlashInverted);
        Assert.Equal(0xffcd0000u, Pixel(ula.LastFrame!, 48, 40));
        // Default period remains the ADR's independent0.5s, not frame/16.
        var defaults = Machine();
        defaults.Scheduler.AdvanceTo(FrameTiming.MasterHz / 2);
        Assert.False(defaults.Ula.FlashInverted);
        defaults.Scheduler.AdvanceTo((FrameTiming.MasterHz + 1) / 2);
        Assert.True(defaults.Ula.FlashInverted);
        Assert.NotEqual(0, defaults.Scheduler.Now % FrameTiming.FrameTicks);
    }

    [Fact]
    public void BeeperHasBothFeBitsExactMasterTimestampsAndNoDuplicateEdges()
    {
        var (_, scheduler, _, ula) = Machine();
        var edges = new List<BeeperEdge>();
        ula.BeeperChanged += edges.Add;
        Assert.Equal(0, ula.Beeper.MixLevel);
        ula.Write(0xfe, 8, 0);
        scheduler.AdvanceTo(100);
        ula.Write(0xfe, 0x10, 100);
        ula.Write(0xfe, 0x17, 100); // border only
        scheduler.AdvanceTo(200);
        ula.Write(0xfe, 0x18, 200);
        scheduler.AdvanceTo(300);
        ula.Write(0xfe, 0, 300);
        Assert.Equal(new[] { 0L, 100L, 200L, 300L }, edges.Select(e => e.MasterTick));
        Assert.Equal(new[] { 1, 2, 3, 0 }, edges.Select(e => e.MixLevel));
        scheduler.AdvanceTo(400);
        Assert.Equal(300, ula.Beeper.MasterTick); // level's last transition, not query time
    }

    [Fact]
    public void IdenticalInputAndDifferentAdvanceChunksYieldIdenticalFrames()
    {
        var a = Machine();
        var b = Machine();
        for (var i = 0; i < 0x1b00; i++)
            a.Memory.Ram.Span[0x4000 + i] = b.Memory.Ram.Span[0x4000 + i] = (byte)(i * 17);
        a.Scheduler.AdvanceTo(123456);
        b.Scheduler.AdvanceTo(100000);
        b.Scheduler.AdvanceTo(123456);
        a.Ula.Write(0xfe, 17, 123456);
        b.Ula.Write(0xfe, 17, 123456);
        a.Scheduler.AdvanceTo(FrameTiming.FrameTicks);
        b.Scheduler.AdvanceTo(FrameTiming.FrameTicks);
        Assert.Equal(a.Ula.LastFrame!.Pixels.ToArray(), b.Ula.LastFrame!.Pixels.ToArray());
        Assert.Equal(a.Ula.Beeper, b.Ula.Beeper);
    }

    [Fact]
    public void FlashSignalEdgePrecedesCoincidentVideoSample()
    {
        var firstPixel = new FrameTiming().FirstPixelTick;
        var (memory, scheduler, _, ula) = Machine(new() { FlashPeriodNumerator = firstPixel, FlashPeriodDenominator = 1 });
        memory.Ram.Span[0x4000] = 0xff;
        memory.Ram.Span[0x5800] = 0x80 | 2 | (4 << 3);
        scheduler.AdvanceTo(FrameTiming.FrameTicks);
        Assert.Equal(0xff00cd00u, Pixel(ula.LastFrame!, 48, 40));
    }

    [Fact]
    public void DeviceOutputsDoNotDependOnCpuDividerOrShareStateBetweenMachines()
    {
        var a = Machine();
        var b = Machine();
        var faithful = new CpuDeviceTimeAdapter(a.Scheduler, new CpuTimeMapper(5, 1));
        var faster = new CpuDeviceTimeAdapter(b.Scheduler, new CpuTimeMapper(5, 2));
        faithful.SyncCpuTime(100);
        faster.SyncCpuTime(200);
        a.Ula.Write(0xfe, 24, 500);
        b.Ula.Write(0xfe, 24, 500);
        Assert.Equal(a.Ula.Beeper, b.Ula.Beeper);
        a.Ula.Keyboard.SetKey(HcKey.Up, true);
        Assert.Equal(23, a.Ula.Keyboard.Read(0xeffe));
        Assert.Equal(31, b.Ula.Keyboard.Read(0xeffe));
        faithful.SyncCpuTime(200);
        Assert.Equal(500, b.Scheduler.Now);
        Assert.Equal(1000, a.Scheduler.Now);
    }

    [Fact]
    public void InvalidPaletteAndSubTickFlashPeriodAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Machine(new() { Palette = new uint[1] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Machine(new() { FlashPeriodNumerator = 1, FlashPeriodDenominator = 2 }));
    }

    [MemoryRomAssetsTheory]
    [InlineData('A', 'B')]
    public void CanonicalCpmRomLowNibbleGlyphsProduce64ColumnsSharingAttributes(char left, char right)
    {
        var path = Path.Combine(StorageAssetTests.Root(), "roms", "hc2000", "v2", "cpm-hc2kzip.bin");
        var rom = File.ReadAllBytes(path);
        var (memory, scheduler, _, ula) = Machine();
        var palette = new VideoProfile().Palette;
        for (var y = 0; y < 192; y++)
            for (var column = 0; column < 32; column++)
            {
                var a = rom[0x5ab + (left - 32) * 8 + y % 8] & 15;
                var b = rom[0x5ab + (right - 32) * 8 + y % 8] & 15;
                memory.Ram.Span[0x4000 + Bitmap(y, column)] = (byte)((a << 4) | b);
                // Two4x8 characters share each8x8 attribute. Vary it across every row and cell.
                memory.Ram.Span[0x5800 + (y >> 3) * 32 + column] = (byte)(1 + ((y >> 3) + column) % 7);
            }
        scheduler.AdvanceTo(FrameTiming.FrameTicks);
        for (var y = 0; y < 192; y++)
            for (var x = 0; x < 256; x++)
            {
                var nibble = rom[0x5ab + ((x % 8 < 4 ? left : right) - 32) * 8 + y % 8] & 15;
                var colour = 1 + ((y >> 3) + x / 8) % 7;
                var expected = (nibble & (8 >> (x % 4))) != 0 ? palette[colour] : palette[0];
                Assert.Equal(expected, Pixel(ula.LastFrame!, x + 48, y + 40));
            }
    }
}
