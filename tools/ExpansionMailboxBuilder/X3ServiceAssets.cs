using System.Globalization;
using System.Text;
using Hc2000.Asm;
using Hc2000.Machine.Storage;
using Hc2000.Tools;

namespace Hc2000.Examples;

/// <summary>Builds the explicit X3 command entry as an ordinary guest CODE tape.</summary>
public static class X3ServiceAssets
{
    private const string SourceDirectory = "examples/x3-service";
    private static readonly string[] Sources = ["main.asm", "x3.service.asm", "player.asm", "sentinel.asm", "sentinel.data.asm", "instructions.html"];
    public static readonly string[] PublishedNames = ["X3SOUND.tap", "X3MOD.tap", "X3-sentinel.mod", .. Sources, "provenance.txt"];

    public static void Generate(string repository, string output, string configuration)
    {
        repository = Path.GetFullPath(repository);
        output = Path.GetFullPath(output);
        if (configuration is not ("Debug" or "Release")) throw new ArgumentException("Configuration must be Debug or Release.");
        var first = Directory.CreateTempSubdirectory("x3-service-");
        var second = Directory.CreateTempSubdirectory("x3-service-");
        try
        {
            Build(repository, first.FullName);
            Build(repository, second.FullName);
            foreach (var name in new[] { "main.bin", "player.bin", "X3SOUND.tap", "X3MOD.tap", "X3-sentinel.mod" })
                if (!File.ReadAllBytes(Path.Combine(first.FullName, name)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(second.FullName, name))))
                    throw new InvalidDataException("Non-deterministic X3 output: " + name);
            var manifest = new StringBuilder("X3 sound provenance v1\n");
            manifest.AppendLine("source-commit " + ExpansionMailboxAssets.Git(repository, "rev-parse", "HEAD"));
            manifest.AppendLine("source-dirty " + (ExpansionMailboxAssets.Git(repository, "status", "--porcelain", "--untracked-files=no").Length != 0 ? "true" : "false"));
            manifest.AppendLine("configuration " + configuration);
            manifest.AppendLine("producer repository-built hc asm / hc tap command handlers; no downloaded inputs");
            manifest.AppendLine("hc asm STAGE/main.asm -o STAGE/main.bin --sym");
            manifest.AppendLine("hc tap create STAGE/X3SOUND.tap");
            manifest.AppendLine("hc tap put STAGE/X3SOUND.tap STAGE/main.bin --name X3SOUND --type 3 --start 32768 --variables 0");
            manifest.AppendLine("hc asm STAGE/player.asm -o STAGE/player.bin --sym; hc asm STAGE/sentinel.asm -o STAGE/X3-sentinel.mod");
            manifest.AppendLine("hc tap create STAGE/X3MOD.tap; hc tap put STAGE/X3MOD.tap STAGE/player.bin --name X3MOD --type 3 --start 32768 --variables 0");
            manifest.AppendLine("X3 sentinel: original synthetic sample data authored in sentinel.data.asm for this example; no third-party song");
            manifest.AppendLine("two distinct fresh staging builds produced identical CODE binaries, tapes and sentinel MOD");
            foreach (var name in Sources)
                manifest.AppendLine(ExpansionMailboxAssets.Receipt("input", SourceDirectory + "/" + name, File.ReadAllBytes(Path.Combine(first.FullName, name))));
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
        foreach (var name in Sources)
            File.Copy(Path.Combine(repository, SourceDirectory, name), Path.Combine(stage, name));
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        BuildTape(stage, "main.asm", "main.bin", "X3SOUND", errors);
        BuildTape(stage, "player.asm", "player.bin", "X3MOD", errors);
        if (AsmCommand.Run([Path.Combine(stage, "sentinel.asm"), "-o", Path.Combine(stage, "X3-sentinel.mod")], errors) != 0)
            throw new InvalidDataException(errors.ToString());
        var module = File.ReadAllBytes(Path.Combine(stage, "X3-sentinel.mod"));
        if (module.Length != 2364 || !module.AsSpan(1080, 4).SequenceEqual("M.K."u8))
            throw new InvalidDataException("Invalid authored X3 sentinel MOD.");
    }

    private static void BuildTape(string stage, string sourceName, string binaryName, string tapeName, StringWriter errors)
    {
        var source = Path.Combine(stage, sourceName);
        var binary = Path.Combine(stage, binaryName);
        if (AsmCommand.Run([source, "-o", binary, "--sym"], errors) != 0)
            throw new InvalidDataException(errors.ToString());
        var result = Assembler.Assemble(source);
        if (!result.Success || result.Segments.Count == 0 || result.Segments.Min(segment => segment.Address) != 0x8000 ||
            result.Segments.Any(segment => segment.Address < 0x8000 || segment.Address + segment.Bytes.Length > 0xc000))
            throw new InvalidDataException("X3 assembly outside its pinned bounds.");
        var tape = Path.Combine(stage, tapeName + ".tap");
        if (StorageCommand.Run("tap", ["create", tape], TextWriter.Null, errors) != 0 ||
            StorageCommand.Run("tap", ["put", tape, binary, "--name", tapeName, "--type", "3", "--start", "32768", "--variables", "0"], TextWriter.Null, errors) != 0)
            throw new InvalidDataException(errors.ToString());
        var file = Tap.Load(File.ReadAllBytes(tape)).List().Single();
        if (file.Name != tapeName || file.Type != 3 || file.Start != 32768 || file.Variables != 0 ||
            !file.Data.AsSpan().SequenceEqual(File.ReadAllBytes(binary)))
            throw new InvalidDataException("X3 tape does not contain the exact CODE payload.");
    }

    public static void Validate(string directory)
    {
        foreach (var name in PublishedNames)
            if (!File.Exists(Path.Combine(directory, name)) || new FileInfo(Path.Combine(directory, name)).Length == 0)
                throw new InvalidDataException("Missing X3 asset: " + name);
        var manifest = File.ReadAllLines(Path.Combine(directory, "provenance.txt"));
        foreach (var name in PublishedNames.Where(name => name != "provenance.txt"))
            if (!manifest.Contains(ExpansionMailboxAssets.Receipt("output", name, File.ReadAllBytes(Path.Combine(directory, name))), StringComparer.Ordinal))
                throw new InvalidDataException("Invalid X3 asset hash: " + name);
        foreach (var name in new[] { "X3SOUND", "X3MOD" })
        {
            var files = Tap.Load(File.ReadAllBytes(Path.Combine(directory, name + ".tap"))).List();
            if (files.Count != 1 || files[0].Name != name || files[0] is not { Type: 3, Start: 32768, Variables: 0 } || files[0].Data.Length is < 3 or > 16384)
                throw new InvalidDataException("Invalid X3 CODE file: " + name);
        }
        var module = File.ReadAllBytes(Path.Combine(directory, "X3-sentinel.mod"));
        if (module.Length != 2364 || !module.AsSpan(1080, 4).SequenceEqual("M.K."u8))
            throw new InvalidDataException("Invalid X3 sentinel.");
    }
}
