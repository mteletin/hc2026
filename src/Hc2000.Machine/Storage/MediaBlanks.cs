namespace Hc2000.Machine.Storage;

/// <summary>Shared blank-media table (KB hc2000-media-home-scan-admission-v1 sec 4): one table, three named
/// targets, one writer per family. The hc CLI and the App call these same methods; the writers return
/// bytes only, so "byte-identical" means one writer, not a second implementation. Home placement and
/// refusal are host-side (Hc2000.HostFiles.MediaHomeCreate).</summary>
public static class MediaBlanks
{
    public sealed record Target(string Name, Func<byte[]> Write);
    public static IReadOnlyList<Target> Targets() => new Target[]
    {
        new("floppy-720", Floppy720),
        new("hdd-volume-8m", HddVolume8M),
        new("tape-blank", TapeBlank)
    };
    /// <summary>The existing cpm-hc80 preset as an extended container: the same bytes hc fs new writes
    /// today. Dpb.Presets() stays four floppy DPBs; this never touches Dpb.HddVolume().</summary>
    public static byte[] Floppy720()
        => CpmFs.Format(Dpb.Presets().Single(p => p.Name == "cpm-hc80"), DiskFormat.Extended).ImageBytes();
    /// <summary>The flat 8,388,608-byte 0xe5 cold volume Dpb.HddVolume() opens; not the zero fill of
    /// hc hdd create, which stays a zero fill.</summary>
    public static byte[] HddVolume8M() => CpmFs.FormatHddVolume();
    /// <summary>A zero-byte tape: Tap.Save() of no blocks; never a spec-13 guest format.</summary>
    public static byte[] TapeBlank() => Tap.Load(Array.Empty<byte>()).Save();
}
