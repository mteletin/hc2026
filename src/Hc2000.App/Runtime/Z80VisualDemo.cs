using Hc2000.Machine;
using Hc2000.Machine.Input;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using CpuZ80 = Hc2000.Z80.Z80;
using LiveMachine = Hc2000.Machine.Machine;

namespace Hc2000.App.Runtime;

/// <summary>One bounded nonboot demonstration composed from the production CPU/machine/video seams.</summary>
public static class Z80VisualDemo
{
    public const ushort ProgramAddress = 0x8000;
    public const ushort ScreenAddress = 0x4000;
    private static readonly byte[] paintImage = LoadImage("Hc2000.App.Demos.z80-visual.bin");
    private static readonly byte[] idleImage = LoadImage("Hc2000.App.Demos.z80-visual-negative.bin");
    public static ReadOnlySpan<byte> PaintProgram => paintImage;
    // Explicit negative control uses its checked-in binary and shares the same blank RAM/output pipeline.
    public static ReadOnlySpan<byte> IdleProgram => idleImage;

    private static byte[] LoadImage(string name)
    {
        using var stream = typeof(Z80VisualDemo).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing demo program resource {name}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static LiveMachine CreateMachine(out CpuZ80 cpu, out HcMemory memory, bool paint = true)
    {
        var profile = MachineProfile.Default;
        var scheduler = new DeviceScheduler();
        var clock = new FrameClock(scheduler, 1);
        var mapper = CpuTimeMapper.FromProfile(profile);
        var adapter = new CpuDeviceTimeAdapter(scheduler, mapper);
        memory = new HcMemory(profile.Memory with { RomProvenance = "synthetic nonboot visual demo; ROM unused" },
            new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        // Explicit blank initial screen. Only guest instructions paint the test pattern.
        memory.Ram.Span.Slice(ScreenAddress, 0x1b00).Clear();
        var ula = new Ula(memory, new KeyboardMatrix(), scheduler, clock, 2, profile.Video);
        var portDecoder = new PortDecoder([]);
        var machineBus = new HcMachineBus(memory, portDecoder, adapter, scheduler);
        var timedBus = new CpuTimedBus(machineBus, adapter);
        cpu = new CpuZ80(timedBus);
        var actualCpu = cpu;
        timedBus.Attach(actualCpu);
        var image = paint ? PaintProgram : IdleProgram;
        for (var i = 0; i < image.Length; i++) memory.Write((ushort)(ProgramAddress + i), image[i]);
        actualCpu.R.PC = ProgramAddress;
        actualCpu.R.SP = 0xbfff;
        clock.InterruptChanged += (_, asserted) => actualCpu.IntLine = asserted;
        return new LiveMachine(scheduler, clock, adapter, ula, () => actualCpu.Step(), () => actualCpu.TStates);
    }

    public static EmulationWorker CreateWorker(bool paint = true) => new(
        () => CreateMachine(out _, out _, paint), _ => { });

}
