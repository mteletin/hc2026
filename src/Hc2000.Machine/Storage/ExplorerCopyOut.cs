namespace Hc2000.Machine.Storage;

/// <summary>
/// One CP/M entry extracted out of an open filesystem: the on-disk identity (name and user area)
/// and the stored records, byte-faithful to the disk (terminal 0x00 and 0x1A kept, no trimming,
/// no guess of the original host length).
/// </summary>
public sealed record CpmExportEntry(string CpmName, int User, int Records, byte[] Data)
{
    public int Bytes => Data.Length;
}

/// <summary>
/// Explorer export-lane core (hc2000-explorer-copy-out): resolves one named entry out of an
/// open filesystem to its stored records. Read-only: the filesystem is never written. The
/// host naming, collision and file-writing layer lives in Hc2000.App.Explorer. No UI, no
/// mount code, no dependency on the listing lane's model; library code keeps its no-host-I/O
/// contract.
/// </summary>
public static class ExplorerCopyOut
{
    private static readonly string[] ReservedDeviceNames =
    {
        "NUL", "CON", "PRN", "AUX",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>Legal host file name for a CP/M name. Presentation only: the disk identity is never altered.</summary>
    public static string HostName(string cpmName)
    {
        if (cpmName is null) throw new ArgumentNullException(nameof(cpmName));
        var safe = new string(cpmName.TrimEnd(' ').Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray());
        safe = safe.TrimEnd(' ', '.');
        if (safe.Length == 0 || safe is "." or "..") return "hc-file";
        var dot = safe.IndexOf('.');
        var stem = dot < 0 ? safe : safe[..dot];
        if (ReservedDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) safe = "hc-" + safe;
        return safe;
    }

    /// <summary>
    /// Reads one named entry (user area plus 8.3 name) out of the filesystem and returns its
    /// stored records. The filesystem is opened read-only by construction: this never mutates
    /// it. Absent entries throw FileNotFoundException exactly like CpmFs.Get.
    /// </summary>
    public static CpmExportEntry Extract(CpmFs fs, string cpmName, int user)
    {
        if (fs is null) throw new ArgumentNullException(nameof(fs));
        if (cpmName is null) throw new ArgumentNullException(nameof(cpmName));
        if (user is < 0 or > 31) throw new ArgumentOutOfRangeException(nameof(user));
        var data = fs.Get(cpmName, user); // record-rounded, byte-faithful; Get never writes
        var lookup = fs.Parameters.HcHeader ? cpmName.PadRight(11) : cpmName;
        var comparer = fs.Parameters.HcHeader ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var file = fs.List().First(f => f.User == user && string.Equals(f.Name, lookup, comparer));
        return new CpmExportEntry(file.Name, file.User, file.Records, data);
    }
}
