using Hc2000.Disasm;

namespace Hc2000.Tools;

public static class DisasmCommand
{
    public static int Run(string[] args, TextWriter error)
    {
        try
        {
            string? input = null, output = null, ctl = null, xref = null, map = null, mapConfig = null;
            var origin = 0;
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => ++i < args.Length ? args[i] : throw new FormatException("Missing value for " + args[i - 1]);
                switch (args[i])
                {
                    case "--org": origin = ControlFile.Hex(Next()); break;
                    case "--ctl": ctl = Next(); break;
                    case "--xref": xref = Next(); break;
                    case "--map": map = Next(); break;
                    case "--map-config": mapConfig = Next(); break;
                    case "-o": output = Next(); break;
                    default:
                        if (args[i].StartsWith('-') || input is not null) throw new FormatException("Unknown option " + args[i]);
                        input = args[i]; break;
                }
            }
            if (input is null || output is null) throw new FormatException("usage: hc disasm rom.bin --org E000 [--ctl file.ctl] [--map file.hcm [--map-config id]] -o out.asm [--xref file.txt]");
            var control = ControlFile.Parse(ctl is null ? "" : File.ReadAllText(ctl));
            var result = new Disassembler().Disassemble(File.ReadAllBytes(input), origin, control,
                map is null ? null : ExecutionMap.Parse(File.ReadAllBytes(map)), mapConfig);
            foreach (var diagnostic in result.Diagnostics) error.WriteLine($"{ctl ?? input}({diagnostic.Line}): error: {diagnostic.Message}");
            if (!result.Success) return 1;
            File.WriteAllText(output, result.Source);
            if (xref is not null) File.WriteAllText(xref, result.CrossReferences);
            error.WriteLine($"code {result.Statistics.CodeBytes}, data {result.Statistics.DataBytes}, unknown {result.Statistics.UnknownBytes} bytes");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        { error.WriteLine("hc disasm: " + ex.Message); return 1; }
    }
}
