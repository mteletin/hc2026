namespace Hc2000.App;

/// <summary>Per-user directories the installers and the app must agree on.
/// ROMs live at <c>&lt;config&gt;/roms/&lt;rel&gt;</c>, disks at <c>&lt;config&gt;/disks/&lt;name&gt;</c>
/// (spec v4.2). Linux config is <c>$XDG_CONFIG_HOME/hc2000</c> when that variable is set,
/// and always <c>~/.config/hc2000</c>: <c>install-ubuntu.sh</c> hardcodes the latter even
/// when <c>XDG_CONFIG_HOME</c> points elsewhere. Windows config is <c>%APPDATA%\hc2000</c>.</summary>
internal static class InstalledLayout
{
    public static IReadOnlyList<string> ConfigDirectories()
    {
        var dirs = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            AddConfig(dirs, Environment.GetEnvironmentVariable("APPDATA"));
            AddConfig(dirs, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrWhiteSpace(xdg))
                AddConfig(dirs, xdg);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
                home = Environment.GetEnvironmentVariable("HOME") ?? "";
            if (!string.IsNullOrEmpty(home))
                AddConfig(dirs, Path.Combine(home, ".config"));
            // Spec also names the Windows data dir. Honor it when the variable is set;
            // do not invent ~/.config on Windows (that would outrank %APPDATA%).
            AddConfig(dirs, Environment.GetEnvironmentVariable("APPDATA"));
        }
        return dirs;
    }

    private static void AddConfig(List<string> dirs, string? parent) =>
        Add(dirs, string.IsNullOrWhiteSpace(parent) ? null : Path.Combine(parent, "hc2000"));

    private static void Add(List<string> dirs, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (ArgumentException) { return; }
        catch (NotSupportedException) { return; }
        if (dirs.Exists(existing => string.Equals(existing, full, PathComparison))) return;
        dirs.Add(full);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
