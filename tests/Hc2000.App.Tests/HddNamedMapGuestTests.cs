using System.Security.Cryptography;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.Asm;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Video;
using Xunit;
using LiveMachine = Hc2000.Machine.Machine;

namespace Hc2000.App.Tests;

public sealed class HddNamedMapGuestTests
{
    [Fact]
    public void RealGuestInstallsFromAAndReadsTheRequestedNamesOnCThroughF()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux native file identity contract");
        var root = RomSelectionFixture.RepositoryRoot();
        var diskPath = Path.Combine(root, "disks/cpm/CPM22-HC.DSK");
        var romPath = Path.Combine(root, ProfileRomSelection.WholeChipSourcePath);
        Assert.SkipWhen(!File.Exists(romPath) || new FileInfo(romPath).Length != ProfileRomSelection.WholeChipLength
            || !File.Exists(diskPath) || new FileInfo(diskPath).Length <= 256, "Materialized whole-chip ROM and CP/M disk required");
        using var fixture = new HddColdVolumeTests();
        WriteGuestVolumes(fixture.PathName);
        var sourceHash = Hash(fixture.PathName);
        var diskHash = Hash(diskPath);
        var disk = Dsk.Load(File.ReadAllBytes(diskPath));
        var fs = CpmFs.Open(disk);
        var installerPath = Path.Combine(root, "bios/src/hc2000/upgrade/hdd.init.asm");
        fs.Put("HDINST.COM", Assemble(File.ReadAllText(installerPath), installerPath));
        fs.Put("MAPREAD.COM", Assemble(ReadProof(), "map-read.asm"));

        // Deliberately not catalogue order: the same filename has distinguishable payloads.
        using var volume = HddColdVolume.OpenMapped(fixture.PathName,
            [new(2, "VOL2.DSK"), new(3, "VOL3.DSK"), new(4, "VOL4.DSK"), new(5, "VOL1.DSK")]);
        var prepared = BasicMachineComposition.PrepareAdmittedHddMapWholeChipV2(
            ProfileRomSelection.LoadWholeChipV2(root), volume);
        BlockDiskPort? controller = null;
        try
        {
            var machine = BasicMachineComposition.CreateColdNewMachine(prepared, new(),
                new(new(), disk), new(new()), _ => { }, value => controller = value);
            Assert.True(controller!.GuestAckEnabled);
            var media = new HddMediaAdapter(controller, prepared.InitialHdd);
            volume.Admission.AcknowledgeInstalled(prepared, machine, media);
            var installed = media.Inspect();

            // Normal keyboard boot and A-loaded COMs, not a RAM/PC patch or host BDOS call.
            Boot(machine);
            Command(machine, "HDINST"); Frames(machine, 200);
            Command(machine, "MAPREAD");
            // Only the COM prints this marker, after four real OPEN/READ/CLOSEs and byte checks.
            Until(machine, frame => HasGlyphs(frame, ">>>>"), "guest C-F named-map read proof");

            Assert.Equal(installed.ToArray(), media.Inspect().ToArray());
            foreach (var image in prepared.InitialHdd)
            {
                var captured = controller!.CaptureHdd(image.Drive);
                Assert.True(captured.WriteProtected); Assert.False(captured.Dirty);
                Assert.Equal(SHA256.HashData(image.Bytes.AsSpan()), SHA256.HashData(captured.Bytes.AsSpan()));
            }
            Assert.Equal(sourceHash, Hash(fixture.PathName));
            Assert.Equal(diskHash, Hash(diskPath));
            Assert.Equal(HddMediaAdapter.SwitchUnavailable, media.Apply(new(HddAction.Attach)).Error);
        }
        finally { volume.Admission.RejectAfterOwnerDrain(); }
    }

    [Fact]
    public void RealGuestClosesFlushesAcknowledgesAndReadsNewCBindingWithoutRestart()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux native file identity contract");
        var root = RomSelectionFixture.RepositoryRoot();
        var diskPath = Path.Combine(root, "disks/cpm/CPM22-HC.DSK");
        var romPath = Path.Combine(root, ProfileRomSelection.WholeChipSourcePath);
        Assert.SkipWhen(!File.Exists(romPath) || new FileInfo(romPath).Length != ProfileRomSelection.WholeChipLength
            || !File.Exists(diskPath) || new FileInfo(diskPath).Length <= 256, "Materialized whole-chip ROM and CP/M disk required");
        using var fixture = new HddColdVolumeTests();
        var sourceHash = Hash(fixture.PathName); var diskHash = Hash(diskPath);
        var disk = Dsk.Load(File.ReadAllBytes(diskPath));
        var fs = CpmFs.Open(disk);
        foreach (var (name, file) in new[] { ("HDINST.COM", "hdd.init.asm"), ("HDSWAP.COM", "hdd.swap.asm") })
        {
            var path = Path.Combine(root, "bios/src/hc2000/upgrade", file);
            fs.Put(name, Assemble(File.ReadAllText(path), path));
        }
        fs.Put("BEFORE.COM", Assemble(ReadProof(0x41), "before.asm"));
        fs.Put("AFTER.COM", Assemble(ReadProof(0x42, ">>>>>>>>"), "after.asm"));
        using var volume = HddColdVolume.OpenMapped(fixture.PathName, [new(2, "VOL1.DSK")]);
        var prepared = BasicMachineComposition.PrepareAdmittedHddMapWholeChipV2(ProfileRomSelection.LoadWholeChipV2(root), volume);
        BlockDiskPort? controller = null;
        try
        {
            var machine = BasicMachineComposition.CreateColdNewMachine(prepared, new(), new(new(), disk), new(new()), _ => { }, value => controller = value);
            var adapter = new HddMediaAdapter(controller!, prepared.InitialHdd);
            volume.Admission.AcknowledgeInstalled(prepared, machine, adapter);
            bool media() { adapter.PumpGuestSwitch(); return !controller!.GuestSwitchHoldsCpu; }
            Boot(machine, media);
            Command(machine, "HDINST", media); Frames(machine, 200, media);
            Command(machine, "BEFORE", media);
            Until(machine, frame => HasGlyphs(frame, ">>>>"), "guest read of original C binding", media);
            Frames(machine, 200, media); // Finish the COM's warm boot before typing the next command.
            using (var target = HddColdVolume.Open(fixture.PathName, "VOL2.DSK"))
            {
                var pending = adapter.Apply(new(HddAction.BeginSwitch, Target: new(fixture.PathName, target.Identity), ExpectedGeneration: 1));
                Assert.Null(pending.Error); Assert.Equal(HddSwitchState.AwaitingGuest, pending.Switch?.State);
            }
            Assert.Equal(0x41, controller!.CaptureHdd(2).Bytes[32768]);
            Command(machine, "HDSWAP", media); Frames(machine, 200, media);
            var result = adapter.Apply(new(HddAction.Inspect));
            Assert.Equal(HddSwitchState.Committed, result.Switch?.State);
            Assert.Equal("VOL2.DSK", result.Drives[0].Name); Assert.Equal(2u, result.Drives[0].Generation);
            Assert.True(result.Drives[0].WriteProtected); Assert.False(result.Drives[0].Dirty);
            Assert.False(controller.GuestSwitchHoldsCpu);
            Command(machine, "AFTER", media);
            Until(machine, frame => HasGlyphs(frame, ">>>>>>>>"), "guest read of replacement C binding", media);
            Assert.Equal(sourceHash, Hash(fixture.PathName)); Assert.Equal(diskHash, Hash(diskPath));
        }
        finally { volume.Admission.RejectAfterOwnerDrain(); }
    }

    private static void WriteGuestVolumes(string path)
    {
        // Independent literal directory/footer CRC32; four different SEED.TXT payloads.
        // Keep the original two-volume fixture and its retained hashes unchanged.
        var payload = new byte[HddMediaAdapter.ImageLength];
        using (var source = File.OpenRead(path)) source.ReadExactly(payload);
        using var target = File.Create(path);
        for (byte i = 0; i < 4; i++)
        {
            payload.AsSpan(32768, 4096).Fill((byte)(0x41 + i));
            target.Write(payload);
        }
        target.Write(Convert.FromHexString("4843564401000400564f4c312020202044534b0000008000000000005ba36203564f4c322020202044534b010000800001000000b9eccf25564f4c332020202044534b000000800002000000d82b848e564f4c342020202044534b0100008000030000007d739568b953b0f8"));
        target.Write(Convert.FromHexString("4843564d31454e4401000000000000000102030405060708090a0b0c0d0e0f10010000000000000000000002000000006c000000b953b0f8000000001aa635e0"));
    }

    private static byte[] Assemble(string source, string path)
    {
        var result = Assembler.AssembleText(source, file: path);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        return result.ToBinary();
    }

    // Test software loaded by the real CCP. Each FCB is fresh; every drive uses BDOS, not port pokes.
    private static string ReadProof(byte? singleValue = null, string marker = ">>>>") => "ORG 0x0100\n" + string.Join("\n", Enumerable.Range(0, singleValue.HasValue ? 1 : 4).Select(i => $$"""
        LD HL,Fcb
        LD DE,Fcb+1
        LD BC,35
        LD (HL),0
        LDIR
        LD A,{{i + 3}}
        LD (Fcb),A
        LD HL,Name
        LD DE,Fcb+1
        LD BC,11
        LDIR
        LD DE,Fcb
        LD C,15
        CALL 5
        CP 255
        JP Z,Failure
        LD DE,Dma
        LD C,26
        CALL 5
        LD DE,Fcb
        LD C,20
        CALL 5
        OR A
        JP NZ,Failure
        LD HL,Dma
        LD B,128
        Check{{i}}:
        LD A,(HL)
        CP {{(singleValue is { } value ? value : 0x41 + (i + 1) % 4)}}
        JP NZ,Failure
        INC HL
        DJNZ Check{{i}}
        LD DE,Fcb
        LD C,16
        CALL 5
        CP 255
        JP Z,Failure
        """)) + $$"""

        LD DE,Passed
        LD C,9
        CALL 5
        JP 0
        Failure:
        LD DE,Failed
        LD C,9
        CALL 5
        JP 0
        Name: DB "SEED    TXT"
        Passed: DB 13,10,"{{marker}}",13,10,"$"
        Failed: DB 13,10,"AAAA",13,10,"$"
        Fcb: DEFS 36,0
        Dma: DEFS 128,0
        """;

    private static void Boot(LiveMachine machine, Func<bool>? media = null)
    {
        Frames(machine, 250, media);
        Key(machine, HcKey.P, media); Key(machine, HcKey.Space, media);
        machine.Keyboard.SetKey(HcKey.CapsShift, true);
        machine.Keyboard.SetKey(HcKey.SymbolShift, true);
        Frames(machine, 8, media);
        machine.Keyboard.SetKey(HcKey.CapsShift, false);
        machine.Keyboard.SetKey(HcKey.SymbolShift, false);
        Frames(machine, 8, media);
        Key(machine, HcKey.L, media); Key(machine, HcKey.Space, media);
        foreach (var key in new[] { HcKey.D1, HcKey.D4, HcKey.D4, HcKey.D4, HcKey.D6, HcKey.Enter }) Key(machine, key, media);
        Until(machine, frame => HasGlyphs(frame, "A>"), "whole-chip CP/M A> prompt", media);
    }
    private static void Command(LiveMachine machine, string text, Func<bool>? media = null)
    {
        foreach (var ch in text) Key(machine, Enum.Parse<HcKey>(ch.ToString()), media);
        Key(machine, HcKey.Enter, media);
    }
    private static void Key(LiveMachine machine, HcKey key, Func<bool>? media = null)
    {
        machine.Keyboard.SetKey(key, true); Frames(machine, 8, media);
        machine.Keyboard.SetKey(key, false); Frames(machine, 8, media);
    }
    private static VideoFrame Frame(LiveMachine machine, Func<bool>? media)
    {
        if (media is null) return machine.RunFrame();
        // Same instruction-boundary contract as the App worker; a real guest ACK is
        // consumed by the existing owner before the following instruction can execute.
        Assert.True(machine.TryRunFrame(media, out var result));
        return result!.Video;
    }
    private static void Frames(LiveMachine machine, int count, Func<bool>? media = null)
    {
        for (var i = 0; i < count; i++) Frame(machine, media);
    }
    private static void Until(LiveMachine machine, Func<VideoFrame, bool> predicate, string description, Func<bool>? media = null)
    {
        for (var i = 0; i < 1500; i++)
            if (predicate(Frame(machine, media))) return;
        Assert.Fail("Did not observe " + description + " in published video.");
    }
    // Same published-pixel CP/M 4x8 glyph convention as GuiDiskAttachTests.
    private static bool HasGlyphs(VideoFrame frame, string text)
    {
        if (frame.Width != 352 || frame.Height != 296) return false;
        for (var row = 0; row < 24; row++)
            for (var col = 0; col <= 64 - text.Length; col++)
                if (text.Select((ch, i) => Glyph(frame, col + i, row, ch)).All(match => match)) return true;
        return false;
    }
    private static bool Glyph(VideoFrame frame, int col, int row, char ch)
    {
        ReadOnlySpan<byte> glyph = ch == 'A'
            ? [0x00, 0x02, 0x05, 0x05, 0x07, 0x05, 0x05, 0x00]
            : [0x00, 0x00, 0x04, 0x02, 0x01, 0x02, 0x04, 0x00];
        var pixels = frame.Pixels.Span;
        var cellX = 48 + col / 2 * 8; var cellY = 40 + row * 8;
        var counts = new Dictionary<uint, int>();
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
            {
                var color = pixels[(cellY + y) * frame.Width + cellX + x];
                counts[color] = counts.GetValueOrDefault(color) + 1;
            }
        var background = counts.MaxBy(pair => pair.Value).Key;
        for (var y = 0; y < 8; y++)
        {
            byte bits = 0;
            for (var x = 0; x < 4; x++)
                if (pixels[(cellY + y) * frame.Width + 48 + col * 4 + x] != background)
                    bits |= (byte)(0x8 >> x);
            if (bits != glyph[y]) return false;
        }
        return true;
    }
    private static byte[] Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }
}
