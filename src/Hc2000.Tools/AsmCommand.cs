using Hc2000.Asm;

namespace Hc2000.Tools;

/// <summary>The hc asm adapter: source APIs are side-effect free; only the CLI writes outputs.</summary>
public static class AsmCommand
{
    public static int Run(string[] args, TextWriter error)
    {
        string? source = null;
        string? output = null;
        var hex = false; var listing = false; var symbols = false;
        byte fill = 0xFF;
        var defines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                string Value()
                {
                    if (++i >= args.Length) throw new FormatException($"Missing value for {args[i - 1]}.");
                    return args[i];
                }
                var arg = args[i];
                switch (arg)
                {
                    case "-o": output = Value(); break;
                    case "--hex": hex = true; break;
                    case "--lst": listing = true; break;
                    case "--sym": symbols = true; break;
                    case "--fill":
                        var value = Evaluate(Value());
                        if (value is < 0 or > 255) throw new FormatException("Fill must be 0..255.");
                        fill = (byte)value;
                        break;
                    case "-D": AddDefine(Value()); break;
                    default:
                        if (arg.StartsWith("-D", StringComparison.Ordinal) && arg.Length > 2) AddDefine(arg[2..]);
                        else if (arg.StartsWith('-')) throw new FormatException($"Unknown option '{arg}'.");
                        else if (source == null) source = arg;
                        else throw new FormatException("Only one source file is allowed.");
                        break;
                }
            }
            if (source == null) throw new FormatException("usage: hc asm source.asm -o out.bin [--hex] [--lst] [--sym] [-D NAME=value] [--fill 0xFF]");
            var result = Assembler.Assemble(source, new() { Fill = fill, Defines = defines });
            foreach (var diagnostic in result.Diagnostics)
                error.WriteLine($"{diagnostic.File}({diagnostic.Line},{diagnostic.Column}): {diagnostic.Severity.ToString().ToLowerInvariant()}: {diagnostic.Message}");
            if (!result.Success) return 1;
            if (output == null && result.Outputs.Count == 0) throw new FormatException("Specify -o or an OUTPUT/SAVEBIN directive.");
            if (output != null)
            {
                if (hex) File.WriteAllText(output, result.ToIntelHex());
                else File.WriteAllBytes(output, result.ToBinary());
            }
            foreach (var request in result.Outputs) File.WriteAllBytes(request.Path, result.ToBinary(request.Start, request.Length));
            var sidecar = output ?? result.Outputs[0].Path;
            if (listing) File.WriteAllText(Path.ChangeExtension(sidecar, ".lst"), result.ToListing());
            if (symbols) File.WriteAllText(Path.ChangeExtension(sidecar, ".sym"), result.ToSymbolFile());
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or OverflowException)
        {
            error.WriteLine($"hc asm: {ex.Message}");
            return 1;
        }

        int Evaluate(string expression) => Expressions.Evaluate(expression, name => defines.TryGetValue(name, out var value) ? value : throw new FormatException($"Unknown define '{name}'."));
        void AddDefine(string definition)
        {
            var equal = definition.IndexOf('=');
            if (equal <= 0 || equal == definition.Length - 1) throw new FormatException("Define expects NAME=value.");
            var name = definition[..equal];
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$")) throw new FormatException("Invalid define name.");
            defines[name] = Evaluate(definition[(equal + 1)..]);
        }
    }
}
