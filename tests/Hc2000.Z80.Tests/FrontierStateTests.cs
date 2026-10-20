using System.Text.Json;
using Hc2000.Z80.Frontier;
using Xunit;

namespace Hc2000.Z80.Tests;

public sealed partial class FrontierTests
{
    private static string Wire(FrontierState state) => JsonSerializer.Serialize(state, new JsonSerializerOptions { IncludeFields = true });

    [Theory]
    [InlineData(0)] [InlineData(0xcb)] [InlineData(0xed)] [InlineData(0xdd)]
    [InlineData(0xfd)] [InlineData(0xddcb)] [InlineData(0xfdcb)]
    public void EverySuspendedOpcodeRestoresWithoutReplayingAnEffect(int family)
    {
        for (var opcode = 0; opcode < 256; opcode++)
        {
            var memory = Enumerable.Range(0, 65536).Select(i => (byte)(i * 71 + 0x9b)).ToArray();
            var program = family switch
            {
                0 => new byte[] { (byte)opcode, 0x09, 0x80 },
                0xddcb or 0xfdcb => new byte[] { (byte)(family >> 8), 0xcb, 0xf9, (byte)opcode },
                _ => new byte[] { (byte)family, (byte)opcode, 0x09, 0x80 }
            };
            program.CopyTo(memory, 0);
            var referenceMemory = memory.ToArray();
            var referenceBus = new TraceBus(referenceMemory);
            var reference = new Z80(referenceBus);
            referenceBus.Cpu = reference;
            var cpu = new Z80(new ForbiddenBus());
            cpu.R = reference.R = new Registers
            {
                AF = 0xb293, BC = 0x0217, DE = 0x7890, HL = 0x4567, IX = 0x3456, IY = 0x5678,
                SP = 0xff00, I = 0x87, R = 0xfe, Q = 0x93, WZ = 0x9087, IFF1 = true, IFF2 = true, IM = 2
            };
            reference.Step();
            var owner = new TraceBus(memory);
            for (var effectCount = 0; ; effectCount++)
            {
                Assert.True(effectCount < 128);
                var effect = cpu.PeekNextFrontier();
                var captured = cpu.CaptureFrontier();
                var json = Wire(captured);
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= 4096);
                var detached = JsonSerializer.Deserialize<FrontierState>(json, new JsonSerializerOptions { IncludeFields = true })!;
                cpu = new Z80(new ForbiddenBus());
                cpu.RestoreFrontier(detached);
                Assert.Equal(effect, cpu.PeekNextFrontier());
                if (cpu.CommitFrontier(owner.Apply(effect)).Retired) break;
            }
            Assert.Equal(reference.R, cpu.R);
            Assert.Equal(reference.TStates, cpu.TStates);
            Assert.Equal(referenceBus.Trace, owner.Trace);
            Assert.Equal(referenceMemory, memory);
        }
    }

    [Fact]
    public void InvalidRestoreIsDetachedAndLeavesTheExistingContinuationUnchanged()
    {
        var cpu = new Z80(new ForbiddenBus());
        cpu.PeekNextFrontier();
        var state = cpu.CaptureFrontier();
        var before = Wire(state);
        var garbage = state.Temporaries.ToArray();
        garbage[24] = 1;
        FrontierState[] invalid =
        [
            state with { Version = 2 }, state with { TStates = -1 }, state with { EiDelay = 3 },
            state with { Family = (PrefixFamily)127 }, state with { Stage = ushort.MaxValue },
            state with { OpcodePresent = true }, state with { TemporaryCount = 31 },
            state with { Temporaries = new byte[33] }, state with { Temporaries = garbage },
            state with { Immediate = false }, state with { Refresh = false },
            state with { Pending = state.Pending!.Value with { AtCpuTState = 0 } },
            state with { Pending = state.Pending!.Value with { Kind = FrontierEffectKind.PortWrite } }
        ];
        foreach (var bad in invalid)
        {
            Assert.Throws<ArgumentException>(() => cpu.RestoreFrontier(bad));
            Assert.Equal(before, Wire(cpu.CaptureFrontier()));
        }
    }

    [Fact]
    public void ResetCancelsPendingPrefixAndLegacyStepCannotConsumeAPartialInstruction()
    {
        var cpu = new Z80(new ForbiddenBus());
        cpu.PeekNextFrontier();
        cpu.CommitFrontier(0xdd);
        Assert.Equal("A partial frontier instruction cannot use Step.",
            Assert.Throws<InvalidOperationException>(() => cpu.Step()).Message);
        cpu.Reset();
        Assert.True(cpu.AtInstructionBoundary);
        Assert.Equal(new FrontierEffect(4, FrontierEffectKind.M1Read, 0, InstructionStart: true), cpu.PeekNextFrontier());
    }

    [Fact]
    public void CapturedAndRestoredTemporaryBuffersCannotMutateALiveCpu()
    {
        var cpu = new Z80(new ForbiddenBus());
        cpu.PeekNextFrontier();
        cpu.CommitFrontier(0x18); // JR, suspend on its operand read
        var captured = cpu.CaptureFrontier();
        var restored = new Z80(new ForbiddenBus());
        restored.RestoreFrontier(captured);
        var before = Wire(restored.CaptureFrontier());
        Array.Fill(captured.Temporaries, (byte)0xff);
        Assert.Equal(before, Wire(cpu.CaptureFrontier()));
        Assert.Equal(before, Wire(restored.CaptureFrontier()));
        restored.CommitFrontier(0xfe);
        restored.RestoreFrontier(restored.CaptureFrontier());
        restored.CommitFrontier();
        Assert.Equal((ushort)0, restored.R.PC);
    }

    [Fact]
    public void NmiEdgeAtAnAcknowledgingFrontierRemainsLatchedForTheNextBoundary()
    {
        var cpu = new Z80(new ForbiddenBus());
        cpu.Nmi();
        Assert.Equal(FrontierEffectKind.Internal, cpu.PeekNextFrontier().Kind);
        cpu.CommitFrontier(nmiEdge: true);
        while (!cpu.AtInstructionBoundary)
        {
            cpu.PeekNextFrontier();
            cpu.CommitFrontier();
        }
        Assert.True(cpu.NmiPendingState);
        Assert.Equal(FrontierEffectKind.Internal, cpu.PeekNextFrontier().Kind);
    }
}
