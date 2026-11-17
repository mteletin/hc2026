using Hc2000.Asm;

namespace Hc2000.Tools;

/// <summary>The hc asm adapter: source APIs are side-effect free; only the CLI writes outputs.</summary>
public static class AsmCommand
{
    public const string Usage = "usage: hc asm source.asm -o out.bin [--hex] [--lst] [--sym] [-D NAME=value] [-I dir] [--target name [--library src-library]] [--fill 0xFF]";

    public static int Run(string[] args, TextWriter error)
    {
        string? source = null;
        string? output = null;
        var hex = false; var listing = false; var symbols = false;
        byte fill = 0xFF;
        var defines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var includes = new List<string>();
        LibraryTarget? target = null;
        string? library = null;
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
                    case "-I": includes.Add(Path.GetFullPath(Value())); break;
                    case "--library": library = Path.GetFullPath(Value()); break;
                    case "--target":
                        var name = Value();
                        target = LibraryTargets.Find(name) ?? throw new FormatException($"Unknown target '{name}'; the targets are {LibraryTargets.Names}.");
                        break;
                    default:
                        if (arg.StartsWith("-D", StringComparison.Ordinal) && arg.Length > 2) AddDefine(arg[2..]);
                        else if (arg.StartsWith("-I", StringComparison.Ordinal) && arg.Length > 2) includes.Add(Path.GetFullPath(arg[2..]));
                        else if (arg.StartsWith('-')) throw new FormatException($"Unknown option '{arg}'.");
                        else if (source == null) source = arg;
                        else throw new FormatException("Only one source file is allowed.");
                        break;
                }
            }
            if (source == null) throw new FormatException(Usage);
            if (target is not null)
            {
                // --target is -D TARGET=<id> -D TARGET_<NAME>=1 plus the target's targets.inc and the shared libraries on
                // the search path, after any -I (decision hc2000-asm-library-build-v1, contract §1). The library is the one
                // named, else the one the source sits in, else the copy installed with hc: a program on a remote host's
                // mounted HC2000 drive builds as the library's own do.
                var root = (library is not null && Directory.Exists(Path.Combine(library, "shared")) ? library : null)
                    ?? (library is null ? LibraryTargets.FindLibraryRoot(Path.GetDirectoryName(Path.GetFullPath(source))!) ?? Installed() : null)
                    ?? throw new FormatException(library is not null ? $"--library {library} has no shared/ folder."
                        : "--target needs the source library: the source is not under a src-library folder with shared/, and hc has none installed beside it; pass --library <src-library>.");
                foreach (var (name, value) in LibraryTargets.Defines(target)) defines.TryAdd(name, value);
                includes.AddRange(LibraryTargets.IncludePaths(root, target));
            }
            var result = Assembler.Assemble(source, new() { Fill = fill, Defines = defines, IncludePaths = includes });
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

        static string? Installed() => Directory.Exists(Path.Combine(AppContext.BaseDirectory, "src-library", "shared"))
            ? Path.Combine(AppContext.BaseDirectory, "src-library") : null;
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
