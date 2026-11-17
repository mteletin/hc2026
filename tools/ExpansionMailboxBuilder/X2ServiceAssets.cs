using System.Globalization;
using System.Text;
using Hc2000.Asm;
using Hc2000.Machine.Storage;
using Hc2000.Tools;

namespace Hc2000.Examples;

/// <summary>Builds the explicit main-owned X2 service as an ordinary guest CODE tape.</summary>
public static class X2ServiceAssets
{
    private const string SourceDirectory = "examples/x2-service";
    public static readonly string[] PublishedNames = ["X2VGA.tap", "instructions.html", "main.asm", "x2.service.asm", "provenance.txt"];

    public static void Generate(string repository, string output, string configuration)
    {
        repository = Path.GetFullPath(repository);
        output = Path.GetFullPath(output);
        if (configuration is not ("Debug" or "Release")) throw new ArgumentException("Configuration must be Debug or Release.");
        var first = Directory.CreateTempSubdirectory("x2-service-");
        var second = Directory.CreateTempSubdirectory("x2-service-");
        try
        {
            Build(repository, first.FullName);
            Build(repository, second.FullName);
            foreach (var name in new[] { "main.bin", "X2VGA.tap" })
                if (!File.ReadAllBytes(Path.Combine(first.FullName, name)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(second.FullName, name))))
                    throw new InvalidDataException("Non-deterministic X2 output: " + name);
            var manifest = new StringBuilder("X2 VGA provenance v1\n");
            manifest.AppendLine("source-commit " + ExpansionMailboxAssets.Git(repository, "rev-parse", "HEAD"));
            manifest.AppendLine("source-dirty " + (ExpansionMailboxAssets.Git(repository, "status", "--porcelain", "--untracked-files=no").Length != 0 ? "true" : "false"));
            manifest.AppendLine("configuration " + configuration);
            manifest.AppendLine("producer repository-built hc asm / hc tap command handlers; no downloaded inputs");
            manifest.AppendLine("hc asm STAGE/main.asm -o STAGE/main.bin --sym");
            manifest.AppendLine("hc tap create STAGE/X2VGA.tap");
            manifest.AppendLine("hc tap put STAGE/X2VGA.tap STAGE/main.bin --name X2VGA --type 3 --start 32768 --variables 0");
            manifest.AppendLine("two distinct fresh staging builds produced identical main.bin and X2VGA.tap");
            foreach (var path in new[] { "main.asm", "x2.service.asm", "instructions.html" })
                manifest.AppendLine(ExpansionMailboxAssets.Receipt("input", SourceDirectory + "/" + path, File.ReadAllBytes(Path.Combine(first.FullName, path))));
            foreach (var name in PublishedNames.Where(name => name != "provenance.txt"))
                manifest.AppendLine(ExpansionMailboxAssets.Receipt("output", name, File.ReadAllBytes(Path.Combine(first.FullName, name))));
            File.WriteAllText(Path.Combine(first.FullName, "provenance.txt"), manifest.ToString().Replace("\r\n", "\n"), new UTF8Encoding(false));
            Validate(first.FullName);
            Directory.CreateDirectory(output);
            foreach (var name in PublishedNames) File.Copy(Path.Combine(first.FullName, name), Path.Combine(output, name), true);
            Validate(output);
        }
        finally { first.Delete(true); second.Delete(true); }
    }

    private static void Build(string repository, string stage)
    {
        foreach (var name in new[] { "main.asm", "x2.service.asm", "instructions.html" })
            File.Copy(Path.Combine(repository, SourceDirectory, name), Path.Combine(stage, name));
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var source = Path.Combine(stage, "main.asm");
        var binary = Path.Combine(stage, "main.bin");
        if (AsmCommand.Run([source, "-o", binary, "--sym"], errors) != 0)
            throw new InvalidDataException(errors.ToString());
        var result = Assembler.Assemble(source);
        if (!result.Success || result.Segments.Count == 0 || result.Segments.Min(segment => segment.Address) != 0x8000 ||
            result.Segments.Any(segment => segment.Address < 0x8000 || segment.Address + segment.Bytes.Length > 0xc000))
            throw new InvalidDataException("X2 assembly outside its pinned bounds.");
        var tape = Path.Combine(stage, "X2VGA.tap");
        if (StorageCommand.Run("tap", ["create", tape], TextWriter.Null, errors) != 0 ||
            StorageCommand.Run("tap", ["put", tape, binary, "--name", "X2VGA", "--type", "3", "--start", "32768", "--variables", "0"], TextWriter.Null, errors) != 0)
            throw new InvalidDataException(errors.ToString());
        var file = Tap.Load(File.ReadAllBytes(tape)).List().Single();
        if (file.Name != "X2VGA" || file.Type != 3 || file.Start != 32768 || file.Variables != 0 ||
            !file.Data.AsSpan().SequenceEqual(File.ReadAllBytes(binary)))
            throw new InvalidDataException("X2 tape does not contain the exact CODE payload.");
    }

    public static void Validate(string directory)
    {
        foreach (var name in PublishedNames)
            if (!File.Exists(Path.Combine(directory, name)) || new FileInfo(Path.Combine(directory, name)).Length == 0)
                throw new InvalidDataException("Missing X2 asset: " + name);
        var manifest = File.ReadAllLines(Path.Combine(directory, "provenance.txt"));
        foreach (var name in PublishedNames.Where(name => name != "provenance.txt"))
            if (!manifest.Contains(ExpansionMailboxAssets.Receipt("output", name, File.ReadAllBytes(Path.Combine(directory, name))), StringComparer.Ordinal))
                throw new InvalidDataException("Invalid X2 asset hash: " + name);
        var files = Tap.Load(File.ReadAllBytes(Path.Combine(directory, "X2VGA.tap"))).List();
        if (files.Count != 1 || files[0] is not { Name: "X2VGA", Type: 3, Start: 32768, Variables: 0 } || files[0].Data.Length is < 6 or > 16384)
            throw new InvalidDataException("Invalid X2 CODE file.");
    }
}
