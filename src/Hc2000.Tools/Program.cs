// The `hc` toolkit: thin host adapters for asm, disasm, dsk, hdd, fs, tap, tape, rom and paging.

namespace Hc2000.Tools;

public static class Program
{
    private static readonly string[] Subcommands = { "asm", "disasm", "dsk", "hdd", "fs", "tap", "tape", "rom", "paging" };

    public static int Main(string[] args)
    {
        if (args is ["--help"])
        {
            PrintUsage();
            return 0;
        }
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var sub = args[0];
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
        if (subcommand.Equals("asm", StringComparison.OrdinalIgnoreCase))
            return AsmCommand.Run(rest, Console.Error);
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

    private static void PrintUsage()
    {
        Console.Error.WriteLine("usage: hc <subcommand> [options]");
        foreach (var s in Subcommands)
        {
            Console.Error.WriteLine($"  {s}");
        }
    }
}
