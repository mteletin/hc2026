using System.Text;
using System.Text.Json;
using Hc2000.App.Settings;

namespace Hc2000.Tools;

public static class SettingsCommand
{
    private const string Usage = """
        usage: hc settings preview [--config-dir DIRECTORY]
               hc settings restore --apply [--config-dir DIRECTORY]

        Close the App first. Only App preferences in settings.json are selected;
        session launch configuration, ROMs, disks, tapes and snapshots are untouched.
        Preview prints the exact current bytes in base64 and the installed defaults;
        it writes nothing. Restore requires --apply and saves existing bytes to a
        new local backup before replacing settings.json. Backups may contain private
        settings; keep them secure. To recover, close the App and copy the printed
        backup file over settings.json using your file manager or a copy command.

        Exit status: 0 preview with changes or restored; 3 already identical;
        1 refusal, failure or usage error.
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["--help"] or ["preview", "--help"] or ["restore", "--help"])
        {
            output.WriteLine(Usage);
            return 0;
        }
        if (args.Length == 0 || args[0] is not ("preview" or "restore"))
        {
            error.WriteLine(Usage);
            return 1;
        }

        var apply = false;
        string? directory = null;
        for (var index = 1; index < args.Length; index++)
        {
            if (args[index] == "--apply" && !apply) apply = true;
            else if (args[index] == "--config-dir" && directory is null && index + 1 < args.Length)
                directory = args[++index];
            else { error.WriteLine("hc settings: invalid option; see hc settings --help."); return 1; }
        }
        if (apply != (args[0] == "restore"))
        {
            error.WriteLine("hc settings: restore requires --apply; preview never accepts --apply.");
            return 1;
        }

        try
        {
            var path = new SettingsStore(directory).FilePath;
            var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
            path = Path.Combine(parent, "settings.json");
            if (!Directory.Exists(parent)) throw new IOException("configuration directory does not exist or is inaccessible.");

            byte[]? previous = null;
            FileAttributes? attributes = null;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { }
            if (attributes.HasValue)
            {
                if ((attributes.Value & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0)
                    throw new IOException("settings file is a directory, link, or read-only.");
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                using var buffer = new MemoryStream();
                source.CopyTo(buffer);
                previous = buffer.ToArray();
            }
            var defaults = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new AppSettings(), AppSettings.JsonOptions));
            output.WriteLine($"Selected App preferences: {path}");
            output.WriteLine(previous is null ? "Current: missing" : $"Current bytes (base64): {Convert.ToBase64String(previous)}");
            output.WriteLine("Installed defaults:\n" + Encoding.UTF8.GetString(defaults));
            if (previous is not null && previous.AsSpan().SequenceEqual(defaults))
            {
                output.WriteLine("No change: preferences already match installed defaults.");
                return 3;
            }
            if (!apply)
            {
                output.WriteLine("Preview only; no files written.");
                return 0;
            }

            string? backup = null;
            var temporary = Path.Combine(parent, $".settings-{Guid.NewGuid():N}.tmp");
            var committed = false;
            var backupCreated = false;
            var temporaryCreated = false;
            try
            {
                if (previous is not null)
                {
                    backup = Path.Combine(parent, $"settings.json.backup-{Guid.NewGuid():N}");
                    using var destination = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    backupCreated = true;
                    destination.Write(previous);
                    destination.Flush(true);
                }
                using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    temporaryCreated = true;
                    destination.Write(defaults);
                    destination.Flush(true);
                }
                File.Move(temporary, path, overwrite: true);
                committed = true;
            }
            finally
            {
                if (temporaryCreated && File.Exists(temporary)) File.Delete(temporary);
                if (!committed && backupCreated && backup is not null && File.Exists(backup)) File.Delete(backup);
            }
            output.WriteLine(backup is null ? "Restored defaults; no previous file existed." :
                $"Restored defaults. Recover original bytes by copying {backup} over {path} with the App closed.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error.WriteLine($"hc settings: refused or failed: {ex.Message}");
            return 1;
        }
    }
}
