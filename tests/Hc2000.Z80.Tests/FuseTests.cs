using System.Globalization;
using Xunit;

namespace Hc2000.Z80.Tests;

public class FuseTests
{
    // Immutable fixture snapshots only; each test still owns a fresh CPU and bus.
    private static readonly Lazy<Dictionary<string, Case>> Inputs = new(() => Parse(Path.Combine(Z80AssetFactAttribute.AssetRoot()!, "fuse", "tests.in"), false).ToDictionary(c => c.Name));
    private static readonly Lazy<Dictionary<string, Case>> Expected = new(() => Parse(Path.Combine(Z80AssetFactAttribute.AssetRoot()!, "fuse", "tests.expected"), true).ToDictionary(c => c.Name));
    public static IEnumerable<object[]> Cases()
    {
        var root = Z80AssetFactAttribute.AssetRoot();
        var path = root == null ? null : Path.Combine(root, "fuse", "tests.in");
        if (path == null || !File.Exists(path)) { yield return ["missing"]; yield break; }
        foreach (var entry in Inputs.Value.Values) yield return [entry.Name];
    }

    [Z80AssetTheory]
    [MemberData(nameof(Cases))]
    public void PinnedRegistersMemoryTimingAndBusEvents(string name) => AssertRaw(ReplayCase(name));

    internal sealed record Replay(string Name, Case Expected, Z80 Cpu, byte[] Memory, byte[] ExpectedMemory, List<Event> Events);
    internal static Replay ReplayCase(string name)
    {
        var initial = Inputs.Value[name];
        var expected = Expected.Value[name];
        var bus = new EventBus(expected.Events);
        foreach (var cell in initial.Memory) bus.Memory[cell.Key] = cell.Value;
        var expectedMemory = (byte[])bus.Memory.Clone();
        foreach (var cell in expected.Memory) expectedMemory[cell.Key] = cell.Value;
        var cpu = new Z80(bus) { R = initial.Registers, Halted = initial.Halted };
        bus.Cpu = cpu;
        while (cpu.TStates < initial.Time) cpu.Step();
        bus.Finish();
        return new Replay(name, expected, cpu, bus.Memory, expectedMemory, bus.Events);
    }
    internal static void AssertRaw(Replay replay)
    {
        var cpu = replay.Cpu;
        var expected = replay.Expected;
        var expectedRegisters = expected.Registers;
        expectedRegisters.Q = cpu.R.Q; // Fuse format has no Q field; SingleStep checks it independently.
        Assert.True(cpu.R.Equals(expectedRegisters), $"{replay.Name}: actual {State(cpu.R)}; expected {State(expectedRegisters)}");
        Assert.Equal(expected.Halted, cpu.Halted);
        Assert.Equal(expected.Time, cpu.TStates);
        Assert.Equal(replay.ExpectedMemory, replay.Memory);
        Assert.Equal(expected.Events, replay.Events);
    }

    private static string State(Registers r) => $"AF {r.AF:X4} BC {r.BC:X4} DE {r.DE:X4} HL {r.HL:X4} AF' {r.AFShadow:X4} BC' {r.BCShadow:X4} DE' {r.DEShadow:X4} HL' {r.HLShadow:X4} IX {r.IX:X4} IY {r.IY:X4} SP {r.SP:X4} PC {r.PC:X4} WZ {r.WZ:X4} I {r.I:X2} R {r.R:X2} IFF1 {r.IFF1} IFF2 {r.IFF2} IM {r.IM}";
    internal sealed record Case(string Name, Registers Registers, bool Halted, long Time, Dictionary<ushort, byte> Memory, List<Event> Events);
    internal readonly record struct Event(long Time, string Kind, ushort Address, byte? Data);
    private static string[] Tokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private static int Hex(string s) => int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static IEnumerable<Case> Parse(string path, bool expected)
    {
        var lines = File.ReadAllLines(path);
        var i = 0;
        while (i < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) { i++; continue; }
            var name = lines[i++].Trim();
            var events = new List<Event>();
            if (expected)
                while (i < lines.Length)
                {
                    var t = Tokens(lines[i]);
                    if (t.Length < 3 || t[1] is not ("MR" or "MW" or "MC" or "PR" or "PW" or "PC")) break;
                    events.Add(new Event(long.Parse(t[0], CultureInfo.InvariantCulture), t[1], (ushort)Hex(t[2]), t.Length > 3 ? (byte)Hex(t[3]) : null));
                    i++;
                }
            var pairs = Tokens(lines[i++]).Select(Hex).ToArray();
            var state = Tokens(lines[i++]);
            var r = new Registers
            {
                AF = (ushort)pairs[0], BC = (ushort)pairs[1], DE = (ushort)pairs[2], HL = (ushort)pairs[3],
                AFShadow = (ushort)pairs[4], BCShadow = (ushort)pairs[5], DEShadow = (ushort)pairs[6], HLShadow = (ushort)pairs[7],
                IX = (ushort)pairs[8], IY = (ushort)pairs[9], SP = (ushort)pairs[10], PC = (ushort)pairs[11], WZ = (ushort)pairs[12],
                I = (byte)Hex(state[0]), R = (byte)Hex(state[1]), IFF1 = state[2] != "0", IFF2 = state[3] != "0", IM = (byte)Hex(state[4])
            };
            var memory = new Dictionary<ushort, byte>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                var t = Tokens(lines[i++]);
                if (t[0] == "-1") break;
                var address = (ushort)Hex(t[0]);
                for (var j = 1; j < t.Length && t[j] != "-1"; j++)
                { memory[address] = (byte)Hex(t[j]); address = unchecked((ushort)(address + 1)); }
            }
            yield return new Case(name, r, state[5] != "0", long.Parse(state[6], CultureInfo.InvariantCulture), memory, events);
        }
    }

    /// <summary>
    /// Translate the core's generic contention hooks into Fuse's MC/PC trace
    /// convention. Memory accesses have one contention event; idle cycles have
    /// one per T-state. PC models the fixture runner's Spectrum port arbitration,
    /// not HC2000 contention. This adapter never adds wait states to the CPU.
    /// </summary>
    private sealed class EventBus(List<Event> expected) : IBus
    {
        public readonly byte[] Memory = new byte[65536];
        public readonly List<Event> Events = [];
        public Z80 Cpu = null!;
        private readonly Queue<Event> input = new(expected.Where(e => e.Kind == "PR"));
        private (long Time, ushort Address, int Count, bool IoTail)? pending;
        private bool nextIoTail;
        private static bool HighContended(ushort port) => (port & 0xc000) == 0x4000;
        public void Contend(ushort addr, int tstates)
        {
            Finish();
            pending = (Cpu.TStates, addr, tstates, nextIoTail);
            nextIoTail = false;
        }
        public void Finish()
        {
            if (pending is not { } p) return;
            if (p.IoTail)
            {
                if ((p.Address & 1) == 0) Events.Add(new Event(p.Time, "PC", p.Address, null));
                else if (HighContended(p.Address))
                    for (var t = 0; t < p.Count; t++) Events.Add(new Event(p.Time + t, "PC", p.Address, null));
            }
            else for (var t = 0; t < p.Count; t++) Events.Add(new Event(p.Time + t, "MC", p.Address, null));
            pending = null;
        }
        private void MemoryEvent(string kind, ushort address, byte data)
        {
            if (pending is { } p) Events.Add(new Event(p.Time, "MC", p.Address, null));
            pending = null;
            Events.Add(new Event(Cpu.TStates, kind, address, data));
        }
        public byte Read(ushort addr) { var value = Memory[addr]; MemoryEvent("MR", addr, value); return value; }
        public void Write(ushort addr, byte value) { MemoryEvent("MW", addr, value); Memory[addr] = value; }
        private void PortEvent(string kind, ushort address, byte data)
        {
            if (pending is { } p && HighContended(address)) Events.Add(new Event(p.Time, "PC", address, null));
            pending = null;
            Events.Add(new Event(Cpu.TStates, kind, address, data));
            nextIoTail = true;
        }
        public byte In(ushort port)
        {
            var value = input.Count != 0 ? input.Dequeue().Data!.Value : (byte)(port >> 8);
            PortEvent("PR", port, value);
            return value;
        }
        public void Out(ushort port, byte value) => PortEvent("PW", port, value);
        public byte IntVector() => throw new InvalidOperationException("Fuse fixtures do not accept external interrupts.");
    }
}
