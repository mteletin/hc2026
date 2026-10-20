using Hc2000.Z80.Frontier;
using Xunit;

namespace Hc2000.Z80.Tests;

public sealed partial class FrontierTests
{
    private sealed class ForbiddenBus : IBus
    {
        public byte Read(ushort addr) => throw new InvalidOperationException("Frontier CPU accessed its bus.");
        public void Write(ushort addr, byte value) => throw new InvalidOperationException("Frontier CPU accessed its bus.");
        public byte In(ushort port) => throw new InvalidOperationException("Frontier CPU accessed its bus.");
        public void Out(ushort port, byte value) => throw new InvalidOperationException("Frontier CPU accessed its bus.");
        public void Contend(ushort addr, int tstates) => throw new InvalidOperationException("Frontier CPU accessed its bus.");
        public byte IntVector() => throw new InvalidOperationException("Frontier CPU accessed its bus.");
    }

    private readonly record struct Access(long At, FrontierEffectKind Kind, ushort Address, byte Value, bool Start = false);
    private sealed class TraceBus(byte[] memory) : IBus, IM1FetchBus
    {
        public Z80 Cpu = null!;
        public readonly List<Access> Trace = [];
        public byte Read(ushort address) => ReadAt(Cpu.TStates, FrontierEffectKind.MemoryRead, address);
        public void Write(ushort address, byte value) => WriteAt(Cpu.TStates, FrontierEffectKind.MemoryWrite, address, value);
        public byte In(ushort port) => ReadAt(Cpu.TStates, FrontierEffectKind.PortRead, port);
        public void Out(ushort port, byte value) => WriteAt(Cpu.TStates, FrontierEffectKind.PortWrite, port, value);
        public void Contend(ushort address, int tstates) { }
        public byte IntVector() => ReadAt(Cpu.TStates, FrontierEffectKind.InterruptAck, 0);
        public byte FetchM1(ushort pc, bool instructionStart, long absoluteCpuTStates) =>
            ReadAt(absoluteCpuTStates, FrontierEffectKind.M1Read, pc, instructionStart);
        private byte ReadAt(long at, FrontierEffectKind kind, ushort address, bool start = false)
        {
            var value = kind == FrontierEffectKind.PortRead ? (byte)(address ^ (address >> 8) ^ 0xa5) :
                kind == FrontierEffectKind.InterruptAck ? (byte)0xff : memory[address];
            Trace.Add(new(at, kind, address, value, start));
            return value;
        }
        private void WriteAt(long at, FrontierEffectKind kind, ushort address, byte value)
        {
            Trace.Add(new(at, kind, address, value));
            if (kind == FrontierEffectKind.MemoryWrite) memory[address] = value;
        }
        public byte Apply(FrontierEffect effect)
        {
            if (effect.Kind == FrontierEffectKind.Internal) return 0;
            if (effect.Kind is FrontierEffectKind.MemoryWrite or FrontierEffectKind.PortWrite)
            {
                WriteAt(effect.AtCpuTState, effect.Kind, effect.Address, effect.WriteValue);
                return 0;
            }
            return ReadAt(effect.AtCpuTState, effect.Kind, effect.Address, effect.InstructionStart);
        }
    }

    private static void ExecuteOne(Z80 cpu, TraceBus owner, bool intLine = false)
    {
        var retired = false;
        var last = cpu.TStates;
        for (var count = 0; !retired; count++)
        {
            Assert.True(count < 128, "Finite instruction did not retire.");
            var next = cpu.PeekNextFrontier();
            Assert.Equal(next, cpu.PeekNextFrontier()); // observation cannot execute an effect
            Assert.True(next.AtCpuTState > last);
            // Exercise the detached continuation at every interrupt/HALT/normal cycle seam too.
            cpu.RestoreFrontier(cpu.CaptureFrontier());
            Assert.Equal(next, cpu.PeekNextFrontier());
            retired = cpu.CommitFrontier(owner.Apply(next), intLine).Retired;
            last = next.AtCpuTState;
        }
        Assert.True(cpu.AtInstructionBoundary);
    }

    [Theory]
    [InlineData(0)] [InlineData(0xcb)] [InlineData(0xed)] [InlineData(0xdd)]
    [InlineData(0xfd)] [InlineData(0xddcb)] [InlineData(0xfdcb)]
    public void EveryOpcodeFamilyMatchesStepRegistersMemoryAndOrderedBusEffects(int family)
    {
        foreach (var seed in new[] { 0x1234, 0xc781 })
        for (var opcode = 0; opcode < 256; opcode++)
        {
            var memory = new byte[65536];
            for (var index = 0; index < memory.Length; index++) memory[index] = (byte)(index * 37 + seed);
            var program = family switch
            {
                0 => new byte[] { (byte)opcode, 0x34, 0x12, 0 },
                0xddcb or 0xfdcb => new byte[] { (byte)(family >> 8), 0xcb, 0xfd, (byte)opcode, 0 },
                _ => new byte[] { (byte)family, (byte)opcode, 0x34, 0x12, 0 }
            };
            program.CopyTo(memory, 0);
            var referenceMemory = memory.ToArray();
            var referenceBus = new TraceBus(referenceMemory);
            var reference = new Z80(referenceBus);
            referenceBus.Cpu = reference;
            var actual = new Z80(new ForbiddenBus());
            var owner = new TraceBus(memory);
            actual.R = reference.R = new Registers
            {
                AF = (ushort)seed, BC = 0x0217, DE = 0x9876, HL = 0x5432, IX = 0x4567, IY = 0x6789,
                SP = 0xff00, AFShadow = 0x5678, BCShadow = 0x9abc, DEShadow = 0xdef0, HLShadow = 0x1357,
                I = 0x42, R = 0xfe, Q = (byte)seed, WZ = 0x9988, IFF1 = true, IFF2 = true, IM = 1
            };
            reference.Step();
            ExecuteOne(actual, owner);
            Assert.True(reference.R.Equals(actual.R), $"Registers differ: family {family:X4}, opcode {opcode:X2}, seed {seed:X4}.");
            Assert.Equal(reference.TStates, actual.TStates);
            Assert.Equal(reference.Halted, actual.Halted);
            Assert.Equal(reference.LdAirPending, actual.LdAirPending);
            Assert.Equal(reference.EiDelayRaw, actual.EiDelayRaw);
            Assert.Equal(referenceBus.Trace, owner.Trace);
            Assert.Equal(referenceMemory, memory);
        }
    }

    [Fact]
    public void InfinitePrefixStreamYieldsWithoutRetirementOrInventedInterruptBoundary()
    {
        var cpu = new Z80(new ForbiddenBus());
        cpu.R.IFF1 = cpu.R.IFF2 = true;
        for (var index = 0; index < 8192; index++)
        {
            var next = cpu.PeekNextFrontier();
            Assert.Equal(FrontierEffectKind.M1Read, next.Kind);
            Assert.Equal((index + 1) * 4L, next.AtCpuTState);
            Assert.Equal((ushort)index, next.Address);
            Assert.Equal(index == 0, next.InstructionStart);
            Assert.False(cpu.CommitFrontier(index % 2 == 0 ? (byte)0xdd : (byte)0xfd, intLine: true).Retired);
            Assert.False(cpu.AtInstructionBoundary);
        }
        Assert.Equal(8192 * 4, cpu.TStates);
        Assert.Equal((ushort)8192, cpu.R.PC);
        Assert.True(cpu.R.IFF1);
        var terminal = cpu.PeekNextFrontier();
        Assert.Equal(FrontierEffectKind.M1Read, terminal.Kind);
        Assert.True(cpu.CommitFrontier(0, intLine: true).Retired);
        Assert.Equal(FrontierEffectKind.InterruptAck, cpu.PeekNextFrontier().Kind);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void InterruptEiHaltAndRefreshSequenceMatchesStep(int mode)
    {
        var memory = new byte[65536];
        new byte[] { 0xfb, 0x76, 0, 0 }.CopyTo(memory, 0); // EI; HALT; held cycles; interrupt
        var referenceMemory = memory.ToArray();
        var referenceBus = new TraceBus(referenceMemory);
        var reference = new Z80(referenceBus);
        referenceBus.Cpu = reference;
        var owner = new TraceBus(memory);
        var actual = new Z80(new ForbiddenBus());
        reference.R.IM = actual.R.IM = (byte)(mode % 3);
        reference.R.I = actual.R.I = 0x80;
        for (var step = 0; step < 6; step++)
        {
            var interrupt = step >= 4;
            reference.IntLine = actual.IntLine = interrupt;
            if (interrupt && mode == 3) { reference.Nmi(); actual.Nmi(); }
            reference.Step();
            ExecuteOne(actual, owner, interrupt);
            Assert.Equal(reference.R, actual.R);
            Assert.Equal(reference.TStates, actual.TStates);
            Assert.Equal(reference.Halted, actual.Halted);
            Assert.Equal(reference.EiDelayRaw, actual.EiDelayRaw);
            Assert.Equal(reference.NmiPendingState, actual.NmiPendingState);
            Assert.Equal(referenceBus.Trace, owner.Trace);
            Assert.Equal(referenceMemory, memory);
        }
    }
}
