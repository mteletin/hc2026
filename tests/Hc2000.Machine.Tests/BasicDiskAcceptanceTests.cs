using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using Hc2000.Machine.Input;
using Hc2000.Machine.Video;
using Xunit;
using Xunit.Sdk;

namespace Hc2000.Machine.Tests;

public sealed class BasicDiskAcceptanceTests
{
    [Fact]
    public void BasicV2PhysicalPrintControlIsConsumed()
    {
        if (!MachineBootAcceptanceTests.AssetsAvailable("basic")) return;

        var root = StorageAssetTests.Root();
        var boot = new MachineBootAcceptanceTests.BootHarness(
            MachineBootAcceptanceTests.BootHarness.Config.ForBasic("hc2000-v2"));
        Assert.True(MachineBootAcceptanceTests.BootHarness.ReachesEditor(boot, maxFrames: 1000));
        for (var frame = 0; frame < 30; frame++) boot.RunFrame();
        boot.TypeKey(HcKey.P); // BASIC K-mode P key produces PRINT token F5.
        boot.TypeKey(HcKey.Space);
        boot.TypeKey(HcKey.D2);
        var eLine = boot.Memory.Read(0x5C59) | (boot.Memory.Read(0x5C5A) << 8);
        var line = Enumerable.Range(0, 5).Select(i => boot.Memory.Read((ushort)(eLine + i))).ToArray();
        Assert.Equal(new byte[] { 0xF5, 0x20, 0x32, 0x0D, 0x80 }, line);

        boot.TraceInstructions = true;
        boot.TypeKey(HcKey.Enter);
        VideoFrame? output = null;
        for (var frame = 0; frame < 30; frame++) output = boot.RunFrame();
        var screen = boot.ScreenText();
        var evidence = Path.Combine(root, "temp", "wi-118-step7a");
        Directory.CreateDirectory(evidence);
        WriteBmp(Path.Combine(evidence, "physical-print-control.bmp"), output!);
        File.WriteAllText(Path.Combine(evidence, "physical-print-control.txt"),
            $"Command line: {Convert.ToHexString(line)}\nScreen:\n{screen}\n" +
            $"Saw LINE-SCAN: {boot.SawLineScan}\nFirst RST08: {boot.FirstRst08Trace ?? "none"}\n" +
            $"PC: {boot.Cpu.Cpu.R.PC:X4} ERR_NR: {boot.Memory.Read(0x5C3A):X2}\n" +
            $"M1 trace:\n{string.Join("\n", boot.M1Trace)}\n");
        Assert.True(boot.SawLineScan, "Physical PRINT control did not reach BASIC LINE-SCAN.");
        Assert.Null(boot.FirstRst08Trace);
    }

    [Theory]
    [BasicDiskData("ALBA8.DSK")]
    [BasicDiskData("ALEO-UTILS.DSK")]
    [BasicDiskData("ALEO-GAMES1.DSK")]
    [BasicDiskData("BASDEMO35.DSK")]
    [BasicDiskData("BASIC2.DSK")]
    [BasicDiskData("CRISTIG1.DSK")]
    [BasicDiskData("GEOOLDIES.DSK")]
    [BasicDiskData("RARESGOLDENAXE.DSK")]
    public void BasicV2CatReadsEachTitleDisk(string disk)
    {
        if (!MachineBootAcceptanceTests.AssetsAvailable("basic")) return;

        var root = StorageAssetTests.Root();
        var diskPath = Path.Combine(root, "disks", "basic", disk);
        var diskHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(diskPath))).ToLowerInvariant();
        var boot = new MachineBootAcceptanceTests.BootHarness(
            MachineBootAcceptanceTests.BootHarness.Config.ForBasicDisk("hc2000-v2", disk));
        Assert.True(MachineBootAcceptanceTests.BootHarness.ReachesEditor(boot, maxFrames: 1000),
            $"BASIC-v2 editor not ready for {disk}; tick={boot.MasterTick} screen={boot.ScreenText()}");
        for (var frame = 0; frame < 30; frame++) boot.RunFrame();

        // K-DECODE 0333: CAPS+SYMBOL SHIFT enters E-mode; SYMBOL SHIFT+9 maps through 0254+39
        // to token CF (CAT). These are physical matrix inputs, never injected BASIC bytes.
        boot.TypeExtendedChord();
        Assert.Equal(1, boot.Memory.Read(0x5C41)); // MODE: extended digit lookup
        boot.TypeShiftedKey(HcKey.SymbolShift, HcKey.D9);
        boot.TypeKey(HcKey.Space);
        boot.TypeKey(HcKey.D1);
        Assert.Equal(0, boot.Memory.Read(0x5C41));

        var eLine = boot.Memory.Read(0x5C59) | (boot.Memory.Read(0x5C5A) << 8);
        var line = Enumerable.Range(0, 5).Select(i => boot.Memory.Read((ushort)(eLine + i))).ToArray();
        Assert.Equal(new byte[] { 0xCF, 0x20, 0x31, 0x0D, 0x80 }, line);

        var before = boot.RunFrame();
        boot.M1Trace.Clear();
        boot.PortTrace.Clear();
        boot.TraceInstructions = true;
        boot.TypeKey(HcKey.Enter);
        VideoFrame? output = null;
        for (var frame = 0; frame < 1000; frame++) output = boot.RunFrame();

        var fdc = boot.Floppy!.Fdc.DiagLog.ToString();
        var evidence = Path.Combine(root, "temp", "wi-118-step7a");
        Directory.CreateDirectory(evidence);
        var if1Path = Path.Combine(root, "roms", "hc2000", "v2", "if1.rom");
        var if1Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(if1Path))).ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(disk);
        WriteBmp(Path.Combine(evidence, name + "-cat.bmp"), output!);
        File.WriteAllText(Path.Combine(evidence, name + "-cat.txt"),
            $"Disk: {disk}\nSHA-256: {diskHash}\nIF1 ROM SHA-256: {if1Hash}\nCommand line: {Convert.ToHexString(line)}\n" +
            $"MODE: {boot.Memory.Read(0x5C41):X2}\n7E: {boot.Memory.Latches.Port7E:X2}\n" +
            $"PC: {boot.Cpu.Cpu.R.PC:X4} SP: {boot.Cpu.Cpu.R.SP:X4}\n" +
            $"ERR_NR: {boot.Memory.Read(0x5C3A):X2} CH_ADD: {boot.Memory.Read(0x5C5D) | (boot.Memory.Read(0x5C5E) << 8):X4}\n" +
            $"Shadow: {boot.Memory.Latches.Shadow} EditorReady: {boot.EditorReady()} " +
            $"E_LINE: {boot.Memory.Read(0x5C59) | (boot.Memory.Read(0x5C5A) << 8):X4} " +
            $"WORKSP: {boot.Memory.Read(0x5C61) | (boot.Memory.Read(0x5C62) << 8):X4}\n" +
            $"Master tick: {boot.MasterTick}\n" +
            $"Screen text:\n{boot.ScreenText()}\nM1 trace:\n{string.Join("\n", boot.M1Trace)}\n" +
            $"First RST08 after LINE-SCAN: {boot.FirstRst08Trace ?? "none"}\n" +
            $"First transfer to 0008: {boot.FirstTransferTo0008 ?? "none"}\n" +
            $"First IF1 page-out Step: {boot.FirstIf1PageOutStepTrace ?? "none"}\n" +
            $"First RAM transfer after LINE-SCAN: {boot.FirstRamTransferAfterLineScan ?? "none"}\n" +
            $"Saw LINE-SCAN: {boot.SawLineScan}\nPost-LINE-SCAN trace:\n{string.Join("\n", boot.PostLineScanTrace)}\n" +
            $"Last instructions:\n{string.Join("\n", boot.InstructionTrace)}\n" +
            $"Port trace:\n{string.Join("\n", boot.PortTrace)}\nFDC:\n{fdc}");

        Assert.Contains("op=6", fdc); // READ DATA issued
        Assert.Contains("R@", fdc);   // bytes actually returned from the inserted DSK
        Assert.True(boot.VideoHasText(), $"CAT output blank for {disk}; screen={boot.ScreenText()}");
        Assert.NotEqual(boot.FrameHash(before), boot.FrameHash(output!));
    }

    [Theory]
    [BasicDiskData("ALBA8.DSK", "run")]
    [BasicDiskData("ALEO-UTILS.DSK", "artist2")]
    [BasicDiskData("ALEO-GAMES1.DSK", "run")]
    [BasicDiskData("BASDEMO35.DSK", "run")]
    [BasicDiskData("BASIC2.DSK", "run")]
    [BasicDiskData("CRISTIG1.DSK", "run")]
    [BasicDiskData("GEOOLDIES.DSK", "ManicMiner")]
    [BasicDiskData("RARESGOLDENAXE.DSK", "goldenaxe")]
    public void FreshBasicV2LoadsAndRunsEachTitleProgram(string disk, string program)
    {
        if (!MachineBootAcceptanceTests.AssetsAvailable("basic")) return;
        var root = StorageAssetTests.Root();
        var diskPath = Path.Combine(root, "disks", "basic", disk);
        var diskHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(diskPath))).ToLowerInvariant();
        var boot = new MachineBootAcceptanceTests.BootHarness(
            MachineBootAcceptanceTests.BootHarness.Config.ForBasicDisk("hc2000-v2", disk));
        Assert.True(MachineBootAcceptanceTests.BootHarness.ReachesEditor(boot, maxFrames: 1000));
        for (var frame = 0; frame < 30; frame++) boot.RunFrame();

        TypeLoadCommand(boot, program);
        var eLine = boot.Memory.Read(0x5C59) | (boot.Memory.Read(0x5C5A) << 8);
        var expected = new List<byte> { 0xef, 0x20, (byte)'*', (byte)'\"', (byte)'d', (byte)'\"', (byte)';', (byte)'1', (byte)';', (byte)'\"' };
        expected.AddRange(System.Text.Encoding.ASCII.GetBytes(program));
        expected.AddRange([(byte)'\"', (byte)0x0d, (byte)0x80]);
        var actual = Enumerable.Range(0, expected.Count).Select(i => boot.Memory.Read((ushort)(eLine + i))).ToArray();
        Assert.Equal(expected, actual);

        boot.TraceInstructions = true;
        boot.TypeKey(HcKey.Enter);
        VideoFrame? loaded = null;
        for (var frame = 0; frame < 1000; frame++) loaded = boot.RunFrame();
        var loadFdc = boot.Floppy!.Fdc.DiagLog.ToString();
        var loadedPc = boot.Cpu.Cpu.R.PC;
        var loadedErr = boot.Memory.Read(0x5C3A);
        var loadedScreen = boot.ScreenText();
        var loadedFrameHash = boot.FrameHash(loaded!);
        var loadedELine = boot.Memory.Read(0x5C59) | (boot.Memory.Read(0x5C5A) << 8);
        var loadedWsp = boot.Memory.Read(0x5C61) | (boot.Memory.Read(0x5C62) << 8);
        var loadedEditorReady = PostLoadEditorReady(boot);
        string runCommand;
        VideoFrame? output = loaded;
        if (loadedEditorReady)
        {
            var runLineAt = boot.Memory.Read(0x5C59) | (boot.Memory.Read(0x5C5A) << 8);
            var runLine = new byte[] { 0xf7, 0x0d, 0x80 }; // tokenized RUN + line terminators
            for (var i = 0; i < runLine.Length; i++) boot.Memory.Write((ushort)(runLineAt + i), runLine[i]);
            Assert.Equal(runLine, Enumerable.Range(0, runLine.Length)
                .Select(i => boot.Memory.Read((ushort)(runLineAt + i))).ToArray());
            runCommand = "RUN (token F7 0D 80 written at E_LINE; physical Enter)";
            boot.TypeKey(HcKey.Enter); // execute RUN through the real BASIC line scanner
        }
        else
        {
            runCommand = "not entered: post-LOAD EditorReady=false (observe loaded program)";
        }
        for (var frame = 0; frame < 1000; frame++) output = boot.RunFrame();
        var fdc = boot.Floppy.Fdc.DiagLog.ToString();
        var if1Path = Path.Combine(root, "roms", "hc2000", "v2", "if1.rom");
        var if1Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(if1Path))).ToLowerInvariant();
        var evidence = Path.Combine(root, "temp", "wi-118-step7a");
        Directory.CreateDirectory(evidence);
        var name = Path.GetFileNameWithoutExtension(disk);
        WriteBmp(Path.Combine(evidence, name + "-run.bmp"), output!);
        File.WriteAllText(Path.Combine(evidence, name + "-run.txt"),
            $"Disk: {disk}\nSHA-256: {diskHash}\nIF1 ROM SHA-256: {if1Hash}\nProfile: hc2000-v2 BASIC, drive 1\n" +
            $"Commands: LOAD *\"d\";1;\"{program}\"<ENTER> (physical keys J, Enter); {runCommand}\n" +
            $"Frames observed: 1000 after LOAD, 1000 after RUN/observation; Master tick: {boot.MasterTick}\n" +
            $"LOAD READ DATA commands: {CountLines(loadFdc, "op=6")} returned-records: {CountLines(loadFdc, "R@")}\n" +
            $"Total READ DATA commands: {CountLines(fdc, "op=6")} returned-records: {CountLines(fdc, "R@")}\n" +
            $"After LOAD: PC={loadedPc:X4} ERR_NR={loadedErr:X2} EditorReady={loadedEditorReady} " +
            $"E_LINE={loadedELine:X4} WORKSP={loadedWsp:X4} gap={loadedWsp - loadedELine} " +
            $"frame SHA-256={loadedFrameHash} screen:\n{loadedScreen}\n" +
            $"Final observation: PC={boot.Cpu.Cpu.R.PC:X4} ERR_NR={boot.Memory.Read(0x5C3A):X2} " +
            $"frame SHA-256={boot.FrameHash(output!)} screen:\n{boot.ScreenText()}\n" +
            $"Last instructions:\n{string.Join("\n", boot.InstructionTrace)}\nFDC:\n{fdc}");

        Assert.Contains("op=6", fdc);
        Assert.Contains("R@", fdc);
        Assert.True(boot.VideoHasText(), $"Output blank for {disk}/{program}; screen={boot.ScreenText()}");
        Assert.NotNull(loaded);
    }

    private static bool PostLoadEditorReady(MachineBootAcceptanceTests.BootHarness boot)
    {
        if (boot.Memory.Read(0x5C41) != 0) return false;
        var eLine = boot.Memory.Read(0x5C59) | (boot.Memory.Read(0x5C5A) << 8);
        var workspace = boot.Memory.Read(0x5C61) | (boot.Memory.Read(0x5C62) << 8);
        var gap = workspace - eLine;
        return eLine >= 0x4000 && eLine < 0x10000 && workspace >= 0x4000 && workspace < 0x10000 &&
            gap is >= 1 and <= 32;
    }

    private static void TypeLoadCommand(MachineBootAcceptanceTests.BootHarness boot, string program)
    {
        boot.TypeKey(HcKey.J); // K-mode J tokenizes to LOAD.
        boot.TypeKey(HcKey.Space);
        TypeSymbol(boot, HcKey.B); // *
        TypeSymbol(boot, HcKey.P); // opening quote
        boot.TypeKey(HcKey.D); // drive designator d
        TypeSymbol(boot, HcKey.P); // closing quote
        TypeSymbol(boot, HcKey.O); // ;
        boot.TypeKey(HcKey.D1);
        TypeSymbol(boot, HcKey.O); // ;
        TypeSymbol(boot, HcKey.P); // opening quote
        foreach (var ch in program)
        {
            var key = Enum.Parse<HcKey>(char.IsDigit(ch) ? $"D{ch}" : char.ToUpperInvariant(ch).ToString());
            if (char.IsUpper(ch)) boot.TypeShiftedKey(HcKey.CapsShift, key);
            else boot.TypeKey(key);
        }
        TypeSymbol(boot, HcKey.P); // closing quote
    }

    private static int CountLines(string text, string marker) =>
        text.Split('\n').Count(line => line.Contains(marker, StringComparison.Ordinal));

    private static void TypeSymbol(MachineBootAcceptanceTests.BootHarness boot, HcKey key) =>
        boot.TypeShiftedKey(HcKey.SymbolShift, key);

    private static void WriteBmp(string path, VideoFrame frame)
    {
        var pixelBytes = checked(frame.Width * frame.Height * 4);
        var bmp = new byte[54 + pixelBytes];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), frame.Width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -frame.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), pixelBytes);
        var pixels = frame.Pixels.Span;
        for (var i = 0; i < pixels.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(54 + i * 4), pixels[i]);
        File.WriteAllBytes(path, bmp);
    }
}

// xUnit v2 data-source skips apply to this row, not to the whole theory.
public sealed class BasicDiskDataAttribute : DataAttribute
{
    private readonly object[] data;

    public BasicDiskDataAttribute(string disk, params string[] program)
        : this(StorageAssetTests.Root(), disk, program) { }

    internal BasicDiskDataAttribute(string root, string disk, string[] program)
    {
        data = [disk, .. program];
        var relative = Path.Combine("disks", "basic", disk);
        if (!File.Exists(Path.Combine(root, relative)))
            Skip = "Copyrighted asset is absent: " + relative;
    }

    public override IEnumerable<object[]> GetData(MethodInfo testMethod) => [data];
}
