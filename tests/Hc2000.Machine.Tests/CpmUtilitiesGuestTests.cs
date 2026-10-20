using System.Diagnostics;
using System.Security.Cryptography;
using Hc2000.Asm;
using Hc2000.Machine;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Z80Seam = Hc2000.Machine.Tests.MachineBootAcceptanceTests.Z80Seam;
using Xunit;
using Xunit.Abstractions;

namespace Hc2000.Machine.Tests;

// The guest proves the Hc2000.Asm utilities on the real m-paged machine: the
// real builder produces the paged bootstrap disk, the repo assembler assembles
// each COM, and the guest runs them from CP/M. The only console hook observes
// the verified CONOUT site, as in TapeGuestWitnessTests.
[CollectionDefinition("cpm-utilities-guest", DisableParallelization = true)]
public sealed class CpmUtilitiesGuestCollection { }

file sealed class CpmUtilitiesAssetFactAttribute : FactAttribute
{
    public CpmUtilitiesAssetFactAttribute() => Skip = CpmUtilitiesAssetAvailability.Missing();
}

file static class CpmUtilitiesAssetAvailability
{
    public static string? Missing()
    {
        foreach (var (path, length) in new[]
        {
            ("disks/cpm/CPM22-HC.DSK", 778496),
            ("roms/hc2000/eprom-dump-hc2k-2/M27C512.bin", 65536)
        })
        {
            var full = Path.Combine(StorageAssetTests.Root(), path);
            if (!File.Exists(full) || new FileInfo(full).Length != length)
                return "Copyrighted asset missing or LFS pointer (run git lfs pull): " + path;
        }
        return null;
    }
}

[Collection("cpm-utilities-guest")]
public sealed class CpmUtilitiesGuestTests(ITestOutputHelper output)
{
    [CpmUtilitiesAssetFact]
    public void GuestRunsEveryUtilityAndTheHostSeesThePagedResult()
    {
        var boot = new GuestHarness(BuildGuestDisk());
        BootToCpm(boot);

        // RAMSTAT: the paged M: drive is present and, before any guest write,
        // 198 of its 1024 blocks are free (the m-paged BIOS install reserves the
        // rest for the CP/M window; deterministic, observed on first guest run).
        Assert.Contains("M: PRESENT\nM: FREE 198/1024\n", Command(boot, "RAMSTAT"));

        // MFMT: format M:, write the known file, and re-read it byte-exact.
        Assert.Contains("M: FORMATTED\nM: FILE VERIFY OK\n", Command(boot, "MFMT"));

        // DRVSEL: an out-of-range drive is refused with the stated reason.
        Assert.Contains("DRVSEL: G OUT OF RANGE (valid: A B C D E F M)\n", Command(boot, "DRVSEL G"));

        // DRVSEL with no argument lists every drive: A and M ready, B..F not.
        Assert.Contains(
            "A: READY\nB: NOT READY\nC: NOT READY\nD: NOT READY\nE: NOT READY\nF: NOT READY\nM: READY\n",
            Command(boot, "DRVSEL"));

        // HDFMT: refuses a drive outside C-F, refuses with no drive, and on this
        // machine (no HDD resident) refuses a present C: with the reason.
        Assert.Contains("HDFMT: G OUT OF RANGE (valid: C D E F)\n", Command(boot, "HDFMT G"));
        Assert.Contains("HDFMT: NEED DRIVE C-F\n", Command(boot, "HDFMT"));
        Assert.Contains("HDFMT: C NOT READY (HDD RESIDENT NOT READY)\n", Command(boot, "HDFMT C"));

        // Host evidence: the guest's writes landed in paged M: RAM.
        AssertGuestFile(boot.Memory);
        AssertFormattedRegion(boot.Memory);
    }

    [CpmUtilitiesAssetFact]
    public void GuestFormatsEveryHddDriveWithoutChangingAnotherDriveOrData()
    {
        var boot = new GuestHarness(BuildHddGuestDisk(), hdd: true);
        BootToCpm(boot);
        Command(boot, "HDINST");
        // The real installer, not a host patch, hooked BDOS and the BIOS.
        Assert.Equal(0x9006, boot.Memory.Read(6) | boot.Memory.Read(7) << 8);
        var originals = Enumerable.Range(2, 4).Select(unit => boot.BlockDisk!.ExportHdd((byte)unit)).ToArray();

        for (var unit = 2; unit <= 5; unit++)
        {
            var transcript = Command(boot, "HDFMT " + (char)('A' + unit));
            output.WriteLine(transcript);
            Assert.Contains("HDFMT: FORMATTED\n", transcript);
            for (var check = 2; check <= 5; check++)
            {
                var expected = (byte[])originals[check - 2].Clone();
                // DRM=1023, OFF=0: 1024 32-byte directory entries.
                if (check <= unit) expected.AsSpan(0, 32768).Fill(0xE5);
                Assert.True(expected.AsSpan().SequenceEqual(boot.BlockDisk!.ExportHdd((byte)check)),
                    $"HDFMT {(char)('A' + unit)} changed the wrong bytes on {(char)('A' + check)}:");
            }
            output.WriteLine($"{(char)('A' + unit)}: directory 32768 bytes = E5; remaining data and other drives byte-exact.");
        }
    }

    [CpmUtilitiesAssetFact]
    public void GuestRefusesAWriteProtectedHddWithoutReportingSuccessOrChangingMedia()
    {
        var boot = new GuestHarness(BuildHddGuestDisk(), hdd: true, writeProtected: true);
        BootToCpm(boot);
        Command(boot, "HDINST");
        Assert.Equal(0x9006, boot.Memory.Read(6) | boot.Memory.Read(7) << 8);
        var originals = Enumerable.Range(2, 4).Select(unit => boot.BlockDisk!.ExportHdd((byte)unit)).ToArray();
        var start = boot.ConsoleText().Length;
        boot.TypeString("HDFMT C");
        // A failed write leaves the resident's dirty cache fail-closed, so
        // warm boot need not reach a prompt. Prove the reported refusal and
        // untouched media, rather than requiring the resident to discard it.
        for (var frame = 0; frame < 500 && !boot.ConsoleText()[start..].Contains("HDFMT: WRITE FAILED"); frame++)
            boot.RunFrame();
        for (var frame = 0; frame < 30; frame++) boot.RunFrame();
        var transcript = boot.ConsoleText()[start..];
        output.WriteLine(transcript);
        Assert.Contains("HDFMT: WRITE FAILED", transcript);
        Assert.DoesNotContain("FORMATTED", transcript);
        for (byte unit = 2; unit <= 5; unit++)
            Assert.True(originals[unit - 2].AsSpan().SequenceEqual(boot.BlockDisk!.ExportHdd(unit)));
    }

    private static byte[] BuildHddGuestDisk()
    {
        var media = Dsk.Load(File.ReadAllBytes(Path.Combine(StorageAssetTests.Root(), "disks/cpm/CPM22-HC.DSK")));
        var fs = CpmFs.Open(media);
        PutUtility(fs, "HDINST", "hdd.init");
        PutUtility(fs, "HDFMT", "hdfmt");
        return media.Save();
    }

    private static void PutUtility(CpmFs fs, string command, string sourceName)
    {
        var source = Path.Combine(StorageAssetTests.Root(), "bios/src/hc2000/upgrade", sourceName + ".asm");
        var assembly = Assembler.AssembleText(File.ReadAllText(source), file: source);
        Assert.True(assembly.Success, string.Join(';', assembly.Diagnostics));
        fs.Put(command + ".COM", assembly.ToBinary());
    }

    // The real m-paged producer plus the four repo-assembled COMs on A:.
    private static byte[] BuildGuestDisk()
    {
        var root = StorageAssetTests.Root();
        var directory = Path.Combine(Path.GetTempPath(), "hc2000-cpm-utilities-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = root,
                    UseShellExecute = false
                }
            };
            foreach (var arg in new[] { "run", "--project", "tools/UpgradeBiosBuilder", "--", "m-paged", directory })
                process.StartInfo.ArgumentList.Add(arg);
            process.Start();
            if (!process.WaitForExit(240_000))
            {
                process.Kill(true);
                throw new TimeoutException("Real m-paged producer timed out.");
            }
            Assert.True(process.ExitCode == 0, $"Real m-paged producer exit={process.ExitCode}");
            var media = Dsk.Load(File.ReadAllBytes(Path.Combine(directory, "CPM22-HC-ram-paged-upgrade.DSK")));
            var fs = CpmFs.Open(media);
            foreach (var name in new[] { "ramstat", "mfmt", "drvsel", "hdfmt" })
                PutUtility(fs, name.ToUpperInvariant(), name);
            return media.Save();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    // MFMT wrote TEST.TST, 3 consecutive 128B records, record byte j = j^0x53.
    // That exact 384B pattern must appear in exactly one place in the paged
    // RAM: the file's allocation (the 128B DMA buffer never holds all three
    // records at once).
    private static void AssertGuestFile(HcMemory memory)
    {
        var ram = memory.Ram.Span;
        var expected = new byte[384];
        for (var j = 0; j < expected.Length; j++)
            expected[j] = (byte)((j % 128) ^ 0x53);
        var occurrences = 0;
        for (var offset = 0; offset + expected.Length <= ram.Length; offset++)
        {
            if (ram[offset] != 0x53 || ram[offset + 1] != 0x52) continue;
            if (ram.Slice(offset, expected.Length).SequenceEqual(expected))
            {
                occurrences++;
                if (occurrences > 1) break;
            }
        }
        Assert.Equal(1, occurrences);
    }

    // MFMT filled track 0 sectors 0..63 with 0xE5 (bank 0, physical 0x10000).
    // BDOS then wrote the TEST.TST entry into sector 0, so the untouched run
    // is sectors 1..63: 8064 bytes at 0x10080. The entry itself is user 0
    // plus the 11-byte name at the start of that bank.
    private static void AssertFormattedRegion(HcMemory memory)
    {
        var ram = memory.Ram.Span;
        const int entry = 0x10000;
        const int fill = 0x10080;
        const int fillLength = 63 * 128;
        Assert.Equal(0, ram[entry]);
        Assert.Equal("TEST    TST"u8.ToArray(), ram.Slice(entry + 1, 11).ToArray());
        Assert.True(ram.Slice(fill, fillLength).IndexOfAnyExcept((byte)0xE5) < 0,
            "Expected the 8064-byte 0xE5 directory fill at 0x10080.");
    }

    private static void BootToCpm(GuestHarness boot)
    {
        Assert.True(GuestHarness.ReachesEditor(boot), "BASIC editor not reached.");
        for (var f = 0; f < 30; f++) boot.RunFrame();
        boot.TypeKey(HcKey.P);
        boot.TypeKey(HcKey.Space);
        boot.TypeExtendedChord();
        boot.TypeKey(HcKey.L);
        boot.TypeKey(HcKey.Space);
        foreach (var key in new[] { HcKey.D1, HcKey.D4, HcKey.D4, HcKey.D4, HcKey.D6 })
            boot.TypeKey(key);
        boot.TypeKey(HcKey.Enter);
        boot.ArmConsoleHook();
        Assert.True(boot.RunsToPrompt(),
            $"CP/M prompt not reached: {boot.ConsoleText()} PC={boot.Cpu.Cpu.R.PC:X4}");
    }

    // Type an uppercase CP/M command and press Enter; return the console
    // output produced by the command. Runs of newlines are collapsed to one:
    // the CONOUT console hook reports the asm's \r\n as several bytes, and the
    // exact count is not the point of these assertions.
    private static string Command(GuestHarness boot, string command)
    {
        var start = boot.ConsoleText().Length;
        boot.TypeString(command);
        Assert.True(boot.RunsToPrompt(start),
            $"No prompt after {command}: {boot.ConsoleText()} PC={boot.Cpu.Cpu.R.PC:X4}");
        var text = boot.ConsoleText()[start..];
        var sb = new System.Text.StringBuilder();
        var inNewlines = false;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                if (inNewlines) continue;
                inNewlines = true;
            }
            else inNewlines = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private sealed class GuestHarness
    {
        public long MasterTick => machine.MasterTick;
        public HcMemory Memory { get; }
        public Z80Seam Cpu { get; }
        public KeyboardMatrix Keyboard { get; }
        public BlockDiskPort? BlockDisk { get; }
        public readonly List<byte> ConsoleOutput = new();
        private bool _consoleHookArmed;
        private readonly Machine machine;
        private readonly Z80Seam cpu;

        public GuestHarness(byte[] diskImage, bool hdd = false, bool writeProtected = false)
        {
            var root = StorageAssetTests.Root();
            var profile = hdd ? MachineProfile.Hc2000V2HddGuestV1 : MachineProfile.Hc2000V2PagedMTransferV1;
            profile.Validate();

            var chip = File.ReadAllBytes(Path.Combine(root, "roms/hc2000/eprom-dump-hc2k-2/M27C512.bin"));
            Assert.Equal("83E3213B049C5B849EE32E04DA901EBA06992D5AA1F44F76564F312C79748D2B",
                Convert.ToHexString(SHA256.HashData(chip)));
            var basic = chip[0x8000..0xC000];
            var cpm = chip[0xC000..0x10000];

            var mapper = CpuTimeMapper.FromProfile(profile);
            var scheduler = new DeviceScheduler();
            var clock = new FrameClock(scheduler, 1);
            var adapter = new CpuDeviceTimeAdapter(scheduler, mapper);
            Memory = new HcMemory(profile.Memory with
            {
                RomProvenance =
                    $"repository ROMs, {profile.Name} (canonical hashes per MANIFEST.sha256); disk CPM22-HC.DSK plus repo-assembled utility COMs"
            }, basic, cpm, chip[0x4000..0x8000]);

            Keyboard = new KeyboardMatrix();
            var ula = new Ula(Memory, Keyboard, scheduler, clock, 2, profile.Video);
            var floppy = new FloppyController(scheduler, 100,
                new FloppyDrive(new DriveProfile(80, 2), Dsk.Load(diskImage)), new FloppyDrive(new()),
                instant: false);

            var idle = new PortDevice();
            var driveLatch = new PortDevice(
                read: (_, tick) => floppy.ReadLatch(),
                write: (_, v, tick) => floppy.WriteLatch(v));
            var fdcStatus = new PortDevice(read: (_, tick) => floppy.Fdc.ReadStatus());
            var fdcData = new PortDevice(
                read: (_, tick) => floppy.Fdc.ReadData(),
                write: (_, v, tick) => floppy.Fdc.WriteData(v));

            if (hdd)
            {
                BlockDisk = new BlockDiskPort(Memory, floppy, hddEnabled: true, guestAckEnabled: true);
                for (byte unit = 2; unit <= 5; unit++)
                {
                    var image = new byte[8_388_608];
                    for (var i = 0; i < image.Length; i++) image[i] = (byte)(unit * 31 + i % 251);
                    BlockDisk.AttachHdd(unit, image, writeProtected);
                }
            }
            var mappings = hdd
                ? HcIoProfile.WithBlockDisk(profile, Memory, ula, driveLatch, fdcStatus, fdcData, idle, idle, BlockDisk!)
                : HcIoProfile.WithPagedRam(Memory, ula, driveLatch, fdcStatus, fdcData, idle, idle);
            var ports = new PortDecoder(mappings);
            var bus = new HcMachineBus(Memory, ports, adapter, scheduler);
            cpu = new Z80Seam(bus);
            Cpu = cpu;
            clock.InterruptChanged += (_, asserted) => cpu.Cpu.IntLine = asserted;

            machine = new Machine(scheduler, clock, adapter, ula,
                () => cpu.Step(), () => cpu.TStates);
            machine.AttachSnapshotState(profile, clock, Memory, mapper, cpu, 2, floppy);
        }

        // Instruction-boundary observation only; no ROM/PC/BDOS substitution.
        public bool RunFrame() => machine.TryRunFrame(Boundary, out _);

        private bool Boundary()
        {
            if (_consoleHookArmed && cpu.Cpu.R.PC == 0xE107)
                ConsoleOutput.Add(cpu.Cpu.R.C);
            return true;
        }

        public void ArmConsoleHook() => _consoleHookArmed = true;

        public string ConsoleText()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var b in ConsoleOutput)
            {
                if (b is >= 32 and < 127) sb.Append((char)b);
                else if (b is 0x0D or 0x0A) sb.Append('\n');
                else sb.Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        public void TypeString(string text)
        {
            foreach (var c in text)
            {
                if (c is ':' or '.' or '=')
                {
                    Keyboard.SetKey(HcKey.SymbolShift, true);
                    TypeKey(c == ':' ? HcKey.Z : c == '.' ? HcKey.M : HcKey.L);
                    Keyboard.SetKey(HcKey.SymbolShift, false);
                    RunFrame();
                }
                else TypeKey(ChToKey(c));
            }
            TypeKey(HcKey.Enter);
        }

        public void TypeKey(HcKey key, int frames = 8)
        {
            Keyboard.SetKey(key, true);
            for (var f = 0; f < frames; f++) RunFrame();
            Keyboard.SetKey(key, false);
            for (var f = 0; f < frames; f++) RunFrame();
        }

        public void TypeExtendedChord(int frames = 8)
        {
            Keyboard.SetKey(HcKey.CapsShift, true);
            Keyboard.SetKey(HcKey.SymbolShift, true);
            for (var f = 0; f < frames; f++) RunFrame();
            Keyboard.SetKey(HcKey.CapsShift, false);
            Keyboard.SetKey(HcKey.SymbolShift, false);
            for (var f = 0; f < frames; f++) RunFrame();
        }

        private static HcKey ChToKey(char ch) => ch switch
        {
            ' ' => HcKey.Space,
            '\n' or '\r' => HcKey.Enter,
            '0' => HcKey.D0, '1' => HcKey.D1, '2' => HcKey.D2,
            '3' => HcKey.D3, '4' => HcKey.D4, '5' => HcKey.D5,
            '6' => HcKey.D6, '7' => HcKey.D7, '8' => HcKey.D8, '9' => HcKey.D9,
            'A' => HcKey.A, 'B' => HcKey.B, 'C' => HcKey.C, 'D' => HcKey.D,
            'E' => HcKey.E, 'F' => HcKey.F, 'G' => HcKey.G, 'H' => HcKey.H,
            'I' => HcKey.I, 'J' => HcKey.J, 'K' => HcKey.K, 'L' => HcKey.L,
            'M' => HcKey.M, 'N' => HcKey.N, 'O' => HcKey.O, 'P' => HcKey.P,
            'Q' => HcKey.Q, 'R' => HcKey.R, 'S' => HcKey.S, 'T' => HcKey.T,
            'U' => HcKey.U, 'V' => HcKey.V, 'W' => HcKey.W, 'X' => HcKey.X,
            'Y' => HcKey.Y, 'Z' => HcKey.Z,
            _ => throw new ArgumentOutOfRangeException(nameof(ch), $"Unmapped character '{ch}'"),
        };

        /// <summary>Editor in the settled prompt state (MODE 0, pointer near workspace top) for 3 frames.</summary>
        public bool EditorReady()
        {
            if (Memory.Read(0x5C41) != 0) return false;
            int eLine = Memory.Read(0x5C59) | (Memory.Read(0x5C5A) << 8);
            int wsp = Memory.Read(0x5C61) | (Memory.Read(0x5C62) << 8);
            if (eLine is < 0x4000 or >= 0x6000) return false;
            if (wsp is < 0x4000 or >= 0x6000) return false;
            var gap = wsp - eLine;
            return gap is >= 1 and <= 32;
        }

        public static bool ReachesEditor(GuestHarness boot, int maxFrames = 1000)
        {
            var lastTick = -1L;
            var stable = 0;
            for (var frame = 0; frame < maxFrames; frame++)
            {
                boot.RunFrame();
                if (!boot.EditorReady())
                {
                    stable = 0;
                    continue;
                }
                stable++;
                if (stable >= 3 && boot.MasterTick != lastTick) return true;
                lastTick = boot.MasterTick;
            }
            return false;
        }

        /// <summary>
        /// Run frames until the CP/M prompt appears in console output from sinceChars onward
        /// (10 emulated seconds). sinceChars skips output already consumed by an earlier wait.
        /// </summary>
        public bool RunsToPrompt(int sinceChars = 0, int maxFrames = 4000)
        {
            var deadline = MasterTick + 10L * FrameTiming.MasterHz;
            for (var f = 0; f < maxFrames && MasterTick < deadline; f++)
            {
                RunFrame();
                if (PromptAppeared(sinceChars)) return true;
            }
            return PromptAppeared(sinceChars);
        }

        private bool PromptAppeared(int sinceChars)
        {
            var text = ConsoleText();
            return text.Length > sinceChars && text[sinceChars..].Contains("A>");
        }
    }
}
