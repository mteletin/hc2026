using Hc2000.Machine.Input;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Video;

/// <summary>Logical 48K speaker/MIC mix codes, not calibrated host audio amplitudes.</summary>
public readonly record struct BeeperEdge(long MasterTick, bool Speaker, bool Microphone)
{
    public int MixLevel => (Speaker ? 2 : 0) + (Microphone ? 1 : 0);
}

public record VideoFrame(long MasterTick, int Width, int Height, ReadOnlyMemory<uint> Pixels);

/// <summary>An unchanged faithful frame plus a separate, transparent-paper 2x ink surface.
/// Raster content is preserved verbatim, not recognized or replaced with host fonts.</summary>
public sealed record TextOverlayVideoFrame(long MasterTick, int Width, int Height,
    ReadOnlyMemory<uint> Pixels, VideoFrame TextOverlay) : VideoFrame(MasterTick, Width, Height, Pixels);

/// <summary>Character-granular ULA on the master clock; no CPU, wall-clock or host I/O.</summary>
public sealed class Ula : IPortDevice
{
    private const int RasterWidth = 456, RasterHeight = 312;
    private readonly DeviceScheduler scheduler;
    private readonly HcMemory memory;
    private readonly FrameClock frameClock;
    private readonly int flashDeviceId;
    private RationalClock flashClock;
    private readonly uint[] palette;
    private readonly bool fullRaster;
    // Opt-in mask follows the faithful raster in the same store: no new default-state allocation.
    private readonly uint[] raster;
    public KeyboardMatrix Keyboard { get; }
    public byte FeLatch { get; private set; }
    private bool tapeIn;
    /// <summary>EAR comparator output; notifications observe the existing scheduler timestamp.</summary>
    public bool TapeIn
    {
        get => tapeIn;
        set
        {
            if (tapeIn == value) return;
            tapeIn = value;
            TapeInputChanged?.Invoke(scheduler.Now, value);
        }
    }
    public event Action<long, bool>? TapeInputChanged;
    public bool FlashInverted { get; private set; }
    /// <summary>Current pins and their last transition timestamp; initial pins are low at epoch zero.</summary>
    public BeeperEdge Beeper { get; private set; }
    public VideoFrame? LastFrame { get; private set; }
    public event Action<BeeperEdge>? BeeperChanged;
    public event Action<VideoFrame>? FrameReady;

    public Ula(HcMemory memory, KeyboardMatrix keyboard, DeviceScheduler scheduler,
        FrameClock frameClock, int stableFlashDeviceId, VideoProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(keyboard);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(frameClock);
        if (scheduler.Now != 0) throw new ArgumentException("Construct ULA at the machine epoch.", nameof(scheduler));
        profile ??= new();
        profile.Validate();
        this.memory = memory;
        Keyboard = keyboard;
        this.scheduler = scheduler;
        this.frameClock = frameClock;
        flashDeviceId = stableFlashDeviceId;
        fullRaster = profile.FullRaster;
        palette = profile.Palette.ToArray();
        raster = new uint[RasterWidth * RasterHeight * (profile.TextOverlay ? 2 : 1)];
        Array.Fill(raster, palette[0], 0, RasterWidth * RasterHeight);
        flashClock = new(0, profile.FlashPeriodNumerator, profile.FlashPeriodDenominator);
        scheduler.RegisterDevice(flashDeviceId, Flash);
        ScheduleFlash();
        frameClock.VideoSample += Sample;
        frameClock.FrameCompleted += Publish;
    }

    public byte Read(ushort port, long masterTick)
    {
        RequireTime(masterTick);
        return (byte)(0xa0 | Keyboard.Read(port) | (TapeIn ? 0x40 : 0));
    }

    public void Write(ushort port, byte value, long masterTick)
    {
        RequireTime(masterTick);
        var changed = (FeLatch ^ value) & 0x18;
        FeLatch = (byte)(value & 0x1f);
        if (changed != 0)
        {
            Beeper = new(masterTick, (FeLatch & 16) != 0, (FeLatch & 8) != 0);
            BeeperChanged?.Invoke(Beeper);
        }
    }

    private void RequireTime(long tick)
    {
        if (tick != scheduler.Now) throw new ArgumentException("Drain scheduler to the bus timestamp before a ULA effect.", nameof(tick));
    }

    private void ScheduleFlash() => scheduler.Schedule(new(flashClock.Advance(), EventPhase.SignalEdge,
        flashDeviceId, 0, scheduler.Generation(flashDeviceId)));

    private void Flash(DeviceEvent item)
    {
        ScheduleFlash();
        FlashInverted = !FlashInverted;
    }

    /// <summary>Restores the logical ULA fields of a captured snapshot (snapshot restore).
    /// nextFlashDue is the captured pending flash deadline; the origin-zero flash period makes
    /// it an exact invertible clock phase, so no event bookkeeping is needed here — the
    /// scheduler restore supplies the pending event itself.</summary>
    internal void RestoreSnapshot(byte fe, bool flashInverted, BeeperEdge beeper, bool tapeIn, long nextFlashDue)
    {
        if (nextFlashDue <= scheduler.Now) throw new ArgumentException("Captured flash deadline must be after Now.", nameof(nextFlashDue));
        var num = flashClock.Numerator;
        var den = flashClock.Denominator;
        var periods = (long)(((Int128)(nextFlashDue - 1) * den) / num) + 1;
        var phase = (Int128)periods * num;
        FeLatch = (byte)(fe & 0x1f);
        FlashInverted = flashInverted;
        Beeper = beeper;
        TapeIn = tapeIn;
        flashClock = new RationalClock((long)(phase / den), num, den, (long)(phase % den));
    }

    private void Sample(long tick, VideoPosition position)
    {
        var offset = position.Line * RasterWidth + position.Character * 8;
        if (position.Blanked)
        {
            Array.Fill(raster, palette[0], offset, 8);
            return;
        }
        if (!position.Display)
        {
            Array.Fill(raster, palette[FeLatch & 7], offset, 8);
            return;
        }
        var y = position.Line - 56;
        var startCharacter = (int)(frameClock.Timing.FirstPixelTick % FrameTiming.LineTicks / FrameTiming.CharacterTicks);
        var column = position.Character - startCharacter;
        var bitmapOffset = ((y & 0xc0) << 5) | ((y & 7) << 8) | ((y & 0x38) << 2) | column;
        var ram = memory.Ram.Span;
        var bits = ram[memory.VideoBase + bitmapOffset];
        var attr = ram[memory.VideoBase + 0x1800 + (y >> 3) * 32 + column];
        var bright = (attr & 0x40) >> 3;
        var ink = palette[(attr & 7) + bright];
        var paper = palette[((attr >> 3) & 7) + bright];
        if ((attr & 0x80) != 0 && FlashInverted) (ink, paper) = (paper, ink);
        for (var x = 0; x < 8; x++) raster[offset + x] = (bits & (0x80 >> x)) != 0 ? ink : paper;
        // Reuse the very same sampled byte/attribute, including raster-time paging and flash.
        // A zero bit is transparent, including a space; nontext stays an exact pixel fallback.
        if (raster.Length > RasterWidth * RasterHeight)
            for (var x = 0; x < 8; x++)
                raster[RasterWidth * RasterHeight + offset + x] = (bits & (0x80 >> x)) != 0 ? ink : 0;
    }

    private void Publish(long tick)
    {
        var width = fullRaster ? RasterWidth : 352;
        var height = fullRaster ? RasterHeight : 296;
        var pixels = new uint[width * height];
        // A line begins in horizontal blanking in FrameClock. Rotate the cropped view so the
        // display has a 48-pixel left border. Retain lines16..311 (eight blank,32 top,192 display,64 bottom).
        var displayX = (int)(frameClock.Timing.FirstPixelTick % FrameTiming.LineTicks / FrameTiming.CharacterTicks) * 8;
        var left = (displayX - 48 + RasterWidth) % RasterWidth;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                pixels[y * width + x] = raster[(y + (fullRaster ? 0 : 16)) * RasterWidth +
                    (fullRaster ? x : (left + x) % RasterWidth)];
        var completed = raster.Length == RasterWidth * RasterHeight
            ? new VideoFrame(tick, width, height, pixels)
            : new TextOverlayVideoFrame(tick, width, height, pixels, PublishOverlay(tick, width, height, left));
        Array.Fill(raster, palette[0], 0, RasterWidth * RasterHeight);
        if (raster.Length > RasterWidth * RasterHeight)
            Array.Clear(raster, RasterWidth * RasterHeight, RasterWidth * RasterHeight);
        LastFrame = completed;
        FrameReady?.Invoke(completed);
    }

    private VideoFrame PublishOverlay(long tick, int width, int height, int left)
    {
        var scaledWidth = width * 2;
        var pixels = new uint[scaledWidth * height * 2];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var ink = raster[RasterWidth * RasterHeight + (y + (fullRaster ? 0 : 16)) * RasterWidth +
                    (fullRaster ? x : (left + x) % RasterWidth)];
                var target = y * 2 * scaledWidth + x * 2;
                pixels[target] = pixels[target + 1] = pixels[target + scaledWidth] = pixels[target + scaledWidth + 1] = ink;
            }
        return new(tick, scaledWidth, height * 2, pixels);
    }
}
