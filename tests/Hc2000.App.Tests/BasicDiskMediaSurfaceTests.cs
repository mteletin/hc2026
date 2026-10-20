using System.Buffers.Binary;
using System.Security.Cryptography;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.Machine;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Snapshots;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Z80Cpu = Hc2000.Z80.Z80;
using Xunit;
using LiveMachine = Hc2000.Machine.Machine;
using VideoFrameType = Hc2000.Machine.Video.VideoFrame;

namespace Hc2000.App.Tests;

/// <summary>
/// HC2000 08 step 8: verifies the eight faithful BASIC title-disk scenarios (CAT + LOAD + RUN)
/// through the EXISTING desktop media surface — MediaSession/MediaBridge over the same drives the
/// FDC sees, plus the shared InputQueue that the worker's owner thread consumes each boundary.
/// No emulator or disk reimplementation: the same v2 machine composition the app uses
/// (BasicMachineComposition pattern), the same ROMs, the same physical key chords the app's
/// KeyboardInputAdapter would produce, typed into the machine. The test thread never touches the
/// machine; it only enqueues commands and reads frames from the worker mailbox.
/// </summary>
public sealed class BasicDiskMediaSurfaceTests
{
    private const int HoldFrames = 8;

    [Theory]
    [InlineData("ALBA8.DSK", "run")]
    [InlineData("ALEO-UTILS.DSK", "artist2")]
    [InlineData("ALEO-GAMES1.DSK", "run")]
    [InlineData("BASDEMO35.DSK", "run")]
    [InlineData("BASIC2.DSK", "run")]
    [InlineData("CRISTIG1.DSK", "run")]
    [InlineData("GEOOLDIES.DSK", "ManicMiner")]
    [InlineData("RARESGOLDENAXE.DSK", "goldenaxe")]
    public async Task DesktopMediaSurfaceCatsLoadsAndRunsEachTitleDisk(string disk, string program)
    {
        var root = Root();
        var diskPath = Path.Combine(root, "disks", "basic", disk);
        Assert.True(File.Exists(diskPath), $"Missing asset {diskPath}; run from a full checkout.");
        var diskBytes = File.ReadAllBytes(diskPath);
        var diskHash = Convert.ToHexString(SHA256.HashData(diskBytes)).ToLowerInvariant();
        var basicRom = File.ReadAllBytes(Path.Combine(root, "roms", "hc2000", "v2", "basic.rom"));
        var cpmUpper = File.ReadAllBytes(Path.Combine(root, "roms", "hc2000", "v2", "cpm-hc2kzip.bin"));
        var if1Rom = File.ReadAllBytes(Path.Combine(root, "roms", "hc2000", "v2", "if1.rom"));
        Assert.Equal(0x4000, basicRom.Length);
        Assert.Equal(0x2000, cpmUpper.Length);
        Assert.Equal(0x4000, if1Rom.Length);
        var name = Path.GetFileNameWithoutExtension(disk);
        var evidence = Path.Combine(root, "temp", "wi-118-step8");
        Directory.CreateDirectory(evidence);

        var queue = new InputQueue();
        var bridge = new MediaBridge(queue);
        var harness = new SurfaceHarness(bridge, basicRom, cpmUpper, if1Rom, diskBytes);
        var input = new InputController(queue, control: (ctrl, _) => harness.PendingControl = ctrl,
            media: (command, _) => harness.OnMedia(command));
        using var worker = new EmulationWorker(
            () => harness.BuildMachine(),
            context =>
            {
                input.ApplyPending(context.Machine.MasterTick, context.Machine.Keyboard);
                if (harness.PendingControl is { } ctrl)
                {
                    harness.PendingControl = null;
                    if (ctrl.Kind == InputControlKind.SetSpeed)
                        context.SetSpeed(ctrl.SpeedPercent == 0 ? SpeedMode.Turbo : (SpeedMode)ctrl.SpeedPercent);
                }
                // Editor-state oracle (7a ReachesEditor), sampled on the owner thread.
                // The 5-consecutive-stable-polls requirement lives HERE, on the owner thread only;
                // the test thread merely observes harness.BootReady.
                var ready = harness.EditorReady();
                harness.ReadyPolls = ready ? harness.ReadyPolls + 1 : 0;
                if (harness.ReadyPolls >= 5 && !harness.BootReady)
                    harness.Debug.Enqueue("EDITOR READY @tick " + context.Machine.MasterTick);
                harness.BootReady = harness.ReadyPolls >= 5;
            },
            cleanup: context => input.ReleaseOwned(context.Machine.Keyboard));

        try
        {
            // The desktop media panel inserts the disk: one MediaBridge request over the shared
            // input queue, answered by the MediaSession running on the emulation owner.
            var inserted = await bridge.RequestAsync(new MediaCommand(MediaAction.InsertDisk, DiskSlot.A,
                System.Collections.Immutable.ImmutableArray.CreateRange(diskBytes), name));
            Assert.Null(inserted.Error);
            Assert.True(inserted.A.Present);

            // Turbo: the pacer skips wall pacing; the machine still runs at its real 2 MHz.
            // (SpeedPercent 0 == SpeedMode.Turbo; 400 is Quadruple, which still paces.)
            queue.Enqueue(new RuntimeControl(InputControlKind.SetSpeed, SpeedPercent: 0));

            // v2 boot oracle: the same editor-state check the 7a harness uses (ReachesEditor
            // polls E_LINE/WORKSP until stable). Sampled on the owner thread at every
            // instruction boundary (harness.EditorReady); require 5 consecutive polls, then
            // the 7a settle wait (~30 frames) before typing. The FDC READ DATA gate later
            // is the real gate for disk I/O.
            var seenTexts = new List<string>();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            var last = "";
            while (DateTime.UtcNow < deadline)
            {
                // Oracle sampled ONLY on the owner thread (the boundary callback updates
                // harness.BootReady, 5 consecutive editor-ready polls). The test thread
                // never reads machine state directly: it only waits on the mailbox flag the
                // owner publishes, keeping the app's worker/mailbox composition contract.
                if (harness.BootReady) break;
                if (worker.TakeLatestFrame() is { } frame)
                {
                    last = DecodeBasicScreen(frame, basicRom);
                    var prev = seenTexts.Count > 0 ? seenTexts[^1] : null;
                    if (last != prev && seenTexts.Count < 16) seenTexts.Add(last);
                }
                if (worker.Completion.IsCompleted) await worker.Completion;
                await Task.Delay(1);
            }
            if (!harness.BootReady)
                throw new InvalidOperationException(
                    "editor never ready in 90s (7a oracle: 0x5C41==0, E_LINE/WORKSP in 0x4000..0x6000, gap 1..32); last screen:\n"
                    + last + "\nSeen boot screens:\n" + string.Join("\n---\n", seenTexts));
            // 7a waits ~30 frames after reaching the editor before typing; match that here.
            await WaitFrames(worker, 40);
            var booted = worker.TakeLatestFrame()
                ?? await WaitForAnyFrame(worker);
            var bootColors = booted.Pixels.ToArray().Distinct().Count();
            File.WriteAllText(Path.Combine(evidence, name + "-boot.txt"),
                "Boot screen (after EditorReady oracle, +40 frames):\n" + DecodeBasicScreen(booted, basicRom) + "\n"
                + "Frame: " + booted.Width + "x" + booted.Height + " pixels=" + booted.Pixels.Length
                + " distinctColors=" + bootColors + "\n"
                + "FDC: " + harness.Floppy!.Fdc.DiagLog.Replace("\n", " | ") + "\n"
                + "Debug: " + string.Join(" | ", harness.Debug) + "\n"
                + "Seen boot screens:\n" + string.Join("\n---\n", seenTexts) + "\n");

            // CAT 1 — the exact physical chords of the app adapter (E-mode extended chord,
            // SYMBOL+9 = CAT token CF, space, 1), then physical Enter.
            await TypeKey(queue, worker, new KeyChord(HcKey.CapsShift, HcKey.SymbolShift));
            await TypeKey(queue, worker, new KeyChord(HcKey.SymbolShift, HcKey.D9));
            await TypeKey(queue, worker, new KeyChord(HcKey.Space));
            await TypeKey(queue, worker, new KeyChord(HcKey.D1));
            var catBefore = FrameHash(await WaitForAnyFrame(worker));
            await TypeKey(queue, worker, new KeyChord(HcKey.Enter));

            // Wait for the FDC to return data bytes (DiagReadReturns > 0), not just a screen change.
            // The motor-settle delay is ~5s of machine time; at Turbo this is fast,
            // but the screen changes (cursor) before the FDC finishes.
            // Do NOT call DiagLog here: ToString() throws when the log exceeds ~2GB.
            var catDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            VideoFrameType? catOutput = null;
            while (DateTime.UtcNow < catDeadline)
            {
                if (worker.TakeLatestFrame() is { } f) catOutput = f;
                if (harness.Floppy!.Fdc.DiagReadReturns > 0) break;
                if (worker.Completion.IsCompleted) await worker.Completion;
                await Task.Delay(10);
            }
            catOutput ??= await WaitForAnyFrame(worker);
            var catReadReturns = harness.Floppy!.Fdc.DiagReadReturns;
            string catFdc;
            try { catFdc = harness.Floppy.Fdc.DiagLog; }
            catch (ArgumentOutOfRangeException) { catFdc = "(log too large for ToString)"; }
            var catScreen = DecodeBasicScreen(catOutput, basicRom);
            var catColors = catOutput.Pixels.ToArray().Distinct().Count();
            WriteBmp(Path.Combine(evidence, name + "-cat.bmp"), catOutput);
            File.WriteAllText(Path.Combine(evidence, name + "-cat.txt"),
                $"Disk: {disk}\nSHA-256: {diskHash}\n" +
                $"ROMs: basic.rom={Hash(basicRom)} cpm-hc2kzip.bin={Hash(cpmUpper)} if1.rom={Hash(if1Rom)}\n" +
                $"Commands: media surface InsertDisk(A, {disk}); CAT 1 (extended chord, SYMBOL+9, space, 1, Enter)\n" +
                $"FDC: DiagReadReturns={catReadReturns} READ DATA commands={CountLines(catFdc, "op=6")} returned-records={CountLines(catFdc, "R@")}\n" +
                $"Screen:\n{catScreen}\n" +
                $"Frame: {catOutput.Width}x{catOutput.Height} pixels={catOutput.Pixels.Length} distinctColors={catColors}\n" +
                $"FDC (truncated):\n{(catFdc.Length > 4096 ? catFdc[..4096] + "..." : catFdc)}\n" +
                $"Debug:\n" + string.Join("\n", harness.Debug) + "\n");
            Assert.True(catReadReturns > 0, $"CAT read no FDC data bytes for {disk} (DiagReadReturns=0). Screen:\n{catScreen}");

            // LOAD *"d";1;"<program>" — same physical chords as the app adapter, then Enter.
            await TypeKey(queue, worker, new KeyChord(HcKey.J));
            await TypeKey(queue, worker, new KeyChord(HcKey.Space));
            await TypeKey(queue, worker, new KeyChord(HcKey.SymbolShift, HcKey.B)); // *
            await TypeKey(queue, worker, new KeyChord(HcKey.SymbolShift, HcKey.P)); // "
            await TypeKey(queue, worker, new KeyChord(HcKey.D)); // d
            await TypeKey(queue, worker, new KeyChord(HcKey.SymbolShift, HcKey.P)); // "
            await TypeKey(queue, worker, new KeyChord(HcKey.SymbolShift, HcKey.O)); // ;
            await TypeKey(queue, worker, new KeyChord(HcKey.D1)); // 1
            await TypeKey(queue, worker, new KeyChord(HcKey.SymbolShift, HcKey.O)); // ;
            await TypeKey(queue, worker, new KeyChord(HcKey.SymbolShift, HcKey.P)); // "
            foreach (var letter in program.ToUpperInvariant())
                await TypeKey(queue, worker, new KeyChord(HcKey.CapsShift, ParseLetter(letter)));
            await TypeKey(queue, worker, new KeyChord(HcKey.SymbolShift, HcKey.P)); // "
            var loadBefore = FrameHash(await WaitForAnyFrame(worker));
            await TypeKey(queue, worker, new KeyChord(HcKey.Enter));

            var loaded = await WaitForNewScreen(worker, loadBefore, TimeSpan.FromSeconds(40));
            var loadReadReturns = harness.Floppy.Fdc.DiagReadReturns;
            var loadScreen = DecodeBasicScreen(loaded, basicRom);
            WriteBmp(Path.Combine(evidence, name + "-load.bmp"), loaded);
            File.AppendAllText(Path.Combine(evidence, name + "-cat.txt"),
                $"\nCommands: LOAD *\"d\";1;\"{program}\" (physical J, space, SYMBOL chords, letters, Enter)\n" +
                $"LOAD screen:\n{loadScreen}\n" +
                $"LOAD DiagReadReturns={loadReadReturns}\n");
            Assert.True(loadReadReturns > catReadReturns, $"LOAD read no additional FDC data bytes for {disk}/{program} (DiagReadReturns={loadReadReturns}, was {catReadReturns}). Screen:\n{loadScreen}");

            // RUN — physically typed (caps+R/U/N + Enter); no memory injection through the surface.
            await TypeKey(queue, worker, new KeyChord(HcKey.CapsShift, HcKey.R));
            await TypeKey(queue, worker, new KeyChord(HcKey.CapsShift, HcKey.U));
            await TypeKey(queue, worker, new KeyChord(HcKey.CapsShift, HcKey.N));
            var runBefore = FrameHash(await WaitForAnyFrame(worker));
            await TypeKey(queue, worker, new KeyChord(HcKey.Enter));

            var runOutput = await WaitForNewScreen(worker, runBefore, TimeSpan.FromSeconds(60));
            var runReadReturns = harness.Floppy.Fdc.DiagReadReturns;
            var runScreen = DecodeBasicScreen(runOutput, basicRom);
            WriteBmp(Path.Combine(evidence, name + "-run.bmp"), runOutput);
            File.AppendAllText(Path.Combine(evidence, name + "-cat.txt"),
                $"\nCommands: RUN (physical caps+R, caps+U, caps+N, Enter)\n" +
                $"RUN screen:\n{runScreen}\n" +
                $"FDC totals: DiagReadReturns={runReadReturns}\n");
            var nonBlank = runOutput.Pixels.ToArray().Distinct().Count() > 1;
            Assert.True(nonBlank, $"RUN left a blank screen for {disk}/{program}; screen=\n{runScreen}");
        }
        finally
        {
            worker.Dispose();
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            bridge.Disconnect();
        }
    }

    // ── Composition: the same v2 machine the app builds, plus the app's media surface ──

    private sealed class SurfaceHarness(MediaBridge bridge, byte[] basicRom, byte[] cpmUpper, byte[] if1Rom, byte[] diskBytes)
    {
        /// <summary>Owner-thread debug channel (boundary diagnostics only; no machine access).</summary>
        public System.Collections.Concurrent.ConcurrentQueue<string> Debug { get; } = new();

        /// <summary>Owner-thread 7a oracle: set once 5 consecutive editor-ready polls are seen.
        /// Written on the emulation owner, read (as a plain flag) by the test thread waiting on
        /// the worker mailbox — the same visibility guarantee the worker's Completion task gives.</summary>
        public int ReadyPolls { get; set; }
        public volatile bool BootReady;
        public RuntimeControl? PendingControl;

        public FloppyController? Floppy { get; private set; }
        private HcMemory _memory = null!;
        private KeyboardMatrix _keyboard = null!;
        private MediaSession? session;
        private readonly byte[] basicRom = basicRom;
        private readonly byte[] cpmUpper = cpmUpper;
        private readonly byte[] if1Rom = if1Rom;
        private readonly byte[] diskBytes = diskBytes;

        // Runs on the emulation owner (first media command); the session owns the SAME drives.
        public void OnMedia(MediaCommand command)
        {
            if (session is null) session = new MediaSession(Floppy!.DriveA, Floppy.DriveB);
            Debug.Enqueue($"media: {command.Action} {command.Slot} {command.Name}");
            bridge.Publish(session.Apply(command));
        }

        public LiveMachine BuildMachine()
        {
            var profile = MachineProfile.Hc2000V2;
            profile.Validate();
            var mapper = CpuTimeMapper.FromProfile(profile);
            var scheduler = new DeviceScheduler();
            var clock = new FrameClock(scheduler, 1);
            var adapter = new CpuDeviceTimeAdapter(scheduler, mapper);
            var cpm = new byte[0x4000];
            Buffer.BlockCopy(cpmUpper, 0, cpm, 0x2000, 0x2000); // v2: upper 8K at 0x2000 (7a LoadRoms)
            var memory = new HcMemory(profile.Memory with { RomProvenance = "canonical verified hc2000-v2 ROM set (step 7a/7b sources)" },
                basicRom, cpm, if1Rom);
            _memory = memory;
            var keyboard = new KeyboardMatrix();
            _keyboard = keyboard;
            var ula = new Ula(memory, keyboard, scheduler, clock, 2, profile.Video);
            var floppy = new FloppyController(scheduler, 100,
                new FloppyDrive(new DriveProfile(80, 2), Dsk.Load(diskBytes)), new FloppyDrive(new()), instant: true);
            Floppy = floppy;
            var driveLatch = new PortDevice(
                read: (_, _) => floppy.ReadLatch(),
                write: (_, value, _) => floppy.WriteLatch(value));
            var fdcStatus = new PortDevice(read: (_, _) => floppy.Fdc.ReadStatus());
            var fdcData = new PortDevice(
                read: (_, _) => floppy.Fdc.ReadData(),
                write: (_, value, _) => floppy.Fdc.WriteData(value));
            var idle = new PortDevice();
            var ports = new PortDecoder(HcIoProfile.Faithful(memory, ula, driveLatch, fdcStatus, fdcData, idle, idle));
            var bus = new HcMachineBus(memory, ports, adapter, scheduler);
            var cpu = new Z80Cpu(bus); // HcMachineBus implements IM1FetchBus, so Z80 picks up the M1 fast path.
            Debug.Enqueue("cpu: constructed");
            clock.InterruptChanged += (_, asserted) => cpu.IntLine = asserted;
            var machine = new LiveMachine(scheduler, clock, adapter, ula, () => cpu.Step(), () => cpu.TStates);
            machine.AttachSnapshotState(profile, clock, memory, mapper, new StubSeam(), 2, floppy);
            return machine;
        }

        private sealed class StubSeam : ICpuSnapshotSeam
        {
            public CpuLogicalState Capture() => throw new NotSupportedException("Snapshots are not used by this test.");
            public void Restore(CpuLogicalState state) => throw new NotSupportedException();
        }

        /// <summary>Owner-thread only (boundary callback). The same editor-state oracle the 7a
        /// harness uses (MachineBootAcceptanceTests.EditorReady): BASIC-mode latch 0x5C41 == 0,
        /// E_LINE/WORKSP pointers inside the text RAM window with a 1..32-byte gap.</summary>
        public bool EditorReady()
        {
            if (_memory.Read(0x5C41) != 0) return false;
            var eLine = _memory.Read(0x5C59) | (_memory.Read(0x5C5A) << 8);
            var wsp = _memory.Read(0x5C61) | (_memory.Read(0x5C62) << 8);
            if (eLine < 0x4000 || eLine >= 0x6000) return false;
            if (wsp < 0x4000 || wsp >= 0x6000) return false;
            var gap = wsp - eLine;
            return gap >= 1 && gap <= 32;
        }
    }

    // ── Helpers: key chords through the shared input queue (what the app adapter would enqueue) ──

    private static HcKey ParseLetter(char letter) =>
        char.IsDigit(letter) ? Enum.Parse<HcKey>("D" + letter) : Enum.Parse<HcKey>(letter.ToString());

    private static readonly object EnqueueGate = new();
    private static long _enqueueSeq;

    private static async Task TypeKey(InputQueue queue, EmulationWorker worker, KeyChord chord)
    {
        // InputController matches a key-up to its key-down by Source (held.Remove(source)
        // then Change(-1)). Each physical key of the chord gets ONE source shared by its
        // down and up edges; a unique source per edge would leave the down stuck forever
        // (the up's source is never in held, so counts never decrement and the matrix bit
        // stays closed). All downs are enqueued before the hold wait so the owner applies
        // them in one boundary batch (simultaneous closure, as 7a TypeShiftedKey does);
        // all ups land together on the release boundary.
        var sources = new string[chord.Keys.Length];
        for (var i = 0; i < chord.Keys.Length; i++)
            sources[i] = "pc:step8:edge:" + Interlocked.Increment(ref _enqueueSeq);
        for (var i = 0; i < chord.Keys.Length; i++)
            queue.Enqueue(new KeyTransition(sources[i], new KeyChord(chord.Keys[i]), true));
        await WaitFrames(worker, HoldFrames);
        for (var i = 0; i < chord.Keys.Length; i++)
            queue.Enqueue(new KeyTransition(sources[i], new KeyChord(chord.Keys[i]), false));
        await WaitFrames(worker, HoldFrames);
    }

    // ── Frame polling on the worker mailbox ──

    private static async Task<int> WaitFrames(EmulationWorker worker, int count)
    {
        var seen = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(count * 4); // generous at Turbo
        while (seen < count && DateTime.UtcNow < deadline)
        {
            if (worker.TakeLatestFrame() is { }) seen++;
            if (worker.Completion.IsCompleted) await worker.Completion;
            await Task.Delay(1);
        }
        Assert.True(seen >= count, $"Expected {count} frames; saw {seen}.");
        return seen;
    }

    private static async Task<VideoFrameType> WaitForAnyFrame(EmulationWorker worker)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (worker.TakeLatestFrame() is { } frame) return frame;
            if (worker.Completion.IsCompleted) await worker.Completion;
            await Task.Delay(1);
        }
        throw new TimeoutException("No frame from the emulation worker.");
    }

    /// <summary>Wait until a decoded screen differs from <paramref name="beforeHash"/> (output changed).</summary>
    private static async Task<VideoFrameType> WaitForNewScreen(EmulationWorker worker, string beforeHash, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var last = "(no frame yet)";
        while (DateTime.UtcNow < deadline)
        {
            if (worker.TakeLatestFrame() is { } frame)
            {
                var hash = FrameHash(frame);
                if (hash != beforeHash) return frame; // the visible output is already changed
                last = hash;
            }
            if (worker.Completion.IsCompleted) await worker.Completion;
            await Task.Delay(1);
        }
        throw new TimeoutException($"Screen did not change from {beforeHash}. Last: {last}");
    }

    private static async Task<VideoFrameType> WaitForScreen(EmulationWorker worker, byte[] rom,
        Func<string, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var last = "(no frame yet)";
        while (DateTime.UtcNow < deadline)
        {
            if (worker.TakeLatestFrame() is { } frame)
            {
                last = DecodeBasicScreen(frame, rom);
                if (predicate(last)) return frame;
            }
            if (worker.Completion.IsCompleted) await worker.Completion;
            await Task.Delay(1);
        }
        throw new TimeoutException($"BASIC screen did not reach expected text. Last rendered screen:\n{last}");
    }

    // ── Evidence ──

    private static string FrameHash(VideoFrameType frame) =>
        Convert.ToHexString(SHA256.HashData(frame.Pixels.ToArray().Select(p => (byte)(p & 0xff)).ToArray())).ToLowerInvariant();

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static int CountLines(string text, string marker) =>
        text.Split('\n').Count(line => line.Contains(marker, StringComparison.Ordinal));

    private static void WriteEvidence(string path, string content) => File.WriteAllText(path, content);

    /// <summary>Decode the ULA-published 32x24 framebuffer with the v2 BASIC ROM glyphs. The v2
    /// glyph table (0x3d00–0x3ea0) is byte-identical to v1's, so the app test's decoder applies.</summary>
    private static string DecodeBasicScreen(VideoFrameType frame, byte[] rom)
    {
        if (frame.Width != 352 || frame.Height != 296) return "";
        var glyphs = new Dictionary<string, char>(StringComparer.Ordinal);
        for (var code = 32; code < 127; code++)
        {
            var glyph = rom.AsSpan(0x3d00 + (code - 32) * 8, 8).ToArray();
            glyphs.TryAdd(Convert.ToHexString(glyph), (char)code);
        }
        var pixels = frame.Pixels.Span;
        var screen = new System.Text.StringBuilder(24 * 33);
        for (var row = 0; row < 24; row++)
        {
            for (var column = 0; column < 32; column++)
            {
                var bits = new byte[8];
                var counts = new Dictionary<uint, int>();
                for (var y = 0; y < 8; y++)
                    for (var x = 0; x < 8; x++)
                    {
                        var color = pixels[(40 + row * 8 + y) * frame.Width + 48 + column * 8 + x];
                        counts[color] = counts.GetValueOrDefault(color) + 1;
                    }
                var background = counts.MaxBy(pair => pair.Value).Key;
                for (var y = 0; y < 8; y++)
                    for (var x = 0; x < 8; x++)
                        if (pixels[(40 + row * 8 + y) * frame.Width + 48 + column * 8 + x] != background)
                            bits[y] |= (byte)(0x80 >> x);
                screen.Append(glyphs.GetValueOrDefault(Convert.ToHexString(bits), '?'));
            }
            if (row < 23) screen.Append('\n');
        }
        return screen.ToString();
    }

    private static void WriteBmp(string path, VideoFrameType frame)
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
        var pixels = frame.Pixels.ToArray();
        for (var i = 0; i < pixels.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(54 + i * 4), pixels[i]);
        File.WriteAllBytes(path, bmp);
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hc2000.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
