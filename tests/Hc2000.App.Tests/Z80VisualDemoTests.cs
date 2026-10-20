using System.Security.Cryptography;
using Hc2000.App.Runtime;
using Hc2000.Machine.Memory;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class Z80VisualDemoTests
{
    [Fact]
    public void EmbeddedBinariesMatchPublishedHashes()
    {
        Assert.Equal("8014CA15DB196BF06C657A3E8860C5D0FF91071D9C56B824B42A0951ABD074F1",
            Convert.ToHexString(SHA256.HashData(Z80VisualDemo.PaintProgram)));
        Assert.Equal("45482B05BFCE3370D4300FB4422D68ADBA984217DC5C667B32CE909D985B44D7",
            Convert.ToHexString(SHA256.HashData(Z80VisualDemo.IdleProgram)));
    }

    [Fact]
    public void NotSteppingCpuLeavesFreshScreenBlank()
    {
        _ = Z80VisualDemo.CreateMachine(out var cpu, out var memory);
        Assert.Equal(0, cpu.TStates);
        Assert.All(memory.Ram.Span.Slice(Z80VisualDemo.ScreenAddress, 0x1b00).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void ActualCpuProgramWritesPatternAndUlaRendersIt()
    {
        var machine = Z80VisualDemo.CreateMachine(out var cpu, out var memory);
        var frame = machine.RunFrame();

        Assert.Equal(Z80VisualDemo.ProgramAddress, (ushort)0x8000);
        Assert.True(cpu.TStates > 0);
        Assert.True(cpu.Halted);
        Assert.All(memory.Ram.Span.Slice(Z80VisualDemo.ScreenAddress, 0x20).ToArray(), b => Assert.Equal(0xaa, b));
        Assert.All(memory.Ram.Span.Slice(0x5800, 0x20).ToArray(), b => Assert.Equal(0x07, b));
        Assert.All(memory.Ram.Span.Slice(Z80VisualDemo.ScreenAddress + 0x20, 0x1800 - 0x20).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(352, frame.Width);
        Assert.Equal(296, frame.Height);
        Assert.Equal(352 * 296, frame.Pixels.Length);
        Assert.True(frame.Pixels.Span.ToArray().Distinct().Count() > 1);
    }

    [Fact]
    public void CpuOffNegativeControlDoesNotPaintOrRenderCompletedImage()
    {
        var machine = Z80VisualDemo.CreateMachine(out var cpu, out var memory, paint: false);
        var frame = machine.RunFrame();

        Assert.True(cpu.TStates > 0);
        Assert.True(cpu.Halted);
        Assert.All(memory.Ram.Span.Slice(Z80VisualDemo.ScreenAddress, 0x1b00).ToArray(), b => Assert.Equal(0, b));
        Assert.Single(frame.Pixels.Span.ToArray().Distinct());
    }

    [Fact]
    public void FreshCompositionResetReproducesSameCpuPaintedFrame()
    {
        static (string hash, byte first, long states) Run()
        {
            var machine = Z80VisualDemo.CreateMachine(out var cpu, out var memory);
            var frame = machine.RunFrame();
            var hash = Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame.Pixels.Span)));
            return (hash, memory.Ram.Span[Z80VisualDemo.ScreenAddress], cpu.TStates);
        }

        var first = Run();
        var second = Run();
        Assert.Equal(first.hash, second.hash);
        Assert.Equal(0xaa, first.first);
        Assert.Equal(first.states, second.states);
    }
}
