using System.Buffers.Binary;
using Hc2000.Asm;
using Hc2000.Machine;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Snapshots;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Hc2000.Z80;
using Z80Seam = Hc2000.Machine.Tests.MachineBootAcceptanceTests.Z80Seam;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>
/// The sole genuine guest / held-owner witness for the native HDD guest-ack boundary (KB
/// hc2000-hdd-guest-ack-native-producer, P7): one real machine run where the real guest
/// (CP/M + the A/B-loaded resident + the real COM + real 0x89 doorbells) drives the whole
/// correlated switch against the real native receiver, while the owner holds the CPU:
/// HDINST patches BDOS at 0x9006 and identifies C-F; the owner begins one primary-2 switch
/// with the captured slot generation; the guest's HDISWP re-queries, matches the static
/// challenge and ACKs, leaving the CPU held in the receiver; the owner then commits fresh
/// media and finishes; the guest resumes, observes the Committed terminal through its own
/// doorbell, and returns to the real CP/M prompt. No guest or owner side is faked: the guest
/// is the firmware on the real machine and every state change goes through the owner
/// boundary or a real doorbell. Refusal paths (0x0d stale generation, 0x0e) are qualified
/// separately at the raw-doorbell protocol/boundary level and are not re-implemented here.
/// </summary>
public sealed class HddGuestWitnessTests
{
    private const int ImageBytes = 8_388_608; // 16384 sectors x 512

    /// <summary>Deterministic 8MB pattern, one per unit seed.</summary>
    private static byte[] Pattern(int seed)
    {
        var image = new byte[ImageBytes];
        for (long i = 0; i < image.Length; i++)
            image[(int)i] = (byte)((seed * 7919 + (i % 65536) * 31) & 0xFF);
        return image;
    }

    /// <summary>
    /// The HC CP/M loads a COM verbatim at 0x0100 and jumps there; the canonical disk COMs
    /// (CLS.COM = 1E 1A..., PIP.COM = C3 CE 04 C9) are headerless, so the 0x0100-absolute
    /// code goes to disk exactly as the assembler emitted it.
    /// </summary>
    private static byte[] Com(AssemblyResult code)
    {
        var payload = code.ToBinary();
        Assert.True(payload.Length is > 0 and <= 0x8FFF, $"COM payload is {payload.Length} bytes.");
        return payload;
    }

    private static AssemblyResult Assemble(string name)
    {
        var path = Path.Combine(StorageAssetTests.Root(), "bios/src/hc2000/upgrade", name + ".asm");
        var result = Assembler.AssembleText(File.ReadAllText(path), file: path);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        return result;
    }

    /// <summary>CPM22-HC.DSK plus the two resident COMs at user 0; the canonical disk is untouched.</summary>
    private static byte[] WitnessDisk()
    {
        var root = StorageAssetTests.Root();
        var media = Dsk.Load(File.ReadAllBytes(Path.Combine(root, "disks/cpm/CPM22-HC.DSK")));
        var fs = CpmFs.Open(media);
        fs.Put("HDINST.COM", Com(Assemble("hdd.init")));
        fs.Put("HDISWP.COM", Com(Assemble("hdd.swap")));
        return media.Save();
    }

    /// <summary>
    /// Real-machine composition for the HddGuestV1 profile: faithful Z80 + v2 ROMs + 8272 FDC +
    /// port decode, with the blockport built as the guest-ack receiver and every 0x81/0x83/0x89
    /// access wrapped so the guest's doorbells are witnessed. The CPU may not run while the
    /// receiver holds a completed switch (the owner boundary is the only way to release it).
    /// </summary>
    private sealed class WitnessHarness
    {
        public long MasterTick => machine.MasterTick;
        public HcMemory Memory { get; }
        public Z80Seam Cpu { get; }
        public KeyboardMatrix Keyboard { get; }
        public BlockDiskPort BlockDisk { get; }

        /// <summary>Guest doorbells in order: (operation, unit, receiver status).</summary>
        public readonly List<(byte Op, byte Unit, byte Status)> Doorbells = new();
        public bool GuestSwitchHoldsCpu => BlockDisk.GuestSwitchHoldsCpu;

        public readonly List<byte> ConsoleOutput = new();
        private bool _consoleHookArmed;
        private readonly Machine machine;
        private readonly Z80Seam cpu;
        private byte ptrLo, ptrHi;

        public WitnessHarness(byte[] diskImage)
        {
            var root = StorageAssetTests.Root();
            var profile = MachineProfile.Hc2000V2HddGuestV1;
            profile.Validate();

            var basic = LoadRomFile("v2/basic.rom");
            var cpm = new byte[0x4000];
            var upper = File.ReadAllBytes(Path.Combine(root, "roms/hc2000/v2/cpm-hc2kzip.bin"));
            if (upper.Length != 0x2000) throw new InvalidDataException("v2 cpm-hc2kzip.bin must be 0x2000.");
            Buffer.BlockCopy(upper, 0, cpm, 0x2000, 0x2000);

            var mapper = CpuTimeMapper.FromProfile(profile);
            var scheduler = new DeviceScheduler();
            var clock = new FrameClock(scheduler, 1);
            var adapter = new CpuDeviceTimeAdapter(scheduler, mapper);
            Memory = new HcMemory(profile.Memory with
                { RomProvenance = $"repository ROMs, {profile.Name} (canonical hashes per MANIFEST.sha256); disk CPM22-HC.DSK plus resident COMs" },
                basic, cpm, LoadRomFile("v2/if1.rom"));

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

            BlockDisk = new BlockDiskPort(Memory, floppy, hddEnabled: true, guestAckEnabled: true);
            for (var unit = 2; unit <= 5; unit++)
                BlockDisk.AttachHdd((byte)unit, Pattern(unit));

            var baseMappings = HcIoProfile.WithBlockDisk(profile, Memory, ula, driveLatch, fdcStatus, fdcData, idle, idle, BlockDisk);
            var mappings = baseMappings.Select(mapping => new PortMapping(mapping.Match, mapping.Mask,
                new PortDevice(
                    read: (port, tick) => mapping.Device.Read(port, tick),
                    write: (port, value, tick) =>
                    {
                        switch (port & 0xFF)
                        {
                            case 0x81: ptrLo = (byte)value; break;
                            case 0x83: ptrHi = (byte)value; break;
                        }
                        mapping.Device.Write(port, value, tick);
                        if ((port & 0xFF) == 0x89)
                        {
                            var ptr = (ushort)(ptrLo | (ptrHi << 8));
                            Doorbells.Add((Memory.Read((ushort)(ptr + 1)),
                                Memory.Read((ushort)(ptr + 2)),
                                mapping.Device.Read(0x89, tick)));
                        }
                    }),
                mapping.Access)).ToArray();
            var ports = new PortDecoder(mappings);

            var bus = new HcMachineBus(Memory, ports, adapter, scheduler);
            cpu = new Z80Seam(bus);
            Cpu = cpu;
            clock.InterruptChanged += (_, asserted) => cpu.Cpu.IntLine = asserted;

            machine = new Machine(scheduler, clock, adapter, ula,
                () => cpu.Step(), () => cpu.TStates);
            machine.AttachSnapshotState(profile, clock, Memory, mapper, cpu, 2, floppy);
        }

        private static byte[] LoadRomFile(string relative)
        {
            var bytes = File.ReadAllBytes(Path.Combine(StorageAssetTests.Root(), "roms", "hc2000",
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (bytes.Length != 0x4000) throw new InvalidDataException($"roms/hc2000/{relative} is {bytes.Length} bytes; a faithful half must be 0x4000.");
            return bytes;
        }

        /// <summary>One whole frame; the CPU does not step past the receiver's hold (owner release only).</summary>
        public bool RunFrame() => machine.TryRunFrame(Boundary, out _);

        private bool Boundary()
        {
            if (_consoleHookArmed && cpu.Cpu.R.PC == 0xE107)
                ConsoleOutput.Add(cpu.Cpu.R.C);
            if (_watchArmed)
            {
                var pc = cpu.Cpu.R.PC;
                if (pc is >= 0x0100 and <= 0x01FF && _watch.Count < 2000)
                    _watch.TryAdd(pc, string.Join("", Memory.Read((ushort)pc).ToString("X2")) +
                        string.Join("", Enumerable.Range(1, 2).Select(i => Memory.Read((ushort)(pc + i)).ToString("X2"))));
            }
            return !BlockDisk.GuestSwitchHoldsCpu;
        }

        public void ArmConsoleHook() => _consoleHookArmed = true;

        private readonly Dictionary<int, string> _watch = new();
        private bool _watchArmed;

        public void ArmWatch() => _watchArmed = true;

        public string WatchTrace()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in _watch.OrderBy(k => k.Key))
                sb.Append($" ");
            return sb.ToString();
        }

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

        /// <summary>Type an uppercase CP/M command and press Enter.</summary>
        public void TypeString(string text)
        {
            foreach (var c in text) TypeKey(ChToKey(c));
            TypeKey(HcKey.Enter);
        }

        /// <summary>Run frames until the receiver holds the CPU (10 emulated seconds).</summary>
        public bool RunsToHeld(int maxFrames = 4000)
        {
            var deadline = MasterTick + 10L * FrameTiming.MasterHz;
            for (var f = 0; f < maxFrames && MasterTick < deadline; f++)
            {
                RunFrame();
                if (GuestSwitchHoldsCpu) return true;
            }
            return GuestSwitchHoldsCpu;
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

        public static bool ReachesEditor(WitnessHarness boot, int maxFrames = 1000)
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

    [Fact]
    public void GenuineGuestAndHeldOwnerCompleteTheSwitchAndResumeOnFreshMedia()
    {
        var boot = new WitnessHarness(WitnessDisk());

        // --- The real guest boots: BASIC copyright screen, then warm boot into CP/M. ---
        Assert.True(WitnessHarness.ReachesEditor(boot), "BASIC editor not reached.");
        for (var f = 0; f < 30; f++) boot.RunFrame();
        boot.TypeKey(HcKey.P);
        boot.TypeKey(HcKey.Space);
        boot.TypeExtendedChord();
        boot.TypeKey(HcKey.L);
        boot.TypeKey(HcKey.Space);
        foreach (var key in new[] { HcKey.D1, HcKey.D4, HcKey.D4, HcKey.D4, HcKey.D6 }) boot.TypeKey(key);
        boot.TypeKey(HcKey.Enter);
        boot.ArmConsoleHook();
        Assert.True(boot.RunsToPrompt(), $"CP/M prompt not reached: {boot.ConsoleText()}");

        // --- HDINST: the resident patches BDOS and identifies C-F through its own doorbells. ---
        var instMarker = boot.ConsoleText().Length;
        boot.TypeString("HDINST");
        Assert.True(boot.RunsToPrompt(sinceChars: instMarker), $"CP/M prompt not reached after HDINST: {boot.ConsoleText()}");

        Assert.True(boot.Doorbells.Count == 5, $"doorbells={boot.Doorbells.Count} console=<{boot.ConsoleText()}> preflight 0005={boot.Memory.Read((ushort)0x0005):X2} 0006={boot.Memory.Read((ushort)0x0006):X2} 9000={string.Join("", Enumerable.Range(0x9000, 16).Select(a => boot.Memory.Read((ushort)a).ToString("X2")))} 0100={string.Join("", Enumerable.Range(0x0100, 16).Select(a => boot.Memory.Read((ushort)a).ToString("X2")))}");
        Assert.Equal(new byte[] { 4, 3, 3, 3, 3 }, boot.Doorbells.Select(d => d.Op).ToArray());
        Assert.Equal(new byte[] { 2, 2, 3, 4, 5 }, boot.Doorbells.Select(d => d.Unit).ToArray());
        Assert.All(boot.Doorbells, d => Assert.Equal(0, d.Status));

        // The resident patched the BDOS hook: the word at 0x0006 is now 0x9006.
        Assert.Equal(0x06, boot.Memory.Read(0x0006));
        Assert.Equal(0x90, boot.Memory.Read(0x0007));

        // --- The owner begins the correlated switch from the captured slot generations. ---
        var originals = new Dictionary<byte, byte[]> { [2] = Pattern(2), [3] = Pattern(3), [4] = Pattern(4), [5] = Pattern(5) };
        boot.BlockDisk.BindGuestAckSession(Guid.NewGuid());
        var request = boot.BlockDisk.BeginGuestSwitch(2, 4);
        Assert.Equal(1u, request.CGeneration);
        Assert.Equal(1u, boot.BlockDisk.CaptureHdd(2).Generation);

        // --- HDISWP: the guest re-queries, matches the static challenge and ACKs (op5). ---
        var heldMarker = boot.ConsoleText().Length;
        boot.TypeString("HDISWP");
        Assert.True(boot.RunsToHeld(), $"receiver did not hold the CPU: {boot.ConsoleText()}");

        Assert.Equal(8, boot.Doorbells.Count);
        Assert.Equal(new byte[] { 4, 4, 5 }, boot.Doorbells.Skip(5).Select(d => d.Op).ToArray());
        Assert.Equal(new byte[] { 2, 2, 2 }, boot.Doorbells.Skip(5).Select(d => d.Unit).ToArray());
        Assert.All(boot.Doorbells, d => Assert.Equal(0, d.Status));

        Assert.True(boot.BlockDisk.TryGetHeldGuestSwitch(out var ticket));

        // --- Falsifier: while held, neither more frames nor more doorbells change anything; ---
        // --- only the owner boundary can release the CPU. ---
        for (var f = 0; f < 20; f++) boot.RunFrame();
        Assert.Equal(8, boot.Doorbells.Count);
        Assert.True(boot.BlockDisk.GuestSwitchHoldsCpu);

        // --- The owner commits fresh media and releases the guest. ---
        var fresh = Pattern(9);
        var commit = boot.BlockDisk.CommitGuestSwitch(ticket, fresh, false, false);
        Assert.True(commit.Committed, commit.Error);
        boot.BlockDisk.FinishGuestSwitch(ticket);

        // --- The guest resumes on the committed media and returns to the real CP/M prompt. ---
        Assert.True(boot.RunsToPrompt(sinceChars: heldMarker), $"CP/M prompt not reached after resume: {boot.ConsoleText()}");

        Assert.Equal(10, boot.Doorbells.Count);
        Assert.Equal(new byte[] { 4, 3, 3, 3, 3, 4, 4, 5, 4, 3 },
            boot.Doorbells.Select(d => d.Op).ToArray());
        Assert.Equal(new byte[] { 2, 2, 3, 4, 5, 2, 2, 2, 2, 2 },
            boot.Doorbells.Select(d => d.Unit).ToArray());
        Assert.All(boot.Doorbells, d => Assert.Equal(0, d.Status));

        Assert.True(boot.BlockDisk.ExportHdd(2).AsSpan().SequenceEqual(fresh), "C is not the committed image");
        Assert.True(boot.BlockDisk.ExportHdd(3).AsSpan().SequenceEqual(originals[3]), "D changed");
        Assert.True(boot.BlockDisk.ExportHdd(4).AsSpan().SequenceEqual(originals[4]), "E changed");
        Assert.True(boot.BlockDisk.ExportHdd(5).AsSpan().SequenceEqual(originals[5]), "F changed");
        Assert.Contains("A>", boot.ConsoleText());
    }
}
