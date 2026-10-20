using Hc2000.App.Debugging;
using Hc2000.Disasm;
using Hc2000.Machine.Memory;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class ExecutionRecorderTests
{
    private static HcMemory Memory() => new(new MemoryProfile(), new byte[16384], new byte[16384], new byte[16384]);

    [Fact]
    public void TextAndBinaryPreserveConfigIsolationMismatchAndStart()
    {
        var memory = Memory();
        var recorder = new ExecutionRecorder(new byte[32768], MemoryStore.Rom, 0, "hc2000-v2", new string('a', 64));
        recorder.Start();
        recorder.Observe(memory, 0, ExecutionFlags.M1 | ExecutionFlags.Start, 0);
        recorder.Observe(memory, 1, ExecutionFlags.M1, 0); // continuation, not another Start
        recorder.Observe(memory, 2, ExecutionFlags.Read, 1);
        recorder.Observe(memory, 0, ExecutionFlags.Write);
        memory.WritePort7E(1);
        recorder.Observe(memory, 0, ExecutionFlags.Read, 0);
        recorder.Stop();
        recorder.Observe(memory, 0, ExecutionFlags.M1 | ExecutionFlags.Start, 0);
        var text = ExecutionMap.Parse(recorder.ExportText());
        var binary = ExecutionMap.Parse(recorder.ExportBinary());
        Assert.True(text.Success); Assert.True(binary.Success);
        Assert.Equal(2, text.Configurations.Count);
        for (var config = 0; config < 2; config++)
        {
            Assert.Equal(text.Configurations[config].Windows, binary.Configurations[config].Windows);
            for (var a = 0; a < 65536; a++)
                Assert.Equal(text.Configurations[config].FlagsAt(a), binary.Configurations[config].FlagsAt(a));
        }
        Assert.Equal(ExecutionFlags.M1 | ExecutionFlags.Start | ExecutionFlags.Write, text.Configurations[0].FlagsAt(0));
        Assert.Equal(ExecutionFlags.M1, text.Configurations[0].FlagsAt(1));
        Assert.Equal(ExecutionFlags.Read | ExecutionFlags.Mismatch, text.Configurations[0].FlagsAt(2));
        Assert.Equal(ExecutionFlags.Read, text.Configurations[1].FlagsAt(0));
        Assert.Equal(16384, text.Configurations[1].Windows[0].ImageOffset);
    }

    [Fact]
    public void PhysicalAliasesAndFfffEndpointAreExportedWithoutBankGuessing()
    {
        var memory = Memory(); memory.SetCpm(true);
        var recorder = new ExecutionRecorder(new byte[65536], MemoryStore.Ram, 0, "fixture", new string('b', 64));
        recorder.Start(); recorder.Observe(memory, 0xffff, ExecutionFlags.Target);
        var map = ExecutionMap.Parse(recorder.ExportText());
        Assert.True(map.Success);
        var config = Assert.Single(map.Configurations);
        Assert.Equal(ExecutionFlags.Target, config.FlagsAt(65535));
        Assert.Contains(config.Windows, w => w.CpuStart == 0xc000 && w.ImageOffset == 0xe000);
        Assert.Contains(config.Windows, w => w.CpuStart == 0xe000 && w.ImageOffset == 0xe000);
    }

    [Fact]
    public void ImageBindingIsCopiedAndIf1RamMirrorsRemainExplicitWindows()
    {
        var memory = Memory(); memory.SetShadow(true);
        var image = new byte[1024];
        var recorder = new ExecutionRecorder(image, MemoryStore.ShadowRam, 0, "fixture", new string('c', 64));
        image[0] = 42; // caller mutation must not alter binding
        recorder.Start();
        recorder.Observe(memory, 0x2800, ExecutionFlags.Read, 0);
        recorder.Observe(memory, 0x2801, ExecutionFlags.Read, 0);
        recorder.Observe(memory, 0x2c00, ExecutionFlags.Read, 42);
        var text = recorder.ExportText(); var map = ExecutionMap.Parse(text);
        Assert.True(map.Success);
        var config = Assert.Single(map.Configurations);
        Assert.Equal(4, config.Windows.Count);
        Assert.All(config.Windows, w => Assert.Equal(0, w.ImageOffset));
        Assert.Equal(ExecutionFlags.Read | ExecutionFlags.Mismatch, config.FlagsAt(0x2c00));
        Assert.Equal(ExecutionFlags.Read, config.FlagsAt(0x2800));
        Assert.Contains("mark 2800-2801 02\n", System.Text.Encoding.UTF8.GetString(text));
        Assert.Throws<InvalidOperationException>(() => recorder.Observe(Memory(), 0, ExecutionFlags.Read, 0));
    }

    [Fact]
    public void BadFlagsAndMissingReadValueAreRejectedButUnmappedEventsAreNotMarked()
    {
        var memory = Memory();
        var recorder = new ExecutionRecorder(new byte[16384], MemoryStore.Rom, 0, "fixture", new string('b', 64));
        recorder.Start();
        Assert.Throws<ArgumentException>(() => recorder.Observe(memory, 0, ExecutionFlags.Start, 0));
        Assert.Throws<ArgumentException>(() => recorder.Observe(memory, 0, (ExecutionFlags)0x80));
        Assert.Throws<ArgumentException>(() => recorder.Observe(memory, 0, ExecutionFlags.Read));
        recorder.Observe(memory, 0x8000, ExecutionFlags.Write);
        var empty = Assert.Single(ExecutionMap.Parse(recorder.ExportText()).Configurations);
        Assert.Equal((ExecutionFlags)0, empty.FlagsAt(0x8000));
        Assert.Equal((ExecutionFlags)0, empty.FlagsAt(0));
        recorder.Observe(memory, 0, ExecutionFlags.Read, 0);
        memory.SetShadow(true);
        recorder.Observe(memory, 0, ExecutionFlags.M1 | ExecutionFlags.Start, 0);
        var config = Assert.Single(ExecutionMap.Parse(recorder.ExportText()).Configurations);
        Assert.Equal("7e-00-cpm-0-shadow-0", config.Id);
        Assert.Equal(ExecutionFlags.Read, config.FlagsAt(0));
    }
}
