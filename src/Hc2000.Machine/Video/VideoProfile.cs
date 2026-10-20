using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Video;

/// <summary>Raster/palette policy, not measured analogue colour or edge precision.</summary>
public sealed record VideoProfile
{
    // Independent RC default selected by the device-time ADR; hardware flash rate remains UNVERIFIED.
    public long FlashPeriodNumerator { get; init; } = FrameTiming.MasterHz;
    public long FlashPeriodDenominator { get; init; } = 2;
    public bool FullRaster { get; init; }
    /// <summary>Opt-in host-only 2x ink surface; never changes guest state or the faithful frame.</summary>
    public bool TextOverlay { get; init; }
    public IReadOnlyList<uint> Palette { get; init; } = new uint[] {
        0xff000000, 0xff0000cd, 0xffcd0000, 0xffcd00cd,
        0xff00cd00, 0xff00cdcd, 0xffcdcd00, 0xffcdcdcd,
        0xff000000, 0xff0000ff, 0xffff0000, 0xffff00ff,
        0xff00ff00, 0xff00ffff, 0xffffff00, 0xffffffff
    };

    internal void Validate()
    {
        if (FlashPeriodNumerator <= 0 || FlashPeriodDenominator <= 0 ||
            FlashPeriodNumerator < FlashPeriodDenominator)
            throw new ArgumentOutOfRangeException(nameof(FlashPeriodNumerator));
        if (Palette is null || Palette.Count != 16) throw new ArgumentException("Palette requires sixteen ARGB colours.");
    }
}
