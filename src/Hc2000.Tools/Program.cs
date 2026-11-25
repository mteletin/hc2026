// The `hc` toolkit: thin host adapters for asm, build, disasm, dsk, hdd, fs, tap, tape, rom and paging.

using System.Reflection;

namespace Hc2000.Tools;

public static class Program
{
    private static readonly string[] Subcommands = { "asm", "build", "disasm", "dsk", "hdd", "fs", "tap", "tape", "rom", "paging", "backup", "support", "settings" };

    /// <summary>The release's feature flags; the installed manifest unless a test sets another.</summary>
    internal static Hc2000.Features.FeatureSet Features { get; set; } = Hc2000.Features.FeatureSet.Current;

    public static int Main(string[] args)
    {
        if (Features.Diagnostic is { } flagDiagnostic) Console.Error.WriteLine("hc: " + flagDiagnostic);
        if (args is ["--help"])
        {
            PrintUsage();
            return 0;
        }
        if (args is ["--version"])
        {
            Console.WriteLine(VersionString());
            return 0;
        }
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var sub = args[0];
        if (sub.Equals(Hc2000.FeatureSample.FeatureSampleGate.Subcommand, StringComparison.OrdinalIgnoreCase)) return Dispatch(sub, args[1..]);
        if (Subcommands.Contains(sub, StringComparer.OrdinalIgnoreCase))
        {
            return Dispatch(sub, args[1..]);
        }

        Console.Error.WriteLine($"hc: unknown subcommand '{sub}'");
        PrintUsage();
        return 1;
    }

    /// <summary>
    /// Runs one toolkit subcommand through its host I/O adapter.
    /// </summary>
    public static int Dispatch(string subcommand, string[] rest)
    {
        if (subcommand.Equals(Hc2000.FeatureSample.FeatureSampleGate.Subcommand, StringComparison.OrdinalIgnoreCase))
            return FeatureSample(Features, Console.Out, Console.Error);
        if (subcommand.Equals("settings", StringComparison.OrdinalIgnoreCase))
            return SettingsCommand.Run(rest, Console.Out, Console.Error);
        if (subcommand.Equals("backup", StringComparison.OrdinalIgnoreCase))
            return MediaBackupCommand.Run(rest, Console.Out, Console.Error);
        if (subcommand.Equals("support", StringComparison.OrdinalIgnoreCase))
            return SupportCommand.Run(rest, Console.Out, Console.Error);
        if (subcommand.Equals("asm", StringComparison.OrdinalIgnoreCase))
            return AsmCommand.Run(rest, Console.Error);
        if (subcommand.Equals("build", StringComparison.OrdinalIgnoreCase))
            return BuildCommand.Run(rest, Console.Out, Console.Error);
        if (subcommand.Equals("disasm", StringComparison.OrdinalIgnoreCase))
            return DisasmCommand.Run(rest, Console.Error);
        if (subcommand.Equals("paging", StringComparison.OrdinalIgnoreCase))
            return PagingCommand.Run(rest, Console.Out, Console.Error);
        if (subcommand.Equals("tape", StringComparison.OrdinalIgnoreCase))
            return TapeCommand.Run(rest, Console.Out, Console.Error, Hc2000.Machine.Tape.TapeCodecRegistry.Installed());
        if (subcommand.ToLowerInvariant() is "dsk" or "hdd" or "fs" or "tap" or "rom")
            return StorageCommand.Run(subcommand, rest, Console.Out, Console.Error);
        Console.Error.WriteLine($"hc: unknown subcommand '{subcommand}'");
        return 1;
    }

    /// <summary>hc feature-sample, the flag mechanism's sample: run in a release that ships it on, refused otherwise.</summary>
    internal static int FeatureSample(Hc2000.Features.FeatureSet features, TextWriter stdout, TextWriter stderr) =>
        Hc2000.FeatureSample.FeatureSampleGate.Run(features, "hc " + Hc2000.FeatureSample.FeatureSampleGate.Subcommand, stdout, stderr);

    /// <summary>The subcommands this release lists: the fixed ones, and a flagged one only when it is shipped on.</summary>
    internal static IReadOnlyList<string> Listed(Hc2000.Features.FeatureSet features) =>
        Hc2000.FeatureSample.FeatureSampleGate.Available(features) ? [.. Subcommands, Hc2000.FeatureSample.FeatureSampleGate.Subcommand] : Subcommands;

    private static void PrintUsage()
    {
        Console.Error.WriteLine("usage: hc [--version] <subcommand> [options]");
        foreach (var s in Listed(Features))
        {
            Console.Error.WriteLine($"  {s}");
        }
        Console.Error.WriteLine("  hc fs check --help       Read-only CP/M disk health check and exit statuses");
        Console.Error.WriteLine("  hc fs survey --help      Survey a folder of CP/M disk images and exit statuses");
        Console.Error.WriteLine("  hc fs locate --help      Find CP/M files across a folder of disk images and exit statuses");
        Console.Error.WriteLine("  hc fs duplicates --help  Find identical CP/M files across a folder of disk images and exit statuses");
        Console.Error.WriteLine("  hc fs variants --help    Find same-name CP/M files with differing contents across disk images and exit statuses");
        Console.Error.WriteLine("  hc fs compare --help     Compare stored files between two CP/M disk images and exit statuses");
        Console.Error.WriteLine("  hc fs export --help      Export all CP/M files from a disk image to a safe host folder");
        Console.Error.WriteLine("  hc fs from-folder --help Create a CP/M data-disk image from a host folder and exit statuses");
        Console.Error.WriteLine("  hc fs to-hdd --help      Preview and gather CP/M floppies onto a new data hard disk");
        Console.Error.WriteLine("  hc fs repack --help      Copy a CP/M data disk's files safely to a new 40- or 80-track disk");
        Console.Error.WriteLine("  hc fs restore --help     Restore a replayable CP/M file export to a new 40- or 80-track data disk");
    }

    /// <summary>
    /// The bare assembly version: the informational version with any build
    /// suffix (commit) stripped. The installer pins InformationalVersion to
    /// the version itself, so <c>hc --version</c> equals the installer
    /// version (hc2000-emu-installer-ships-hc).
    /// </summary>
    public static string VersionString()
    {
        var asm = typeof(Program).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var bare = informational.Split('+', '-').First();
            if (Version.TryParse(bare, out _))
            {
                return bare;
            }
        }
        return asm.GetName().Version?.ToString() ?? "0.0.0";
    }
}
