using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hc2000.Asm;
using Hc2000.Machine.Storage;
using Hc2000.Tools;

namespace Hc2000.Examples;

/// <summary>Artifact-only orchestration of the existing hc asm/tap commands. Never constructs a machine.</summary>
public static class ExpansionMailboxAssets
{
    private const string SourceDirectory = "examples/expansion-mailbox";
    public static readonly string[] PublishedNames = ["EXPMAIL.tap", "instructions.html", "provenance.txt"];

    public static void Generate(string repository, string output, string configuration)
    {
        repository = Path.GetFullPath(repository);
        output = Path.GetFullPath(output);
        if (configuration is not ("Debug" or "Release")) throw new ArgumentException("Configuration must be Debug or Release.");
        var first = Directory.CreateTempSubdirectory("expansion-mailbox-");
        var second = Directory.CreateTempSubdirectory("expansion-mailbox-");
        try
        {
            Build(repository, first.FullName);
            Build(repository, second.FullName);
            foreach (var name in new[] { "side.bin", "main.bin", "EXPMAIL.tap" })
                if (!File.ReadAllBytes(Path.Combine(first.FullName, name)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(second.FullName, name))))
                    throw new InvalidDataException("Non-deterministic example output: " + name);
            var manifest = new StringBuilder("EXPMAIL provenance v1\n");
            manifest.AppendLine("source-commit " + Git(repository, "rev-parse", "HEAD"));
            // Tracked changes only: an untracked scan made a cold checkout take over 35 s (no tests ran).
            manifest.AppendLine("source-dirty " + (Git(repository, "status", "--porcelain", "--untracked-files=no").Length != 0 ? "true" : "false"));
            manifest.AppendLine("configuration " + configuration);
            manifest.AppendLine("producer repository-built hc asm / hc tap command handlers; no downloaded inputs");
            manifest.AppendLine("commands (STAGE is a fresh private directory; sources copied byte-for-byte):");
            manifest.AppendLine("hc asm STAGE/side.asm -o STAGE/side.bin --sym");
            manifest.AppendLine("hc asm STAGE/main.asm -o STAGE/main.bin --sym");
            manifest.AppendLine("hc tap create STAGE/EXPMAIL.tap");
            manifest.AppendLine("hc tap put STAGE/EXPMAIL.tap STAGE/main.bin --name EXPMAIL --type 3 --start 32768 --variables 0");
            manifest.AppendLine("deterministic two-distinct-fresh-staging-builds byte-identical side.bin main.bin EXPMAIL.tap");
            foreach (var path in new[] { SourceDirectory + "/main.asm", SourceDirectory + "/side.asm", SourceDirectory + "/instructions.html",
                "tools/ExpansionMailboxBuilder/Program.cs", "tools/ExpansionMailboxBuilder/ExpansionMailboxAssets.cs", "tools/ExpansionMailboxBuilder/ExpansionMailboxBuilder.csproj",
                "src/Hc2000.App/ExpansionMailbox.targets", "src/Hc2000.App/Hc2000.App.csproj",
                "src/Hc2000.Tools/AsmCommand.cs", "src/Hc2000.Tools/StorageCommand.cs", "src/Hc2000.Machine/Storage/Tap.cs" }
                .Concat(Directory.GetFiles(Path.Combine(repository, "src/Hc2000.Asm"), "*.cs").Select(path => Path.GetRelativePath(repository, path).Replace('\\', '/'))).Order(StringComparer.Ordinal))
                manifest.AppendLine(Receipt("input", path, File.ReadAllBytes(Path.Combine(repository, path))));
            foreach (var assembly in new[] { typeof(Assembler).Assembly.Location, typeof(AsmCommand).Assembly.Location, typeof(Tap).Assembly.Location, typeof(ExpansionMailboxAssets).Assembly.Location })
                manifest.AppendLine(Receipt("tool", Path.GetFileName(assembly), File.ReadAllBytes(assembly)));
            foreach (var name in new[] { "side.bin", "main.bin", "EXPMAIL.tap", "instructions.html" })
                manifest.AppendLine(Receipt("output", name, File.ReadAllBytes(Path.Combine(first.FullName, name))));
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
        foreach (var name in new[] { "main.asm", "side.asm", "instructions.html" })
            File.Copy(Path.Combine(repository, SourceDirectory, name), Path.Combine(stage, name));
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        foreach (var name in new[] { "side", "main" })
        {
            if (AsmCommand.Run([Path.Combine(stage, name + ".asm"), "-o", Path.Combine(stage, name + ".bin"), "--sym"], errors) != 0)
                throw new InvalidDataException(errors.ToString());
            var result = Assembler.Assemble(Path.Combine(stage, name + ".asm"));
            int origin = name == "side" ? 0 : 0x8000, limit = name == "side" ? 4096 : 0xc000;
            if (!result.Success || result.Segments.Count == 0 || result.Segments.Min(s => s.Address) != origin || result.Segments.Any(s => s.Address < origin || s.Address + s.Bytes.Length > limit))
                throw new InvalidDataException(name + " assembly outside its pinned bounds.");
        }
        var tap = Path.Combine(stage, "EXPMAIL.tap");
        if (StorageCommand.Run("tap", ["create", tap], TextWriter.Null, errors) != 0 ||
            StorageCommand.Run("tap", ["put", tap, Path.Combine(stage, "main.bin"), "--name", "EXPMAIL", "--type", "3", "--start", "32768", "--variables", "0"], TextWriter.Null, errors) != 0)
            throw new InvalidDataException(errors.ToString());
        var file = Tap.Load(File.ReadAllBytes(tap)).List().Single();
        if (file.Name != "EXPMAIL" || file.Type != 3 || file.Start != 32768 || file.Variables != 0 || !file.Data.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(stage, "main.bin"))))
            throw new InvalidDataException("TAP does not contain the exact CODE payload.");
    }

    public static void Validate(string directory)
    {
        foreach (var name in PublishedNames)
            if (!File.Exists(Path.Combine(directory, name)) || new FileInfo(Path.Combine(directory, name)).Length == 0)
                throw new InvalidDataException("Missing example asset: " + name);
        var manifest = File.ReadAllLines(Path.Combine(directory, "provenance.txt"));
        foreach (var name in new[] { "EXPMAIL.tap", "instructions.html" })
            if (!manifest.Contains(Receipt("output", name, File.ReadAllBytes(Path.Combine(directory, name))), StringComparer.Ordinal))
                throw new InvalidDataException("Invalid example asset hash: " + name);
        var files = Tap.Load(File.ReadAllBytes(Path.Combine(directory, "EXPMAIL.tap"))).List();
        if (files.Count != 1 || files[0] is not { Name: "EXPMAIL", Type: 3, Start: 32768, Variables: 0 } || files[0].Data.Length is < 1 or > 16384)
            throw new InvalidDataException("Invalid example CODE file.");
    }

    internal static string Receipt(string kind, string name, byte[] bytes) =>
        $"{kind} {Convert.ToHexStringLower(SHA256.HashData(bytes))} {bytes.Length.ToString(CultureInfo.InvariantCulture)} {name}";

    internal static string Git(string repository, params string[] arguments)
    {
        using var process = new Process { StartInfo = new("git") { WorkingDirectory = repository, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        // Read while git runs (a full pipe would stall it), and give a cold, loaded box the same 240 s as the disk builder.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(240000)) { process.Kill(true); throw new InvalidOperationException("git provenance timed out."); }
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("git provenance failed: " + error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult().Trim();
    }
}
