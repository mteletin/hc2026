namespace Hc2000.App.Diagnostics;

/// <summary>
/// Append-only record of machine-Start outcomes, written to start.log in the App's settings
/// directory, next to settings.json (hc2000/start.log on Linux). Every Start outcome - success, refusal, failure, and the early
/// "skipped" returns - lands here as one timestamped UTC line. This is the durable half of the
/// Start-visibility contract (the other half is the Prestart/Runtime status line), so that a Start
/// that "does nothing" on an installed release names its cause in the log the next time anyone looks.
///
/// Logging must never block, throw, or otherwise disturb a Start: every failure here is swallowed.
/// </summary>
public static class StartLog
{
    public const string FileName = "start.log";
    private static readonly object Gate = new();

    /// <summary>The log path for a given settings file (start.log next to the settings file).</summary>
    public static string PathFor(string settingsFilePath)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(settingsFilePath));
        return dir is null ? "" : System.IO.Path.Combine(dir, FileName);
    }

    /// <summary>Append one outcome line. Never throws.</summary>
    public static void Write(string settingsFilePath, string outcome)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(settingsFilePath));
            if (dir is null) return;
            Directory.CreateDirectory(dir);
            var line = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffZ") + " " + outcome + Environment.NewLine;
            lock (Gate) File.AppendAllText(System.IO.Path.Combine(dir, FileName), line);
        }
        catch { /* a logging failure must never break or hang a machine Start */ }
    }
}
