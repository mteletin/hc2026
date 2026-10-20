using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Hc2000.Z80;

if (args.Length != 1) throw new ArgumentException("Pass the preserved pre-specialization Hc2000.Z80.dll path.");
var path = Path.GetFullPath(args[0]);
Console.WriteLine($"Reference DLL SHA256={Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))}");
var context = new AssemblyLoadContext("isolated-pre-specialization", isCollectible: false);
var assembly = context.LoadFromAssemblyPath(path);
var cpuType = assembly.GetType("Hc2000.Z80.Z80", throwOnError: true)!;
var interfaceType = assembly.GetType("Hc2000.Z80.IBus", throwOnError: true)!;
var registerField = cpuType.GetField("R")!;
var stepMethod = cpuType.GetMethod("Step")!;
var resetMethod = cpuType.GetMethod("Reset")!;
var nmiMethod = cpuType.GetMethod("Nmi")!;
var entries = new Opcodes().Entries.Where(e => e.DecoderKey.PrefixFamily == PrefixFamily.Base).ToArray();
var scenarios = 0;
var comparedSteps = 0;
foreach (var entry in entries)
for (var seed = 0; seed < 32; seed++)
{
    var random = new Random((entry.DecoderKey.Opcode << 8) | seed);
    var currentBus = new TraceBus(); random.NextBytes(currentBus.Memory);
    var pc = seed % 4 == 0 ? (ushort)0xffff : seed % 4 == 1 ? (ushort)0xfffe : (ushort)0x4000;
    for (var i = 0; i < entry.Length; i++) currentBus.Memory[unchecked((ushort)(pc + i))] = entry.Encoding[i];
    foreach (var slot in entry.Operands)
    {
        currentBus.Memory[unchecked((ushort)(pc + slot.ByteOffset))] = (byte)random.Next(256);
        if (slot.Kind == OperandKind.WordLE)
            currentBus.Memory[unchecked((ushort)(pc + slot.ByteOffset + 1))] = (byte)random.Next(256);
    }
    var referenceBus = new TraceBus(); currentBus.Memory.CopyTo(referenceBus.Memory, 0);
    var cpu = new Z80(currentBus);
    var proxy = DispatchProxy.Create(interfaceType, typeof(ReferenceBusProxy));
    ((ReferenceBusProxy)proxy).Bus = referenceBus;
    var reference = Activator.CreateInstance(cpuType, proxy)!;
    var registers = new Registers
    {
        AF = (ushort)((random.Next(256) << 8) | ((seed * 37) & 255)),
        BC = (ushort)random.Next(65536), DE = (ushort)random.Next(65536), HL = (ushort)random.Next(65536),
        IX = (ushort)random.Next(65536), IY = (ushort)random.Next(65536),
        SP = seed % 4 == 0 ? (ushort)0 : seed % 4 == 1 ? (ushort)0xffff : (ushort)random.Next(65536), PC = pc,
        AFShadow = (ushort)random.Next(65536), BCShadow = (ushort)random.Next(65536),
        DEShadow = (ushort)random.Next(65536), HLShadow = (ushort)random.Next(65536),
        I = (byte)random.Next(256), R = (byte)random.Next(256), IM = (byte)(seed % 3),
        IFF1 = (seed & 1) != 0, IFF2 = (seed & 2) != 0, WZ = (ushort)random.Next(65536), Q = (byte)random.Next(256)
    };
    cpu.R = registers;
    var oldRegisters = Activator.CreateInstance(registerField.FieldType)!;
    foreach (var field in typeof(Registers).GetFields())
        registerField.FieldType.GetField(field.Name)!.SetValue(oldRegisters, field.GetValue(registers));
    registerField.SetValue(reference, oldRegisters);
    cpu.TStates = seed * 100;
    cpuType.GetField("TStates")!.SetValue(reference, cpu.TStates);
    currentBus.Time = () => cpu.TStates;
    referenceBus.Time = () => (long)cpuType.GetField("TStates")!.GetValue(reference)!;
    // A pending edge raised inside a memory callback is accepted only at the next boundary.
    if (seed % 7 == 0)
    {
        currentBus.Edge = cpu.Nmi;
        referenceBus.Edge = () => nmiMethod.Invoke(reference, null);
    }
    for (var step = 0; step < 4; step++)
    {
        if (step == 1 && seed % 2 == 0)
        { cpu.IntLine = true; cpuType.GetField("IntLine")!.SetValue(reference, true); }
        if (step == 2 && seed % 3 == 0)
        { cpu.Nmi(); nmiMethod.Invoke(reference, null); }
        if (step == 3 && seed % 5 == 0)
        { cpu.Reset(); resetMethod.Invoke(reference, null); }
        var actual = cpu.Step(); var expected = (int)stepMethod.Invoke(reference, null)!;
        var label = $"opcode={entry.DecoderKey.Opcode:X2} seed={seed} step={step}";
        Check(actual == expected, label + " duration");
        var snapshot = registerField.GetValue(reference)!;
        foreach (var field in typeof(Registers).GetFields())
            Check(Equals(field.GetValue(cpu.R), registerField.FieldType.GetField(field.Name)!.GetValue(snapshot)), label + " " + field.Name);
        Check(cpu.TStates == (long)cpuType.GetField("TStates")!.GetValue(reference)!, label + " TStates");
        Check(cpu.Halted == (bool)cpuType.GetField("Halted")!.GetValue(reference)!, label + " HALT");
        Check(cpu.IntLine == (bool)cpuType.GetField("IntLine")!.GetValue(reference)!, label + " INT");
        Check(currentBus.Memory.AsSpan().SequenceEqual(referenceBus.Memory), label + " memory");
        Check(currentBus.Events.SequenceEqual(referenceBus.Events), label + " bus events/timestamps");
        comparedSteps++;
    }
    scenarios++;
}
var exceptionCases = 0;
foreach (var operation in new[] { "Contend", "Read", "Write", "In", "Out", "IntVector", "PrefixRead", "PrefixContend", "PushHigh", "PushLow", "PopLow", "PopHigh" })
{
    var prefix = operation.StartsWith("Prefix", StringComparison.Ordinal);
    var push = operation.StartsWith("Push", StringComparison.Ordinal);
    var pop = operation.StartsWith("Pop", StringComparison.Ordinal);
    var fault = prefix ? operation[6..] : push ? "Write" : pop ? "Read" : operation;
    var ordinal = prefix ? 3 : operation == "PushLow" || operation == "PopLow" ? 2 : operation == "PopHigh" ? 3 : 1;
    var currentBus = new TraceBus { ThrowOperation = fault, ThrowAfter = ordinal };
    var referenceBus = new TraceBus { ThrowOperation = fault, ThrowAfter = ordinal };
    currentBus.Memory[0] = push ? (byte)0xe5 : pop ? (byte)0xe1 : fault switch { "Write" => (byte)0x77, "In" => (byte)0xdb, "Out" => (byte)0xd3, _ => (byte)0 };
    if (prefix) { currentBus.Memory[0] = 0xdd; currentBus.Memory[1] = 0xfd; currentBus.Memory[2] = 0xed; }
    currentBus.Memory.CopyTo(referenceBus.Memory, 0);
    var cpu = new Z80(currentBus); cpu.R.HL = 0x3000;
    var proxy = DispatchProxy.Create(interfaceType, typeof(ReferenceBusProxy));
    ((ReferenceBusProxy)proxy).Bus = referenceBus;
    var reference = Activator.CreateInstance(cpuType, proxy)!;
    if (fault == "IntVector") { cpu.R.IM = 1; cpu.R.IFF1 = true; cpu.IntLine = true; }
    var oldRegisters = Activator.CreateInstance(registerField.FieldType)!;
    foreach (var field in typeof(Registers).GetFields())
        registerField.FieldType.GetField(field.Name)!.SetValue(oldRegisters, field.GetValue(cpu.R));
    registerField.SetValue(reference, oldRegisters);
    cpuType.GetField("IntLine")!.SetValue(reference, cpu.IntLine);
    currentBus.Time = () => cpu.TStates;
    referenceBus.Time = () => (long)cpuType.GetField("TStates")!.GetValue(reference)!;
    var actualException = Capture(() => cpu.Step());
    var expectedException = Capture(() => stepMethod.Invoke(reference, null));
    Check(ReferenceEquals(actualException, currentBus.Failure), operation + " original exception identity");
    Check(ReferenceEquals(expectedException, referenceBus.Failure), operation + " reference exception identity");
    Check(actualException!.GetType() == expectedException!.GetType() && actualException.Message == expectedException.Message, operation + " exception type/message");
    var snapshot = registerField.GetValue(reference)!;
    foreach (var field in typeof(Registers).GetFields())
        Check(Equals(field.GetValue(cpu.R), registerField.FieldType.GetField(field.Name)!.GetValue(snapshot)), operation + " " + field.Name);
    foreach (var name in new[] { "TStates", "Halted", "IntLine", "nmiPending", "eiDelay", "lastLdAiOrR" })
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Check(Equals(typeof(Z80).GetField(name, flags)!.GetValue(cpu), cpuType.GetField(name, flags)!.GetValue(reference)), operation + " " + name);
    }
    Check(currentBus.Memory.AsSpan().SequenceEqual(referenceBus.Memory), operation + " memory at exception");
    Check(currentBus.Events.SequenceEqual(referenceBus.Events), operation + " complete event/time trace at exception");
    exceptionCases++;
}
Console.WriteLine($"PASS: {entries.Length} Base entries, {scenarios} varied scenarios, {comparedSteps} sequential Step/state/memory/event comparisons; {exceptionCases} complete exceptional-boundary comparisons; default callbacks.");
// The CLI exits now; no reference implementation remains in a product assembly.

static Exception? Capture(Action action)
{
    try { action(); return null; }
    catch (TargetInvocationException e) when (e.InnerException is not null) { return e.InnerException; }
    catch (Exception e) { return e; }
}

static void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("Execution parity failed: " + label);
}

public class ReferenceBusProxy : DispatchProxy
{
    public TraceBus Bus { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        var args = arguments ?? [];
        return method!.Name switch
        {
            "Read" => Bus.Read((ushort)args[0]!), "In" => Bus.In((ushort)args[0]!), "IntVector" => Bus.IntVector(),
            "Write" => Write((ushort)args[0]!, (byte)args[1]!),
            "Out" => Out((ushort)args[0]!, (byte)args[1]!),
            "Contend" => Contend((ushort)args[0]!, (int)args[1]!),
            _ => throw new InvalidOperationException(method.Name)
        };
    }
    private object? Write(ushort a, byte v) { Bus.Write(a, v); return null; }
    private object? Out(ushort p, byte v) { Bus.Out(p, v); return null; }
    private object? Contend(ushort a, int t) { Bus.Contend(a, t); return null; }
}

public sealed class TraceBus : IBus
{
    public byte[] Memory { get; } = new byte[65536];
    public List<string> Events { get; } = [];
    public Func<long> Time { get; set; } = () => 0;
    public Action? Edge { get; set; }
    public string? ThrowOperation { get; set; }
    public int ThrowAfter { get; set; } = 1;
    public Exception Failure { get; } = new InvalidOperationException("bus exception sentinel");
    private int faultCalls;
    private void Fault(string operation)
    {
        if (operation == ThrowOperation && ++faultCalls == ThrowAfter) throw Failure;
    }
    public byte Read(ushort a)
    {
        var value = Memory[a]; Events.Add($"MR {a:X4} {value:X2} @{Time()}"); Fault("Read");
        var edge = Edge; Edge = null; edge?.Invoke(); return value;
    }
    public void Write(ushort a, byte v) { Events.Add($"MW {a:X4} {v:X2} @{Time()}"); Fault("Write"); Memory[a] = v; }
    public byte In(ushort p) { var v = (byte)(p ^ 0x5a); Events.Add($"PR {p:X4} {v:X2} @{Time()}"); Fault("In"); return v; }
    public void Out(ushort p, byte v) { Events.Add($"PW {p:X4} {v:X2} @{Time()}"); Fault("Out"); }
    public void Contend(ushort a, int t) { Events.Add($"CT {a:X4} {t} @{Time()}"); Fault("Contend"); }
    public byte IntVector() { Events.Add($"ACK FF @{Time()}"); Fault("IntVector"); return 0xff; }
}
