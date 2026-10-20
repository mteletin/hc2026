using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Hc2000.Tools;
using Xunit;

namespace Hc2000.Tools.Tests;

// One paged arena, in one child process, reclaimed on exit; never parallel with other collections.
[CollectionDefinition("paging-command", DisableParallelization = true)]
public sealed class PagingCommandCollection { }

[Collection("paging-command")]
public sealed class PagingCommandTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hc-paging-" + Guid.NewGuid().ToString("N"));
    private readonly string[] roms;
    private readonly string operations;
    public PagingCommandTests()
    {
        Directory.CreateDirectory(root);
        roms = new[] { "basic", "cpm", "if1" }.Select(n => Path.Combine(root, n)).ToArray();
        byte[] markers = [0x36, 0xa9, 0x5b];
        for (int i = 0; i < roms.Length; i++) File.WriteAllBytes(roms[i], Enumerable.Repeat(markers[i], 16384).ToArray());
        operations = Path.Combine(root, "operations.json");
        Document([]);
    }
    public void Dispose() => Directory.Delete(root, true);
    private void Document(object[] ops) => File.WriteAllText(operations, JsonSerializer.Serialize(new { v = 1, operations = ops }));
    private string[] Args(string profile = "hc2000-v2-paged-v1", string budget = "1025") =>
        ["inspect", "--profile", profile, "--diagnostic-roms", .. roms, "--operations", operations, "--max-ram-mib", budget, "--json"];
    private static (int Code, string Output, string Error) Run(string[] args)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        return (PagingCommand.Run(args, output, error), output.ToString(), error.ToString());
    }
    private Dictionary<string, string> Inputs() => Directory.GetFiles(root).ToDictionary(p => Path.GetFileName(p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
    private void Unchanged(Dictionary<string, string> before)
    {
        var after = Inputs();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var pair in before) Assert.Equal(pair.Value, after[pair.Key]);
    }
    private static void Refused((int Code, string Output, string Error) result, string reason)
    {
        Assert.Equal(1, result.Code); Assert.Equal("", result.Output);
        Assert.StartsWith("hc paging:", result.Error); Assert.Contains(reason, result.Error);
    }
    private async Task<(int Code, string Output, string Error)> Cli(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        start.ArgumentList.Add(typeof(PagingCommand).Assembly.Location);
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    [Fact]
    public async Task HelpAndNormalRootListingUseActualDispatchWithoutInputs()
    {
        foreach (var path in roms.Append(operations)) File.Delete(path);
        var help = await Cli("paging", "--help");
        Assert.Equal(0, help.Code); Assert.Equal("", help.Error);
        Assert.Contains("hc paging inspect", help.Output); Assert.Contains("1025", help.Output);
        var rootHelp = await Cli("--help");
        Assert.Equal(0, rootHelp.Code); Assert.Contains("paging", rootHelp.Error);
        var listing = await Cli();
        Assert.Equal(1, listing.Code); Assert.Contains("paging", listing.Error);
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task NativePagedBatchReportsBanksPixelsRomPrecedenceAndLockResetRetention()
    {
        if (!OperatingSystem.IsLinux()) return; // Platform refusal is covered separately, not claimed as a native pass.
        var ops = new List<object>(); var checks = new List<(int Index, Action<JsonElement> Check)>();
        void Add(object op, Action<JsonElement>? check = null) { if (check != null) checks.Add((ops.Count, check)); ops.Add(op); }
        void Out(int port, int value) => Add(new { op = "out", port, value });
        void Read(int address, string hex) => Add(new { op = "read", address, count = hex.Length / 2 }, j => Assert.Equal(hex, j.GetProperty("hex").GetString()));
        void Resolve(int address, string store, int offset, bool writable) => Add(new { op = "resolve", address }, j =>
        { Assert.Equal(store, j.GetProperty("store").GetString()); Assert.Equal(offset, j.GetProperty("offset").GetInt32()); Assert.Equal(writable, j.GetProperty("writable").GetBoolean()); });
        void In(int port, int value) => Add(new { op = "in", port }, j => Assert.Equal(value, j.GetProperty("value").GetInt32()));
        void Ram(int offset, string hex) => Add(new { op = "ram-write", offset, hex });
        void Pixel(uint argb) => Add(new { op = "pixel", x = 48, y = 40 }, j => Assert.Equal(argb, j.GetProperty("argb").GetUInt32()));
        // Distinct base/page0/page0100 markers, authored without querying the native mapping.
        Ram(0xc019, "44"); Ram(0x10019, "27"); Ram(0x410019, "D3");
        Read(0xc019, "27"); Resolve(0xc000, "Ram", 0x10000, true);
        Out(0x7dfd, 1); In(0x7dfd, 1); In(0x7efd, 0);
        Read(0xc019, "D3"); Resolve(0xc000, "Ram", 0x410000, true);
        Add(new { op = "write", address = 0xc019, value = 0xe6 });
        Add(new { op = "ram-read", offset = 0x410019, count = 1 }, j => Assert.Equal("E6", j.GetProperty("hex").GetString()));
        Out(0x7dfd, 0); Out(0x7efd, 0); Read(0xc019, "27");
        Out(0x7efd, 1); Resolve(0xc000, "Ram", 0x14000, true); Out(0x7efd, 0);
        Ram(0x4000, "00"); Ram(0x5800, "07"); Ram(0x2c000, "80"); Ram(0x2d800, "07");
        Add(new { op = "frame" }); Pixel(0xff000000); Out(0x7ffd, 8);
        Add(new { op = "frame" }); Pixel(0xffcdcdcd); Read(0x4000, "00");
        Read(0, "36"); Out(0x7e, 1); Read(0, "36"); Out(0x7ffd, 0x10); Read(0, "A9");
        Resolve(0, "Rom", 0x4000, false); Out(0x7e, 0); Read(0, "A9");
        Add(new { op = "m1", address = 8 }, j => Assert.Equal(0x5b, j.GetProperty("value").GetInt32()));
        Read(0, "5B"); Resolve(0x2800, "ShadowRam", 0, true);
        Out(0x7e, 2); Add(new { op = "write", address = 0, value = 0x92 }); Read(0, "92");
        Resolve(0, "Ram", 0, true); Resolve(0xe000, "Rom", 0x2000, false); Read(0xe000, "36");
        Out(0x7e, 3); Read(0xe000, "A9");
        Add(new { op = "reset" }); Out(0x7dfd, 1); Out(0x7ffd, 0x20);
        Out(0x7ffd, 0); Out(0x7efd, 9); Out(0x7dfd, 0);
        In(0x7ffd, 0x20); In(0x7efd, 0); In(0x7dfd, 1); Read(0xc019, "E6");
        Add(new { op = "reset" }); In(0x7ffd, 0); In(0x7dfd, 0); Read(0xc019, "27");
        Out(0x7dfd, 1); Read(0xc019, "E6");
        Document(ops.ToArray()); var before = Inputs();
        var result = await Cli(["paging", .. Args()]);
        Assert.True(result.Code == 0, result.Error); Assert.Equal("", result.Error);
        using var json = JsonDocument.Parse(result.Output); var rootJson = json.RootElement;
        Assert.Equal(1, rootJson.GetProperty("v").GetInt32()); Assert.True(rootJson.GetProperty("diagnosticOnly").GetBoolean());
        Assert.Equal("hc2000-v2-paged-v1", rootJson.GetProperty("profile").GetString());
        Assert.Equal(0x40010000, rootJson.GetProperty("ramBytes").GetInt32());
        string[] names = ["basic", "cpm", "if1"];
        for (int i = 0; i < 3; i++) Assert.Equal(before[Path.GetFileName(roms[i])], rootJson.GetProperty("inputHashes").GetProperty(names[i]).GetString());
        Assert.Equal(before[Path.GetFileName(operations)], rootJson.GetProperty("operationsHash").GetString());
        var observations = rootJson.GetProperty("observations"); Assert.Equal(ops.Count, observations.GetArrayLength());
        for (int i = 0; i < ops.Count; i++) Assert.Equal(i, observations[i].GetProperty("index").GetInt32());
        foreach (var (index, check) in checks) check(observations[index]);
        Unchanged(before);
    }

    [Fact]
    public void FaithfulProfileKeepsBaseRamAndAbsentPagingPorts()
    {
        if (!OperatingSystem.IsLinux()) return;
        Document([
            new { op = "ram-write", offset = 0xc000, hex = "73" },
            new { op = "out", port = 0x7ffd, value = 0xff }, new { op = "out", port = 0x7efd, value = 0xff }, new { op = "out", port = 0x7dfd, value = 1 },
            new { op = "in", port = 0x7ffd }, new { op = "in", port = 0x7efd }, new { op = "in", port = 0x7dfd },
            new { op = "read", address = 0xc000, count = 1 }, new { op = "resolve", address = 0xc000 }, new { op = "read", address = 0, count = 1 }
        ]);
        var before = Inputs(); var result = Run(Args("hc2000-v2", "1")); Assert.True(result.Code == 0, result.Error);
        using var json = JsonDocument.Parse(result.Output); Assert.Equal(65536, json.RootElement.GetProperty("ramBytes").GetInt32());
        var obs = json.RootElement.GetProperty("observations");
        foreach (int index in new[] { 4, 5, 6 }) Assert.Equal(255, obs[index].GetProperty("value").GetInt32());
        Assert.Equal("73", obs[7].GetProperty("hex").GetString()); Assert.Equal(0xc000, obs[8].GetProperty("offset").GetInt32());
        Assert.Equal("Ram", obs[8].GetProperty("store").GetString()); Assert.True(obs[8].GetProperty("writable").GetBoolean());
        Assert.Equal("36", obs[9].GetProperty("hex").GetString()); Unchanged(before);
    }

    [Theory]
    [InlineData("{", "")]
    [InlineData("{\"v\":2,\"operations\":[]}", "integer outside")]
    [InlineData("{\"v\":1,\"v\":1,\"operations\":[]}", "duplicate field")]
    [InlineData("{\"v\":1,\"operations\":[],\"extra\":0}", "unknown/duplicate field")]
    [InlineData("{\"v\":1,\"operations\":{}}", "array")]
    [InlineData("{\"op\":\"read\",\"address\":65535,\"count\":2}", "wraps")]
    [InlineData("{\"op\":\"read\",\"address\":0,\"count\":257}", "integer outside")]
    [InlineData("{\"op\":\"write\",\"address\":0,\"value\":256}", "integer outside")]
    [InlineData("{\"op\":\"in\",\"port\":-1}", "unsigned decimal")]
    [InlineData("{\"op\":\"in\",\"port\":1.0}", "unsigned decimal")]
    [InlineData("{\"op\":\"in\",\"port\":1e1}", "unsigned decimal")]
    [InlineData("{\"op\":\"in\",\"port\":\"1\"}", "unsigned decimal")]
    [InlineData("{\"op\":\"in\",\"port\":65536}", "integer outside")]
    [InlineData("{\"op\":\"ram-read\",\"offset\":1073807359,\"count\":2}", "physical range")]
    [InlineData("{\"op\":\"ram-write\",\"offset\":0,\"hex\":\"0\"}", "hex")]
    [InlineData("{\"op\":\"ram-write\",\"offset\":0,\"hex\":\"GG\"}", "hex")]
    [InlineData("{\"op\":\"pixel\",\"x\":0,\"y\":0}", "preceding frame")]
    [InlineData("{\"op\":\"reset\",\"value\":0}", "unknown/duplicate field")]
    [InlineData("{\"op\":\"out\",\"port\":1}", "missing required")]
    [InlineData("{\"op\":\"in\",\"port\":1,\"port\":2}", "duplicate field")]
    [InlineData("{\"op\":\"snapshot\"}", "unknown operation")]
    public void InvalidDocumentsAndFieldLocalFailuresRefuseWithoutMutation(string text, string reason)
    {
        if (!OperatingSystem.IsLinux()) return;
        File.WriteAllText(operations, text.StartsWith("{\"op\":", StringComparison.Ordinal) ? "{\"v\":1,\"operations\":[" + text + "]}" : text);
        var before = Inputs(); Refused(Run(Args()), reason); Unchanged(before);
    }

    [Theory]
    [InlineData("operations")] [InlineData("returned")] [InlineData("initialized")] [InlineData("frames")] [InlineData("input")]
    public void FiniteBudgetsRefuseBeforeExecution(string budget)
    {
        if (!OperatingSystem.IsLinux()) return;
        object[] ops = budget switch
        {
            "operations" => Enumerable.Repeat<object>(new { op = "reset" }, 129).ToArray(),
            "returned" => Enumerable.Repeat<object>(new { op = "read", address = 0, count = 256 }, 17).ToArray(),
            "initialized" => Enumerable.Repeat<object>(new { op = "ram-write", offset = 0, hex = new string('A', 512) }, 17).ToArray(),
            "frames" => Enumerable.Repeat<object>(new { op = "frame" }, 5).ToArray(),
            _ => []
        };
        Document(ops); if (budget == "input") File.WriteAllText(operations, new string(' ', 65537));
        var before = Inputs(); Refused(Run(Args()), budget switch { "operations" => "128", "frames" => "four frames", "input" => "65536", _ => "4096" }); Unchanged(before);
    }

    [Theory]
    [InlineData("profile")] [InlineData("ceiling")] [InlineData("missing")] [InlineData("duplicate")] [InlineData("unknown")] [InlineData("short-rom")] [InlineData("long-rom")]
    public void InvalidOptionsLengthsAndCeilingRefuse(string kind)
    {
        if (!OperatingSystem.IsLinux()) return;
        var args = Args();
        if (kind == "profile") args[2] = "other";
        if (kind == "ceiling") args[^2] = "1024";
        if (kind == "missing") args = args[..^1];
        if (kind == "duplicate") args = [.. args, "--json"];
        if (kind == "unknown") args = [.. args, "--snapshot", "out"];
        if (kind.EndsWith("rom", StringComparison.Ordinal)) File.WriteAllBytes(roms[0], new byte[kind == "short-rom" ? 16383 : 16385]);
        var before = Inputs(); Refused(Run(args), kind switch { "profile" => "profile", "ceiling" => "ceiling", "missing" => "required", "duplicate" => "duplicate", "unknown" => "unknown", "short-rom" => "16384", _ => "16384" }); Unchanged(before);
    }

    [Theory]
    [InlineData("fifo", false)] [InlineData("fifo", true)]
    [InlineData("directory", false)] [InlineData("directory", true)]
    [InlineData("symlink", false)] [InlineData("symlink", true)]
    public async Task ActualCliRefusesNonregularInputsWithDeadlineAndCleanup(string kind, bool rom)
    {
        if (!OperatingSystem.IsLinux()) return;
        string unsafePath = Path.Combine(root, "unsafe");
        if (kind == "fifo")
        {
            using var mkfifo = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { unsafePath } })!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await mkfifo.WaitForExitAsync(deadline.Token); Assert.Equal(0, mkfifo.ExitCode);
        }
        else if (kind == "directory") Directory.CreateDirectory(unsafePath);
        else File.CreateSymbolicLink(unsafePath, rom ? roms[0] : operations);
        var ordinaryBefore = roms.Append(operations).ToDictionary(p => p, File.ReadAllBytes);
        var args = Args(); args[rom ? 4 : 8] = unsafePath;
        Refused(await Cli(["paging", .. args]), "ordinary");
        foreach (var pair in ordinaryBefore) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        Assert.Equal(5, Directory.GetFileSystemEntries(root).Length);
        // Deleting each fixture after the child exits also verifies no lingering child/handle retains it.
        if (kind == "directory") Directory.Delete(unsafePath); else File.Delete(unsafePath);
    }

    [Fact]
    public void NonLinuxInspectRefusesBeforeOpeningAnyInput()
    {
        if (OperatingSystem.IsLinux()) return; // Explicitly unrun on the Linux qualification host.
        foreach (var path in roms.Append(operations)) File.Delete(path);
        Refused(Run(Args()), "UnsupportedPlatform"); Assert.Empty(Directory.GetFileSystemEntries(root));
    }
}
