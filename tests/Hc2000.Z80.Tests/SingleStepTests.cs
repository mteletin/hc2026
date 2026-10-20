using System.Text.Json;
using Xunit;

namespace Hc2000.Z80.Tests;

public class SingleStepTests
{
    public static IEnumerable<object[]> Files()
    {
        var root = Z80AssetFactAttribute.AssetRoot();
        var directory = root == null ? null : Path.Combine(root, "singlestep", "v1");
        if (directory == null || !Directory.Exists(directory)) { yield return ["missing"]; yield break; }
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
            yield return [Path.GetFileName(path)];
    }

    [Z80AssetTheory]
    [MemberData(nameof(Files))]
    public void PinnedNmosVectors(string filename)
    {
        var root = Z80AssetFactAttribute.AssetRoot();
        Assert.NotNull(root);
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "singlestep", "v1", filename)));
        Assert.Equal(1000, document.RootElement.GetArrayLength());
        var bus = new VectorBus();
        var cpu = new Z80(bus);
        var failures = 0;
        string? first = null;
        foreach (var vector in document.RootElement.EnumerateArray())
        {
            cpu.Reset(); bus.Reset();
            var initial = vector.GetProperty("initial");
            cpu.R = RegistersFrom(initial);
            cpu.EiPending = initial.GetProperty("ei").GetInt32() != 0;
            cpu.LdAirPending = initial.GetProperty("p").GetInt32() != 0;
            foreach (var cell in initial.GetProperty("ram").EnumerateArray())
                bus.Memory[cell[0].GetInt32()] = (byte)cell[1].GetInt32();
            if (vector.TryGetProperty("ports", out var ports))
                foreach (var port in ports.EnumerateArray())
                    bus.ExpectedPorts.Enqueue(new Port((ushort)port[0].GetInt32(), (byte)port[1].GetInt32(), port[2].GetString()!));
            string? error = null;
            try
            {
                var elapsed = cpu.Step();
                var final = vector.GetProperty("final");
                var expected = RegistersFrom(final);
                if (!cpu.R.Equals(expected))
                    error = RegisterDifference(cpu.R, expected);
                else if (cpu.EiPending != (final.GetProperty("ei").GetInt32() != 0)) error = "EI pending mismatch";
                else if (cpu.LdAirPending != (final.GetProperty("p").GetInt32() != 0)) error = "LD A,I/R pending mismatch";
                else if (elapsed != vector.GetProperty("cycles").GetArrayLength()) error = $"cycles {elapsed}, expected {vector.GetProperty("cycles").GetArrayLength()}";
                else if (bus.PortError != null || bus.ExpectedPorts.Count != 0) error = bus.PortError ?? "Missing port transaction";
                else
                    foreach (var cell in final.GetProperty("ram").EnumerateArray())
                        if (bus.Memory[cell[0].GetInt32()] != (byte)cell[1].GetInt32()) { error = $"RAM[{cell[0]}] mismatch"; break; }
            }
            catch (Exception exception) { error = exception.ToString(); }
            if (error != null)
            {
                failures++;
                first ??= $"{vector.GetProperty("name").GetString()}: {error}";
            }
        }
        Assert.True(failures == 0, $"{filename}: {failures}/1000 failing vectors; first: {first}");
    }

    private static Registers RegistersFrom(JsonElement state)
    {
        int V(string key) => state.GetProperty(key).GetInt32();
        return new Registers
        {
            AF = (ushort)((V("a") << 8) | V("f")), BC = (ushort)((V("b") << 8) | V("c")),
            DE = (ushort)((V("d") << 8) | V("e")), HL = (ushort)((V("h") << 8) | V("l")),
            AFShadow = (ushort)V("af_"), BCShadow = (ushort)V("bc_"), DEShadow = (ushort)V("de_"), HLShadow = (ushort)V("hl_"),
            IX = (ushort)V("ix"), IY = (ushort)V("iy"), SP = (ushort)V("sp"), PC = (ushort)V("pc"),
            I = (byte)V("i"), R = (byte)V("r"), IM = (byte)V("im"), WZ = (ushort)V("wz"), Q = (byte)V("q"),
            IFF1 = V("iff1") != 0, IFF2 = V("iff2") != 0
        };
    }

    private static string RegisterDifference(Registers a, Registers b)
    {
        var actual = new int[] { a.AF, a.BC, a.DE, a.HL, a.AFShadow, a.BCShadow, a.DEShadow, a.HLShadow, a.IX, a.IY, a.SP, a.PC, a.I, a.R, a.IM, a.WZ, a.Q, a.IFF1 ? 1 : 0, a.IFF2 ? 1 : 0 };
        var expected = new int[] { b.AF, b.BC, b.DE, b.HL, b.AFShadow, b.BCShadow, b.DEShadow, b.HLShadow, b.IX, b.IY, b.SP, b.PC, b.I, b.R, b.IM, b.WZ, b.Q, b.IFF1 ? 1 : 0, b.IFF2 ? 1 : 0 };
        var names = new[] { "AF", "BC", "DE", "HL", "AF'", "BC'", "DE'", "HL'", "IX", "IY", "SP", "PC", "I", "R", "IM", "WZ", "Q", "IFF1", "IFF2" };
        return string.Join("; ", Enumerable.Range(0, names.Length).Where(i => actual[i] != expected[i]).Select(i => $"{names[i]}={actual[i]:X4} expected {expected[i]:X4}"));
    }

    private readonly record struct Port(ushort Address, byte Value, string Direction);
    private sealed class VectorBus : IBus
    {
        public readonly byte[] Memory = new byte[65536];
        public readonly Queue<Port> ExpectedPorts = [];
        public string? PortError;
        public void Reset() { Array.Clear(Memory); ExpectedPorts.Clear(); PortError = null; }
        private byte Transaction(ushort address, byte value, string direction)
        {
            if (!ExpectedPorts.TryDequeue(out var expected)) { PortError = "Unexpected port transaction"; return 0; }
            if (expected.Address != address || expected.Direction != direction || (direction == "w" && expected.Value != value))
                PortError = $"port {direction} {address:X4}:{value:X2}, expected {expected}";
            return expected.Value;
        }
        public byte Read(ushort addr) => Memory[addr];
        public void Write(ushort addr, byte value) => Memory[addr] = value;
        public byte In(ushort port) => Transaction(port, 0, "r");
        public void Out(ushort port, byte value) => Transaction(port, value, "w");
        public void Contend(ushort addr, int tstates) { }
        public byte IntVector() => throw new InvalidOperationException("Single-step corpus does not accept interrupts.");
    }
}
