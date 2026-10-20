using System.Security.Cryptography;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Hc2000.Z80;
using Xunit;
using Cpu = Hc2000.Z80.Z80;

namespace Hc2000.Machine.Tests;

public sealed class TextOverlayTests
{
    private static (HcMemory Memory, DeviceScheduler Scheduler, FrameClock Clock, Ula Ula) Create(VideoProfile? profile = null)
    {
        var memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var scheduler = new DeviceScheduler();
        var clock = new FrameClock(scheduler, 1);
        return (memory, scheduler, clock, new Ula(memory, new(), scheduler, clock, 2, profile));
    }

    private static uint Pixel(VideoFrame frame, int x, int y) => frame.Pixels.Span[y * frame.Width + x];

    [Fact]
    public void OptInDerivedSurfaceIsAvailableWithoutAnAppWriter()
    {
        Assert.False(new VideoProfile().TextOverlay);
        var off = Create();
        var on = Create(new() { TextOverlay = true });
        off.Scheduler.AdvanceTo(FrameTiming.FrameTicks);
        on.Scheduler.AdvanceTo(FrameTiming.FrameTicks);
        Assert.IsType<VideoFrame>(off.Ula.LastFrame);
        var frame = Assert.IsType<TextOverlayVideoFrame>(on.Ula.LastFrame);
        Assert.Equal((704, 592, frame.MasterTick), (frame.TextOverlay.Width, frame.TextOverlay.Height, frame.TextOverlay.MasterTick));
        Assert.DoesNotContain(frame.TextOverlay.Pixels.ToArray(), p => p != 0);
        Assert.Equal(off.Ula.LastFrame!.Pixels.ToArray(), frame.Pixels.ToArray());
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void EveryRasterBitIsScaledExactlyWithTransparentPaperAndNoGuestMutation(bool fullRaster, bool flash)
    {
        var on = Create(new() { TextOverlay = true, FullRaster = fullRaster,
            FlashPeriodNumerator = flash ? new FrameTiming().FirstPixelTick : FrameTiming.MasterHz, FlashPeriodDenominator = 1 });
        var off = Create(new() { FullRaster = fullRaster,
            FlashPeriodNumerator = flash ? new FrameTiming().FirstPixelTick : FrameTiming.MasterHz, FlashPeriodDenominator = 1 });
        for (var i = 0; i < on.Memory.Ram.Length; i++)
            on.Memory.Ram.Span[i] = off.Memory.Ram.Span[i] = (byte)(i * 37 + i / 256);
        // Keep flashing on for every cell; vary paper, ink and bright over the full display.
        for (var i = 0; i < 768; i++)
            on.Memory.Ram.Span[0x5800 + i] = off.Memory.Ram.Span[0x5800 + i] = (byte)(i | 0x80);
        var before = on.Memory.Ram.ToArray();
        var pages = on.Memory.Pages;
        var latches = on.Memory.Latches;
        on.Scheduler.AdvanceTo(FrameTiming.FrameTicks);
        off.Scheduler.AdvanceTo(FrameTiming.FrameTicks);
        var frame = Assert.IsType<TextOverlayVideoFrame>(on.Ula.LastFrame);
        Assert.Equal(off.Ula.LastFrame!.Pixels.ToArray(), frame.Pixels.ToArray());
        Assert.Equal(before, on.Memory.Ram.ToArray());
        Assert.Same(pages, on.Memory.Pages);
        Assert.Equal(latches, on.Memory.Latches);
        Assert.Equal(off.Scheduler.Now, on.Scheduler.Now);
        Assert.Equal(off.Ula.FlashInverted, on.Ula.FlashInverted);
        var displayX = fullRaster ? (int)(on.Clock.Timing.FirstPixelTick % FrameTiming.LineTicks / FrameTiming.CharacterTicks) * 8 : 48;
        var displayY = fullRaster ? 56 : 40;
        var palette = new VideoProfile().Palette;
        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
            {
                var expected = 0u;
                var py = y - displayY;
                var px = x - displayX;
                if (py >= 0 && py < 192 && px >= 0 && px < 256)
                {
                    var column = px / 8;
                    var bits = before[0x4000 + ((py & 0xc0) << 5) + ((py & 7) << 8) + ((py & 0x38) << 2) + column];
                    var attr = before[0x5800 + (py / 8) * 32 + column];
                    var ink = attr & 7;
                    var sampleTick = on.Clock.Timing.FirstPixelTick + py * FrameTiming.LineTicks + column * FrameTiming.CharacterTicks;
                    if (flash && (sampleTick / on.Clock.Timing.FirstPixelTick) % 2 != 0) ink = (attr >> 3) & 7;
                    if ((bits & (128 >> (px % 8))) != 0) expected = palette[ink + ((attr & 64) >> 3)];
                }
                for (var dy = 0; dy < 2; dy++)
                    for (var dx = 0; dx < 2; dx++)
                        Assert.Equal(expected, Pixel(frame.TextOverlay, x * 2 + dx, y * 2 + dy));
            }
    }

    [Fact]
    public void SamplingUsesExistingVideoDeadlineAndPagingNotEndOfFrameRam()
    {
        var (memory, scheduler, clock, ula) = Create(new() { TextOverlay = true });
        memory.Ram.Span[0x4000] = 0x80;
        memory.Ram.Span[0x5800] = 2;
        memory.Ram.Span[0xc001] = 1;
        memory.Ram.Span[0xd801] = 4;
        scheduler.AdvanceTo(clock.Timing.FirstPixelTick);
        memory.Ram.Span[0x4000] = 0;
        memory.WritePort7E(8);
        scheduler.AdvanceTo(clock.Timing.FirstPixelTick + 20);
        memory.Ram.Span[0xc001] = 0;
        scheduler.AdvanceTo(FrameTiming.FrameTicks);
        var frame = Assert.IsType<TextOverlayVideoFrame>(ula.LastFrame);
        Assert.Equal(0xffcd0000u, Pixel(frame.TextOverlay, 96, 80));
        Assert.Equal(0xff00cd00u, Pixel(frame.TextOverlay, 126, 80));
        Assert.Equal(0u, Pixel(frame.TextOverlay, 112, 80));
        var held = frame.TextOverlay.Pixels.ToArray();
        scheduler.AdvanceTo(FrameTiming.FrameTicks * 2);
        Assert.DoesNotContain(Assert.IsType<TextOverlayVideoFrame>(ula.LastFrame).TextOverlay.Pixels.ToArray(), p => p != 0);
        Assert.Equal(held, frame.TextOverlay.Pixels.ToArray());
    }

    [MemoryRomAssetsTheory]
    [InlineData(false)] [InlineData(true)]
    public void CanonicalEpromWriterAndUlaAgreeOnAll12288CpmGlyphLines(bool chunked)
    {
        var root = StorageAssetTests.Root();
        var eprom = File.ReadAllBytes(Path.Combine(root, "roms/hc2000/eprom-dump-hc2k-2/M27C512.bin"));
        Assert.Equal("83e3213b049c5b849ee32e04da901eba06992d5aa1f44f76564f312c79748d2b", Convert.ToHexStringLower(SHA256.HashData(eprom)));
        var rom = File.ReadAllBytes(Path.Combine(root, "roms/hc2000/v2/cpm-hc2kzip.bin"));
        Assert.Equal(eprom.AsSpan(0xe000).ToArray(), rom);
        var bus = new SuffixBus();
        eprom.CopyTo(bus.Bytes, 0);
        var cpu = new Cpu(bus);
        var (memory, scheduler, _, ula) = Create(new() { TextOverlay = true });
        memory.WritePort7E(8);
        var addresses = new HashSet<int>();
        var glyphs = new byte[24, 64, 8];
        var count = 0;
        for (var row = 0; row < 24; row++)
            for (var col = 0; col < 64; col++)
            {
                var ch = 32 + (row * 64 + col) % 96;
                bus.Bytes[0xdb04] = 0xab; bus.Bytes[0xdb05] = 0xe5;
                bus.Bytes[0xdb0f] = (byte)col; bus.Bytes[0xdb10] = (byte)row;
                cpu.Reset(); cpu.R.PC = 0xe455; cpu.R.SP = 0xbffe;
                cpu.R.A = (byte)ch;
                bus.Bytes[0xbffe] = 0; bus.Bytes[0xbfff] = 0;
                // Execute the image's complete fetch/address routine, including SUB/LD DE and RET.
                for (var steps = 0; cpu.R.PC != 0 && steps < 32; steps++) cpu.Step();
                Assert.Equal((ushort)0, cpu.R.PC);
                Assert.Equal((ushort)(0xe5ab + 8 * (ch - 32)), cpu.R.DE);
                for (var li = 0; li < 8; li++)
                {
                    var address = cpu.R.HL + li * 256;
                    var y = row * 8 + li;
                    var ulaAddress = 0xc000 + ((y & 0xc0) << 5) + ((y & 7) << 8) + ((y & 0x38) << 2) + col / 2;
                    Assert.Equal(ulaAddress, address);
                    Assert.InRange(address, 0xc000, 0xd7ff);
                    addresses.Add(address); count++;
                    var nibble = glyphs[row, col, li] = (byte)(bus.Bytes[cpu.R.DE + li] & 15);
                    ref var target = ref memory.Ram.Span[address];
                    target = (byte)(col % 2 == 0 ? (target & 15) | (nibble << 4) : (target & 240) | nibble);
                }
                // Execute the separate E59B attribute writer too: one store, independent of li.
                var attrAddress = ((0xd8 + (row >> 3)) << 8) + (row & 7) * 32 + col / 2;
                bus.Bytes[0xdb0c] = (byte)(1 + (row + col / 2) % 7);
                cpu.R.PC = 0xe59b; cpu.R.SP = 0xbffe;
                bus.LastAttributeAddress = -1;
                for (var steps = 0; cpu.R.PC != 0 && steps < 20; steps++) cpu.Step();
                Assert.Equal((ushort)0, cpu.R.PC);
                Assert.Equal(attrAddress, bus.LastAttributeAddress);
                Assert.Equal(bus.Bytes[0xdb0c], bus.Bytes[attrAddress]);
                memory.Ram.Span[attrAddress] = bus.Bytes[attrAddress];
            }
        Assert.Equal(12288, count);
        Assert.Equal(6144, addresses.Count);
        var before = memory.Ram.ToArray();
        if (chunked)
            for (var tick = 1000L; tick < FrameTiming.FrameTicks; tick += 1000) scheduler.AdvanceTo(tick);
        scheduler.AdvanceTo(FrameTiming.FrameTicks);
        Assert.Equal(before, memory.Ram.ToArray());
        var frame = Assert.IsType<TextOverlayVideoFrame>(ula.LastFrame);
        var palette = new VideoProfile().Palette;
        for (var row = 0; row < 24; row++)
            for (var col = 0; col < 64; col++)
                for (var li = 0; li < 8; li++)
                    for (var bit = 0; bit < 4; bit++)
                    {
                        var ink = (glyphs[row, col, li] & (8 >> bit)) != 0;
                        var expected = palette[1 + (row + col / 2) % 7];
                        var x = 48 + col * 4 + bit;
                        var y = 40 + row * 8 + li;
                        Assert.Equal(ink ? expected : palette[0], Pixel(frame, x, y));
                        for (var dy = 0; dy < 2; dy++)
                            for (var dx = 0; dx < 2; dx++)
                                Assert.Equal(ink ? expected : 0u, Pixel(frame.TextOverlay, x * 2 + dx, y * 2 + dy));
                    }
    }

    [MemoryRomAssetsTheory]
    [InlineData("ABC 123")]
    public void BasicFontRasterSurvivesGuestXorScrollAndEraseWithoutHostTextState(string text)
    {
        var rom = File.ReadAllBytes(Path.Combine(StorageAssetTests.Root(), "roms/hc2000/v2/basic.rom"));
        var (memory, scheduler, _, ula) = Create(new() { TextOverlay = true });
        // Fixture writes only: the CHROUT-internal instruction path remains outside this proof.
        // ROM glyphs at 3D00, beginning at space; no host font or inferred characters.
        for (var i = 0; i < text.Length; i++)
            for (var line = 0; line < 8; line++)
            {
                memory.Ram.Span[0x4000 + line * 256 + i] = rom[0x3d00 + (text[i] - 32) * 8 + line];
                memory.Ram.Span[0x5800 + i] = 7;
            }
        scheduler.AdvanceTo(FrameTiming.FrameTicks);
        var original = Assert.IsType<TextOverlayVideoFrame>(ula.LastFrame).TextOverlay;
        for (var i = 0; i < text.Length; i++)
            for (var line = 0; line < 8; line++)
                for (var bit = 0; bit < 8; bit++)
                    Assert.Equal((rom[0x3d00 + (text[i] - 32) * 8 + line] & (128 >> bit)) != 0 ? 0xffcdcdcdu : 0u,
                        Pixel(original, (48 + i * 8 + bit) * 2, (40 + line) * 2));
        // XOR cursor then guest-style row shift: the new pixels, not cached host text, win.
        for (var line = 0; line < 8; line++)
        {
            memory.Ram.Span[0x4000 + line * 256] ^= 0xff;
            memory.Ram.Span.Slice(0x4000 + line * 256, 32).CopyTo(memory.Ram.Span.Slice(0x4020 + line * 256, 32));
            memory.Ram.Span.Slice(0x4000 + line * 256, 32).Clear();
        }
        memory.Ram.Span.Slice(0x5800, 32).CopyTo(memory.Ram.Span.Slice(0x5820, 32));
        var before = memory.Ram.ToArray();
        scheduler.AdvanceTo(FrameTiming.FrameTicks * 2);
        Assert.Equal(before, memory.Ram.ToArray());
        var shifted = Assert.IsType<TextOverlayVideoFrame>(ula.LastFrame).TextOverlay;
        for (var line = 0; line < 8; line++)
            for (var bit = 0; bit < 8; bit++)
            {
                Assert.Equal(0u, Pixel(shifted, (48 + bit) * 2, (40 + line) * 2));
                Assert.Equal(Pixel(original, (48 + bit) * 2, (40 + line) * 2) == 0 ? 0xffcdcdcdu : 0u,
                    Pixel(shifted, (48 + bit) * 2, (48 + line) * 2));
            }
        memory.Ram.Span.Slice(0x4000, 0x1800).Clear();
        scheduler.AdvanceTo(FrameTiming.FrameTicks * 3);
        Assert.DoesNotContain(Assert.IsType<TextOverlayVideoFrame>(ula.LastFrame).TextOverlay.Pixels.ToArray(), p => p != 0);
    }

    private sealed class SuffixBus : IBus
    {
        public byte[] Bytes { get; } = new byte[65536];
        public int LastAttributeAddress { get; set; } = -1;
        public byte Read(ushort address) => Bytes[address];
        public void Write(ushort address, byte value)
        {
            Bytes[address] = value;
            if (address is >= 0xd800 and <= 0xdaff) LastAttributeAddress = address;
        }
        public byte In(ushort port) => 0;
        public void Out(ushort port, byte value) { }
        public byte IntVector() => 0xff;
        public void Contend(ushort address, int tstates) { }
    }
}
