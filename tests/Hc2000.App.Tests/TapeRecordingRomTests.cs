using System.Security.Cryptography;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.Machine;
using Hc2000.Machine.Input;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Hc2000.Z80;
using Xunit;
using CpuZ80 = Hc2000.Z80.Z80;
using LiveMachine = Hc2000.Machine.Machine;

namespace Hc2000.App.Tests;

/// <summary>Headless guest execution with the whole-chip v2/IF1 map, real keyboard,
/// CPU port writes/reads and the one Machine-owned deck. Not installed-window/physical proof.</summary>
public sealed class TapeRecordingRomTests(ITestOutputHelper output)
{
    [RecorderWholeChipFact]
    public void OrdinaryBasicSaveVerifyNewLoadRunUseCapturedMicSignalAndDecodedTapBytes()
    {
        var guest = new Guest(); guest.Boot();
        // Author a BASIC line physically. Its printed answer is not present as a string in RAM input.
        guest.Key(HcKey.D1); guest.Key(HcKey.D0); guest.Key(HcKey.P); guest.Key(HcKey.Space);
        foreach (char c in "12345") guest.Digit(c);
        guest.Chord(HcKey.SymbolShift, HcKey.K); // +
        foreach (char c in "67890") guest.Digit(c);
        guest.Key(HcKey.Enter); guest.Key(HcKey.R); guest.Key(HcKey.Enter);
        guest.UntilText("80235", 100);
        Assert.True(guest.Deck.Blank(TapeCapacity.C60, false).Success);
        Assert.Equal(TapeDeckMode.Stopped, guest.Deck.Observe().Mode);
        // Engage manually BEFORE SAVE. Neither typing nor ROM addresses can arm recording.
        Assert.True(guest.Deck.RecordPlay().Success);
        long start = guest.Machine.MasterTick; int traceStart = guest.MicEdges.Count;
        guest.Key(HcKey.S); guest.Quoted("MIC6");
        Assert.Contains("SAVE", guest.Screen());
        guest.Key(HcKey.Enter);
        guest.UntilText("Start tape, then press any key", 100); guest.Key(HcKey.Enter); // the ROM's own prompt
        guest.UntilEditor(1600, minimumFrames: 600);
        Assert.True(guest.Deck.StopEject(false).Success);
        var recording = guest.Deck.Export(TapeSide.A).Value;
        var trace = guest.MicEdges.Skip(traceStart).ToArray();
        Assert.True(trace.Length > 6000, $"SAVE did not emit a real MIC pilot/data trace: {trace.Length}\n{guest.Screen()}");
        Assert.True(guest.Deck.Observe().A.Dirty);
        Assert.True(recording.Image.Duration > recording.Image.SourceDuration);
        Assert.All(trace, edge => Assert.Equal(0, (edge.Tick - start) % 5));
        output.WriteLine($"Whole-chip SHA256={guest.RomHash}; native MasterHz={FrameTiming.MasterHz}; actual MIC transitions={trace.Length}; start={start}; head={guest.Deck.Observe().Position}");
        var tap = new TapCodec().Write(recording.Image, new(TapeFormat.Tap, TapeSide.A));
        Assert.True(tap.Success, tap.Failure?.Detail);
        Assert.Equal(TapeSignalFidelity.DataOnly, tap.Value.Report.Signal);
        output.WriteLine($"Recorded TAP bytes={tap.Value.Bytes.Length}; SHA256={Convert.ToHexString(SHA256.HashData(tap.Value.Bytes.AsSpan()))}; fidelity=DataOnly");
        // VERIFY against the SAME native recorded signal, not the normalized TAP writer output.
        guest.WindToStart();
        guest.Chord(HcKey.CapsShift, HcKey.SymbolShift); guest.Chord(HcKey.SymbolShift, HcKey.R); guest.Quoted("MIC6"); // E mode, SS+R = VERIFY
        Assert.Contains("VERIFY", guest.Screen());
        guest.Key(HcKey.Enter); Assert.True(guest.Deck.Play().Success);
        long highBefore = guest.Fe.High, lowBefore = guest.Fe.Low;
        guest.UntilEditor(1600, minimumFrames: 600); Assert.Contains("OK", guest.Screen());
        Assert.True(guest.Fe.High > highBefore && guest.Fe.Low > lowBefore, "VERIFY must read both actual FE-D6 phases.");
        Assert.True(guest.Deck.StopEject(false).Success);
        // NEW is a guest command; no host memory injection/removal of the program.
        guest.Key(HcKey.A); guest.Key(HcKey.Enter); guest.Boot(); // NEW re-runs the ROM init and its start menu
        guest.WindToStart(); guest.Key(HcKey.J); guest.Quoted("MIC6");
        Assert.Contains("LOAD", guest.Screen()); guest.Key(HcKey.Enter);
        Assert.True(guest.Deck.Play().Success); guest.UntilEditor(1600, minimumFrames: 600);
        Assert.Contains("OK", guest.Screen()); Assert.True(guest.Deck.StopEject(false).Success);
        guest.Key(HcKey.R); guest.Key(HcKey.Enter); guest.UntilText("80235", 150);
        output.WriteLine("Native recorded-signal guest result:\n" + guest.Screen());
        // A second guest loads detached recognized TAP bytes; original/ideal SAVE regeneration is not used.
        var second = new Guest(); second.Boot();
        var imported = new TapCodec().Read(tap.Value.Bytes.AsSpan(), new(TapeFormat.Tap, TapeSide.A, TapeCapacity.C60)).Value;
        second.Deck.Blank(TapeCapacity.C60, false);
        var blank = second.Deck.Export(TapeSide.B).Value.Image;
        Assert.True(second.Deck.Insert(new(TapeCapacity.C60, imported.Image, blank, imported.SourceAssets), false).Success);
        second.Key(HcKey.J); second.Quoted("MIC6"); second.Key(HcKey.Enter); second.Deck.Play();
        second.UntilEditor(1600, minimumFrames: 600); Assert.Contains("OK", second.Screen()); second.Deck.StopEject(false);
        second.Key(HcKey.R); second.Key(HcKey.Enter); second.UntilText("80235", 150);
        output.WriteLine("Recognized TAP guest result:\n" + second.Screen());
    }

    [RecorderWholeChipFact]
    public void GuestBlankLoadDoesNotAutoCompleteAndPhysicalBreakReturnsToEditor()
    {
        var guest = new Guest(); guest.Boot(); guest.Deck.Blank(TapeCapacity.C60, false);
        guest.Key(HcKey.J); guest.Quoted(""); guest.Key(HcKey.Enter);
        Assert.Equal(TapeDeckMode.Stopped, guest.Deck.Observe().Mode); // no ROM-triggered PLAY
        guest.Deck.Play(); guest.Frames(100);
        Assert.Equal(TapeDeckMode.Playing, guest.Deck.Observe().Mode);
        Assert.True(guest.Fe.Low > 0); Assert.True(guest.Deck.Observe().Position > default(TapeTime));
        guest.Chord(HcKey.CapsShift, HcKey.Space); guest.UntilEditor(200, minimumFrames: 20);
        Assert.Contains("BREAK", guest.Screen()); guest.Deck.StopEject(false);
        output.WriteLine("Guest blank-load/BREAK result:\n" + guest.Screen());
    }

    [RecorderWholeChipFact]
    public void GuestRejectsAuthoredBadXorWithoutRepairingImportedSignal()
    {
        // Synthetic negative fixture from our own bytes, NOT a regenerated positive SAVE.
        var file = Hc2000.Machine.Storage.Tap.Load([]);
        file.Put(new Hc2000.Machine.Storage.TapeFile("BADXOR", 3, 32768, 0, [1, 2, 3, 4]));
        var bytes = file.Save(); bytes[^1] ^= 1;
        var read = new TapCodec().Read(bytes, new(TapeFormat.Tap, TapeSide.A, TapeCapacity.C60)).Value;
        Assert.Contains(read.Warnings, w => w.Code == "bad-xor");
        var guest = new Guest(); guest.Boot(); guest.Deck.Blank(TapeCapacity.C60, false);
        var blank = guest.Deck.Export(TapeSide.B).Value.Image;
        Assert.True(guest.Deck.Insert(new(TapeCapacity.C60, read.Image, blank, read.SourceAssets), false).Success);
        // The fixture is a CODE file: LOAD "" alone skips it, so type LOAD "" CODE (E mode, I).
        guest.Key(HcKey.J); guest.Quoted(""); guest.Chord(HcKey.CapsShift, HcKey.SymbolShift); guest.Key(HcKey.I);
        Assert.Contains("CODE", guest.Screen());
        guest.Key(HcKey.Enter); guest.Deck.Play();
        guest.UntilEditor(1600, minimumFrames: 600);
        Assert.Contains("Tape loading error", guest.Screen(), StringComparison.OrdinalIgnoreCase);
        guest.Deck.StopEject(false); output.WriteLine("Guest bad-XOR result:\n" + guest.Screen());
    }

    internal sealed class FeObservationBus(HcMachineBus inner) : IM1FetchBus
    {
        internal long High, Low;
        public byte In(ushort port)
        {
            byte value = inner.In(port);
            if ((port & 1) == 0) { if ((value & 64) != 0) High++; else Low++; }
            return value;
        }
        public byte Read(ushort address) => inner.Read(address);
        public void Write(ushort address, byte value) => inner.Write(address, value);
        public void Out(ushort port, byte value) => inner.Out(port, value);
        public void Contend(ushort address, int tstates) => inner.Contend(address, tstates);
        public byte IntVector() => inner.IntVector();
        public byte FetchM1(ushort address, bool instructionStart, long cpuTStates) => inner.FetchM1(address, instructionStart, cpuTStates);
    }

    private sealed class Guest
    {
        internal readonly LiveMachine Machine;
        internal readonly ITapeDeck Deck;
        internal readonly CpuZ80 Cpu;
        internal readonly HcMemory Memory;
        internal readonly List<(long Tick, bool Level)> MicEdges = [];
        internal readonly string RomHash;
        internal readonly FeObservationBus Fe;
        private readonly byte[] basic;
        private VideoFrame? last;
        internal Guest()
        {
            var root = RomSelectionFixture.RepositoryRoot();
            var roms = ProfileRomSelection.LoadWholeChipV2(root); basic = roms.Basic;
            RomHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, ProfileRomSelection.WholeChipSourcePath))));
            var profile = roms.Profile; var scheduler = new DeviceScheduler();
            var clock = new FrameClock(scheduler, 1); var mapper = CpuTimeMapper.FromProfile(profile);
            var adapter = new CpuDeviceTimeAdapter(scheduler, mapper);
            Memory = new(profile.Memory with { RomProvenance = "M27C512 whole-chip EPROM" }, basic, roms.Cpm, roms.If1);
            var ula = new Ula(Memory, new KeyboardMatrix(), scheduler, clock, 2, profile.Video);
            var idle = new PortDevice(); var ports = new PortDecoder(HcIoProfile.Faithful(Memory, ula, idle, idle, idle, idle, idle));
            Fe = new(new HcMachineBus(Memory, ports, adapter, scheduler));
            var bus = new CpuTimedBus(Fe, adapter);
            Cpu = new(bus); bus.Attach(Cpu); clock.InterruptChanged += (_, asserted) => Cpu.IntLine = asserted;
            Machine = new(scheduler, clock, adapter, ula, () => Cpu.Step(), () => Cpu.TStates);
            Deck = Machine.EnableTape(42).Deck;
            bool previous = ula.Beeper.Microphone;
            ula.BeeperChanged += edge => { if (edge.Microphone != previous) { MicEdges.Add((edge.MasterTick, edge.Microphone)); previous = edge.Microphone; } };
        }
        internal void Frames(int count) { for (int i = 0; i < count; i++) last = Machine.RunFrame(); }
        internal string Screen() => last is null ? "" : BasicMachineCompositionTests.DecodeBasicScreen(last, basic);
        internal void Key(HcKey key) => Chord(key);
        internal void Chord(params HcKey[] keys)
        {
            foreach (var key in keys) Machine.Keyboard.SetKey(key, true);
            Frames(5);
            foreach (var key in keys) Machine.Keyboard.SetKey(key, false);
            Frames(5);
        }
        internal void Digit(char c) => Key(Enum.Parse<HcKey>("D" + c));
        internal void Quoted(string text)
        {
            Chord(HcKey.SymbolShift, HcKey.P);
            foreach (char c in text) { if (char.IsDigit(c)) Digit(c); else Chord(HcKey.CapsShift, Enum.Parse<HcKey>(c.ToString())); }
            Chord(HcKey.SymbolShift, HcKey.P);
        }
        internal void Boot()
        {
            Frames(150); Key(HcKey.X); Key(HcKey.Enter); Frames(40);
            Assert.Contains("OK", Screen());
        }
        internal void UntilText(string text, int frames)
        {
            for (int i = 0; i < frames && !Screen().Contains(text, StringComparison.Ordinal); i++) Frames(1);
            Assert.Contains(text, Screen());
        }
        internal void UntilEditor(int frames, int minimumFrames)
        {
            // Read-only ROM editor-state oracle, never a control hook or completion shortcut.
            for (int i = 0; i < frames; i++)
            {
                Frames(1);
                ushort word(int address) => (ushort)(Memory.Read((ushort)address) | Memory.Read((ushort)(address + 1)) << 8);
                int line = word(0x5c59), workspace = word(0x5c61), gap = workspace - line;
                if (i >= minimumFrames && Memory.Read(0x5c41) == 0 && line is >= 0x4000 and < 0x6000
                    && workspace is >= 0x4000 and < 0x6000 && gap is >= 1 and <= 32) return;
            }
            Assert.Fail($"Guest did not return to editor within {frames} frames; PC={Cpu.R.PC:X4}; screen:\n{Screen()}");
        }
        internal void WindToStart()
        {
            Assert.True(Deck.SetFastWind(true).Success); Assert.True(Deck.Rewind().Success);
            for (int i = 0; i < 300 && Deck.Observe().Mode != TapeDeckMode.Stopped; i++) Frames(1);
            Assert.Equal(default(TapeTime), Deck.Observe().Position);
            Assert.Equal(TapeHoldReason.PhysicalEnd, Deck.Observe().HoldReason);
        }
    }
}

public sealed class RecorderWholeChipFactAttribute : FactAttribute
{
    public RecorderWholeChipFactAttribute([System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null, [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        var path = Path.Combine(RomSelectionFixture.RepositoryRoot(), ProfileRomSelection.WholeChipSourcePath);
        if (!File.Exists(path) || new FileInfo(path).Length != 65536)
            Skip = "Canonical whole-chip asset absent or not LFS-materialized.";
    }
}
