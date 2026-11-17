namespace Hc2000.Machine.Video;

/// <summary>
/// Live presentation mode for machine video frames.
/// Faithful is the default, rendering unchanged machine pixels.
/// CrispText and SplitComparison apply an opt-in known-font heuristic
/// (ZX Spectrum 32-column BASIC and HC2000 64-column CP/M).
/// Unmatched cells and graphics fall back safely to faithful rendering.
/// </summary>
public enum PresentationMode
{
    /// <summary>Faithful guest raster without modification (default).</summary>
    Faithful = 0,

    /// <summary>Opt-in crisp text rendering for recognized known-font text cells.</summary>
    CrispText = 1,

    /// <summary>50/50 comparison split: left half faithful, right half crisp text, with center divider.</summary>
    SplitComparison = 2
}
