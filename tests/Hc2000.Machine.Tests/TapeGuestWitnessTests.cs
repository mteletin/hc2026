using System.Collections.Immutable;
using Hc2000.Machine;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Z80Seam = Hc2000.Machine.Tests.MachineBootAcceptanceTests.Z80Seam;
using Xunit;

namespace Hc2000.Machine.Tests;

// Native ROM + floppy CP/M + keyboard commands + assembled installer + real paged
// session. The only console hook observes entry fetch at the verified CONOUT site.
[Collection("tape-paged-session")]
public sealed class TapeGuestWitnessTests
{
    private static byte[] Volume()
    {
        var bytes = new byte[65536];
        Array.Fill(bytes, (byte)0xe5);
        // Independent user-0 directory record: block2, one 128B record.
        bytes.AsSpan(0, 32).Clear();
        "READ    TXT"u8.CopyTo(bytes.AsSpan(1));
        bytes[15] = 1; bytes[16] = 2;
        bytes.AsSpan(8192, 4096).Fill(0x1a);
        "TAPE INPUT OK\r\n"u8.CopyTo(bytes.AsSpan(8192));
        return bytes;
    }

    private static ImmutableArray<ImmutableArray<byte>> InitialTape()
    {
        var plan = new TapeCatalogCodec().PlanAppend(new(TapeSide.A, []), null,
            new("VOL1.DSK", ImmutableArray.CreateRange(Volume()), false), new(1000000, 100, true), new());
        Assert.True(plan.Success, plan.Failure?.Detail);
        return plan.Value.AppendedBlocks;
    }

    private static void Insert(TapeTransport tape, ImmutableArray<ImmutableArray<byte>> blocks)
    {
        using var wire = new MemoryStream();
        foreach (var block in blocks)
        {
            wire.WriteByte((byte)block.Length); wire.WriteByte((byte)(block.Length >> 8)); wire.Write(block.AsSpan());
        }
        var read = new TapCodec().Read(wire.ToArray(), new(TapeFormat.Tap, TapeSide.A, TapeCapacity.C60));
        Assert.True(read.Success, read.Failure?.Detail);
        tape.Deck.Blank(TapeCapacity.C60, true);
        var blank = tape.Deck.Export(TapeSide.B).Value.Image;
        Assert.True(tape.Deck.Insert(new(TapeCapacity.C60, read.Value.Image, blank, read.Value.SourceAssets), true).Success);
    }

    private static byte[] WitnessDisk(TapeVolumeSession session)
    {
        var media = Dsk.Load(File.ReadAllBytes(Path.Combine(StorageAssetTests.Root(), "disks/cpm/CPM22-HC.DSK")));
        var fs = CpmFs.Open(media);
        fs.Put("TAPEINIT.COM", TapeGuestInstaller.Bind(TapeGuestBiosTests.Assemble().ToBinary(), session));
        // Genuine guest probe: select the bound bank, enter the native CONOUT,
        // then restore bank zero before handing control back to the native CCP.
        var probe = Hc2000.Asm.Assembler.AssembleText($"""
            ORG 0x0100
            LD SP,0x7EF0
            LD BC,0x7EFD
            LD A,{session.FirstPage & 255}
            OUT (C),A
            LD BC,0x7DFD
            LD A,{session.FirstPage >> 8}
            OUT (C),A
            LD C,33
            CALL 0xE107
            XOR A
            LD BC,0x7EFD
            OUT (C),A
            LD BC,0x7DFD
            OUT (C),A
            JP 0
            """);
        Assert.True(probe.Success, string.Join(';', probe.Diagnostics));
        fs.Put("CONPROBE.COM", probe.ToBinary());
        return media.Save();
    }

    private sealed class WitnessHarness
    {
        public long MasterTick => machine.MasterTick;
        public HcMemory Memory { get; }
        public Z80Seam Cpu { get; }
        public KeyboardMatrix Keyboard { get; }
        public TapeTransport Tape { get; }
        public TapeVolumeSession Session { get; } = null!;
        public readonly List<string> BootTrace = new();
        private PagingLatches previousLatches;


        public readonly List<byte> ConsoleOutput = new();
        private bool _consoleHookArmed;
        private readonly Machine machine;
        private readonly Z80Seam cpu;
        public Action? AtComEntry { get; set; }
        private bool observeInstalledConsole;
        public bool BoundBankConsoleObserved { get; private set; }

        public WitnessHarness(bool combined = true, ImmutableArray<ImmutableArray<byte>>? blocks = null)
        {
            var root = StorageAssetTests.Root();
            var profile = combined ? MachineProfile.Hc2000V2PagedTapeV2 : MachineProfile.Hc2000V2;
            profile.Validate();

            var chip = File.ReadAllBytes(Path.Combine(root, "roms/hc2000/eprom-dump-hc2k-2/M27C512.bin"));
            Assert.Equal("83E3213B049C5B849EE32E04DA901EBA06992D5AA1F44F76564F312C79748D2B",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(chip)));
            var basic = chip[0x8000..0xc000];
            var cpm = chip[0xc000..0x10000];

            var mapper = CpuTimeMapper.FromProfile(profile);
            var scheduler = new DeviceScheduler();
            var clock = new FrameClock(scheduler, 1);
            var adapter = new CpuDeviceTimeAdapter(scheduler, mapper);
            Memory = new HcMemory(profile.Memory with
                { RomProvenance = $"repository ROMs, {profile.Name} (canonical hashes per MANIFEST.sha256); disk CPM22-HC.DSK plus resident COMs" },
                basic, cpm, chip[0x4000..0x8000]);

            Keyboard = new KeyboardMatrix();
            var ula = new Ula(Memory, Keyboard, scheduler, clock, 2, profile.Video);
            Tape = new(scheduler, ula, 200);
            Insert(Tape, blocks ?? InitialTape());
            var diskImage = File.ReadAllBytes(Path.Combine(root, "disks/cpm/CPM22-HC.DSK"));
            if (combined)
            {
                Session = new(profile, Tape, Memory);
                Assert.True(Session.Mount("VOL1.DSK", 'G').Success);
                diskImage = WitnessDisk(Session);
                // Prepare the COM on a private disk, but boot the original system
                // with no bound paging window. Bind only after native A>.
                Session.Cancel();
            }
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

            var control = new TapeControlPort();
            control.Attach(Tape);
            var mappings = combined
                ? HcIoProfile.WithTapeControl(profile, Memory, ula, driveLatch, fdcStatus, fdcData, idle, idle, control)
                : HcIoProfile.Faithful(Memory, ula, driveLatch, fdcStatus, fdcData, idle, idle);
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

        // Instruction-boundary observation only; no ROM/PC/BDOS substitution.
        public bool RunFrame() => machine.TryRunFrame(Boundary, out _);

        private bool Boundary()
        {
            if (cpu.Cpu.R.PC == 0x0100 && AtComEntry is { } action)
            {
                AtComEntry = null;
                ObserveConsoleVector("before Bind");
                action();
                ObserveConsoleVector("after Bind");
                observeInstalledConsole = true;
            }
            if (cpu.Cpu.R.PC == 0xE13F && Session is not null
                && Session.Mounted && Memory.Latches.PagedBank == Session.FirstPage)
            {
                var location = Memory.Pages[0xDB0D >> 13].Resolve(0xDB0D & 0x1fff);
                Assert.Equal(0xDB0D, location.Offset);
                Assert.Equal(Memory.Ram.Span[0xDB0D], Memory.Read(0xDB0D));
                BoundBankConsoleObserved = true;
            }
            // Observe the genuine ROM's first CONOUT dispatch after installation.
            // E13F is LD HL,(DB0D), after OUT C5 selected the BASIC window.
            if (observeInstalledConsole && cpu.Cpu.R.PC == 0xE13F)
            {
                ObserveConsoleVector("ROM CONOUT E13F");
                observeInstalledConsole = false;
            }
            if (Memory.Latches != previousLatches && BootTrace.Count < 300)
            {
                BootTrace.Add($"{cpu.Cpu.R.PC:X4}: {Memory.Latches}");
                previousLatches = Memory.Latches;
            }
            if (cpu.Cpu.R.PC is 0x8007 or 0x800a or 0xe070 && BootTrace.Count < 40)
            {
                var sp = cpu.Cpu.R.SP;
                BootTrace.Add($"{cpu.Cpu.R.PC:X4}:{Memory.Read(cpu.Cpu.R.PC):X2} SP={sp:X4} "
                    + $"stack={Memory.Read(sp):X2},{Memory.Read((ushort)(sp + 1)):X2} "
                    + $"native800A={Memory.Ram.Span[0x800a]:X2} "
                    + $"paged800A={(Memory.Ram.Length > 0x1000a ? Memory.Ram.Span[0x1000a] : 0):X2}");
            }
            if (_consoleHookArmed && cpu.Cpu.R.PC == 0xE107)
                ConsoleOutput.Add(cpu.Cpu.R.C);
            return true;
        }

        private void ObserveConsoleVector(string point)
        {
            var location = Memory.Pages[0xDB0D >> 13].Resolve(0xDB0D & 0x1fff);
            var mapped = Memory.Read(0xDB0D) | Memory.Read(0xDB0E) << 8;
            var native = Memory.Ram.Span[0xDB0D] | Memory.Ram.Span[0xDB0E] << 8;
            BootTrace.Add($"{point}: DB0D={mapped:X4} at {location.Store}[{location.Offset:X}], "
                + $"native RAM[DB0D]={native:X4}; {Memory.Latches}");
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

        /// <summary>Type an uppercase CP/M command and press Enter.</summary>
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenuineGuestListsAndReadsTheMountedTapeDrive(bool combined)
    {
        var savedBlocks = RunGuestWriteAndUnmount(combined);
        if (combined)
        {
            // Each paged machine owns a GiB. End the first machine's lifetime
            // before creating the independent cold-boot reload witness.
            GC.Collect();
            ReloadAndRead(savedBlocks);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static ImmutableArray<ImmutableArray<byte>> RunGuestWriteAndUnmount(bool combined)
    {
        var boot = new WitnessHarness(combined);
        BootToCpm(boot);
        Assert.Contains("PIP", Command(boot, "DIR A:"));
        if (!combined) return [];
        Install(boot);
        Assert.Contains("!", Command(boot, "CONPROBE"));
        Assert.True(boot.BoundBankConsoleObserved);
        Assert.Contains("READ", Command(boot, "DIR G:"));
        Assert.Contains("TAPE INPUT OK", Command(boot, "TYPE G:READ.TXT"));
        Command(boot, "PIP G:NEW.TXT=G:READ.TXT");
        Assert.True(boot.Session.Dirty);
        Assert.Contains("NEW", Command(boot, "DIR G:"));
        Assert.Contains("TAPE INPUT OK", Command(boot, "TYPE G:NEW.TXT"));
        var expected = Enumerable.Range(0, boot.Session.ImageBytes).Select(i => boot.Session.Read((ulong)i)).ToArray();
        Assert.True(boot.Session.Writeback().Success);
        Assert.False(boot.Session.Dirty);
        Assert.True(boot.Session.Unmount().Success);
        var start = boot.ConsoleText().Length;
        boot.TypeString("DIR G:");
        for (var frame = 0; frame < 100; frame++) boot.RunFrame();
        var refused = boot.ConsoleText()[start..];
        Assert.DoesNotContain("READ", refused);
        Assert.Contains("Bdos Err", refused);
        var exported = boot.Tape.Deck.Export(TapeSide.A);
        Assert.True(exported.Success, exported.Failure?.Detail);
        var wire = new TapCodec().Write(exported.Value.Image, new(TapeFormat.Tap, TapeSide.A));
        Assert.True(wire.Success, wire.Failure?.Detail);
        var codec = new TapeCatalogCodec();
        var side = codec.FromTap(wire.Value.Bytes.AsSpan(), TapeSide.A, new()).Value;
        var entry = Assert.Single(codec.FindNewest(side, new()).Value.Newest!.Entries);
        var independent = codec.ReadImage(side, entry, new());
        Assert.True(independent.Success, independent.Failure?.Detail);
        Assert.Equal(expected, independent.Value.ToArray());
        return side.RawBlocks;
    }

    private static void ReloadAndRead(ImmutableArray<ImmutableArray<byte>> blocks)
    {
        var reload = new WitnessHarness(blocks: blocks);
        BootToCpm(reload);
        Install(reload);
        Assert.Contains("NEW", Command(reload, "DIR G:"));
        Assert.Contains("TAPE INPUT OK", Command(reload, "TYPE G:NEW.TXT"));
    }

    private static void BootToCpm(WitnessHarness boot)
    {
        Assert.True(WitnessHarness.ReachesEditor(boot), "BASIC editor not reached.");
        for (var f = 0; f < 30; f++) boot.RunFrame();
        boot.TypeKey(HcKey.P); boot.TypeKey(HcKey.Space); boot.TypeExtendedChord();
        boot.TypeKey(HcKey.L); boot.TypeKey(HcKey.Space);
        foreach (var key in new[] { HcKey.D1, HcKey.D4, HcKey.D4, HcKey.D4, HcKey.D6 }) boot.TypeKey(key);
        boot.TypeKey(HcKey.Enter); boot.ArmConsoleHook();
        Assert.True(boot.RunsToPrompt(), $"CP/M prompt not reached: {boot.ConsoleText()} PC={boot.Cpu.Cpu.R.PC:X4} latches={boot.Memory.Latches}; trace={string.Join(';', boot.BootTrace.Take(70))}");
    }

    private static void Install(WitnessHarness boot)
    {
        // Owner pauses at a safe boundary after the native CCP loaded the COM;
        // binding at A> itself would evict the CCP's BD00-BFFF portion.
        boot.AtComEntry = () => Assert.True(boot.Session.Mount("VOL1.DSK", 'G').Success);
        Command(boot, "TAPEINIT");
        Assert.Equal(0x03, boot.Memory.Read(6));
        Assert.Equal(0x60, boot.Memory.Read(7));
    }

    private static string Command(WitnessHarness boot, string command)
    {
        var start = boot.ConsoleText().Length;
        boot.TypeString(command);
        Assert.True(boot.RunsToPrompt(start), $"No prompt after {command}: {boot.ConsoleText()} PC={boot.Cpu.Cpu.R.PC:X4}\n{string.Join('\n', boot.BootTrace)}");
        return boot.ConsoleText()[start..];
    }
}
