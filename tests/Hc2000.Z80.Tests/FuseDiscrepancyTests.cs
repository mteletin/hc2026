using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using Xunit.Sdk;

namespace Hc2000.Z80.Tests;

public class FuseDiscrepancyTests
{
    private const string Revision = "bb05647bbf9327c973f1794c48c32b3f569fca04";
    private const string ExpectedSha256 = "15a6946f4addcf97e137b5bdd1d5fdb08124ff91f1b169f36a8bf4afe4bab6e4";
    private const string InputSha256 = "9f36e866f22e72ff1f8bf2100bf70ffbf58edd97b453500aab60acf1f403ebbb";

    [Z80AssetFact]
    public void ExactDiscrepancyInventoryDoesNotHideAnyNewFailure()
    {
        using var stream = typeof(FuseDiscrepancyTests).Assembly.GetManifestResourceStream("Hc2000.Z80.Tests.Data.FuseDiscrepancies.json")!;
        using var ledger = JsonDocument.Parse(stream);
        var root = Z80AssetFactAttribute.AssetRoot()!;
        Assert.Equal(InputSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "fuse", "tests.in")))));
        Assert.Equal(ExpectedSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "fuse", "tests.expected")))));
        var entries = ledger.RootElement.GetProperty("cases").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
        var cases = new List<object>();
        var actualIds = new List<string>();
        var rawCases = 0;
        foreach (var row in FuseTests.Cases())
        {
            var replay = FuseTests.ReplayCase((string)row[0]);
            rawCases++;
            var differences = Differences(replay);
            var rawFailure = Record.Exception(() => FuseTests.AssertRaw(replay));
            Assert.Equal(differences.Count > 0, rawFailure != null);
            if (rawFailure != null) Assert.IsAssignableFrom<XunitException>(rawFailure);
            if (differences.Count == 0) continue;
            actualIds.Add(replay.Name);
            Assert.True(entries.TryGetValue(replay.Name, out var entry), $"Unregistered raw failure: {replay.Name}");
            var declared = entry.GetProperty("differences").Deserialize<List<Difference>>()!;
            Assert.Equal(declared, differences); // exact raw fields/events, never replacement expectations
            cases.Add(new { id = replay.Name, declaration = entry, differences });
        }
        Assert.Equal(new[] { "10", "20_2", "28_1", "30_2", "38_1", "76", "edb2_1", "edb3_1", "edb9_2", "edba_1", "edbb_1" }, actualIds.Order(StringComparer.Ordinal));
        ValidateLedgerKeys(entries.Keys, actualIds);
        Assert.Equal(ledger.RootElement.GetProperty("rawCases").GetInt32(), rawCases);
        Assert.Equal(ledger.RootElement.GetProperty("rawFailed").GetInt32(), actualIds.Count);
        Assert.Equal(ledger.RootElement.GetProperty("rawPassed").GetInt32(), rawCases - actualIds.Count);
        var path = Environment.GetEnvironmentVariable("Z80_FUSE_DISCREPANCY_REPORT");
        if (path != null)
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                fixtureRevision = Revision, inputSha256 = InputSha256, expectedSha256 = ExpectedSha256,
                normativeDecision = "hc2000-z80-oracle-conflicts/adr v2 + case-review-cc0a98c v1",
                rawCases, rawPassed = rawCases - actualIds.Count, rawFailed = actualIds.Count, cases
            }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    internal static void ValidateLedgerKeys(IEnumerable<string> declared, IEnumerable<string> actual)
        => Assert.Equal(declared.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));

    [Fact]
    public void SurplusStaleLedgerEntryIsRejected()
    {
        ValidateLedgerKeys(["20_2"], ["20_2"]);
        Assert.ThrowsAny<XunitException>(() => ValidateLedgerKeys(["20_2", "stale"], ["20_2"]));
    }

    [Z80AssetTheory]
    [InlineData("edb3_1", 0x03, 0x0001)]
    [InlineData("edb9_2", 0xaf, 0x7a46)]
    public void PinnedRepeatFieldsAreIndependentOfLegacyExpected(string name, int flags, int wz)
    {
        var replay = FuseTests.ReplayCase(name);
        Assert.Equal((byte)flags, replay.Cpu.R.F);
        Assert.Equal((ushort)wz, replay.Cpu.R.WZ);
        Assert.Throws<TrueException>(() => FuseTests.AssertRaw(replay)); // raw case still FAILS
    }

    [Z80AssetTheory]
    [InlineData("20_2", 7, 1, 0x40)]
    [InlineData("28_1", 7, 1, 0x8e)]
    [InlineData("30_2", 7, 1, 0x50)]
    [InlineData("38_1", 7, 1, 0x66)]
    [InlineData("10", 131, 2, 0xfd)]
    public void UntakenBranchReallyReadsDisplacement(string name, long time, ushort address, byte value)
    {
        var replay = FuseTests.ReplayCase(name);
        var read = new FuseTests.Event(time, "MR", address, value);
        Assert.Contains(read, replay.Events);
        Assert.DoesNotContain(read, replay.Expected.Events);
        Assert.Equal(replay.Expected.Time, replay.Cpu.TStates);
        if (name != "10") { Assert.Equal(7L, replay.Cpu.TStates); Assert.Equal((ushort)2, replay.Cpu.R.PC); }
    }

    [Z80AssetTheory]
    [InlineData("edb2_1", 0x00)]
    [InlineData("edba_1", 0x00)]
    [InlineData("edbb_1", 0x03)]
    public void ReviewedRepeatIoCasesUseModernTakenRepeatFormula(string name, byte flags)
    {
        var replay = FuseTests.ReplayCase(name);
        Assert.Equal(flags, replay.Cpu.R.F);
        Assert.Equal((ushort)1, replay.Cpu.R.WZ); // pcED 0000 + 1
        Assert.Throws<TrueException>(() => FuseTests.AssertRaw(replay));
    }

    [Fact]
    public void HaltKeepsNextPcAcrossWrapAndInterrupt()
    {
        var bus = new TraceBus(); bus.Memory[0xffff] = 0x76;
        var cpu = new Z80(bus); cpu.R.PC = 0xffff; cpu.R.SP = 0x8000; cpu.R.IM = 1;
        cpu.R.IFF1 = true;
        Assert.Equal(4, cpu.Step()); Assert.Equal((ushort)0, cpu.R.PC); Assert.True(cpu.Halted);
        Assert.Equal(4, cpu.Step()); Assert.Equal((ushort)0, cpu.R.PC);
        cpu.IntLine = true;
        Assert.Equal(13, cpu.Step()); Assert.False(cpu.Halted);
        Assert.Equal((byte)0, bus.Memory[0x7ffe]); Assert.Equal((byte)0, bus.Memory[0x7fff]);
    }

    [Z80AssetFact]
    public void ExtraOrdinaryEventOrMemoryCorruptionRemainsADefectEvenInKnownConflict()
    {
        var replay = FuseTests.ReplayCase("20_2");
        var original = Differences(replay);
        replay.Events.Add(new FuseTests.Event(7, "MR", 0x1234, 0));
        replay.Memory[0x6000] ^= 0xff;
        var mutated = Differences(replay);
        Assert.Equal(original.Count + 2, mutated.Count);
        Assert.Contains(mutated, d => d.Subject == "RAM[6000]");
        Assert.Contains(mutated, d => d.Subject == "event" && d.Observed.Contains("1234", StringComparison.Ordinal));
        Assert.ThrowsAny<XunitException>(() => FuseTests.AssertRaw(replay));
    }

    internal sealed record Difference(string Kind, string Subject, string Legacy, string Observed);
    internal static List<Difference> Differences(FuseTests.Replay replay)
    {
        var list = new List<Difference>();
        var expected = replay.Expected.Registers;
        foreach (var field in typeof(Registers).GetFields())
        {
            if (field.Name == nameof(Registers.Q)) continue; // not represented by Fuse
            var a = field.GetValue(replay.Cpu.R)!; var b = field.GetValue(expected)!;
            if (a.Equals(b)) continue;
            if (field.Name == nameof(Registers.AF))
            {
                if (replay.Cpu.R.A != expected.A) list.Add(new("state", "A", expected.A.ToString("X2"), replay.Cpu.R.A.ToString("X2")));
                if (replay.Cpu.R.F != expected.F) list.Add(new("state", "F", expected.F.ToString("X2"), replay.Cpu.R.F.ToString("X2")));
            }
            else list.Add(new("state", field.Name, Format(b), Format(a)));
        }
        if (replay.Cpu.Halted != replay.Expected.Halted) list.Add(new("state", "Halted", replay.Expected.Halted.ToString(), replay.Cpu.Halted.ToString()));
        if (replay.Cpu.TStates != replay.Expected.Time) list.Add(new("timing", "TStates", replay.Expected.Time.ToString(CultureInfo.InvariantCulture), replay.Cpu.TStates.ToString(CultureInfo.InvariantCulture)));
        for (var i = 0; i < 65536; i++)
            if (replay.Memory[i] != replay.ExpectedMemory[i]) list.Add(new("memory", $"RAM[{i:X4}]", replay.ExpectedMemory[i].ToString("X2"), replay.Memory[i].ToString("X2")));
        // Longest common subsequence preserves timestamps, types, addresses, data and order.
        var old = replay.Expected.Events; var current = replay.Events;
        var lcs = new int[old.Count + 1, current.Count + 1];
        for (var i = old.Count - 1; i >= 0; i--)
        for (var j = current.Count - 1; j >= 0; j--)
            lcs[i, j] = old[i] == current[j] ? 1 + lcs[i + 1, j + 1] : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var x = 0; var y = 0;
        while (x < old.Count || y < current.Count)
        {
            if (x < old.Count && y < current.Count && old[x] == current[y]) { x++; y++; }
            else if (y < current.Count && (x == old.Count || lcs[x, y + 1] >= lcs[x + 1, y]))
                list.Add(new("extra-event", "event", "absent", EventText(current[y++])));
            else list.Add(new("missing-event", "event", EventText(old[x++]), "absent"));
        }
        return list;
    }
    private static string Format(object v) => v is bool ? v.ToString()! : Convert.ToInt32(v, CultureInfo.InvariantCulture).ToString("X4");
    private static string EventText(FuseTests.Event e) => $"{e.Time} {e.Kind} {e.Address:X4}" + (e.Data is { } d ? $" {d:X2}" : "");
    private sealed class TraceBus : IBus
    {
        public readonly byte[] Memory = new byte[65536];
        public byte Read(ushort addr) => Memory[addr];
        public void Write(ushort addr, byte value) => Memory[addr] = value;
        public byte In(ushort port) => 0;
        public void Out(ushort port, byte value) { }
        public void Contend(ushort addr, int tstates) { }
        public byte IntVector() => 0xff;
    }
}
