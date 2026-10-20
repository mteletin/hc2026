using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.Machine;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Hc2000.Z80;
using CpuZ80 = Hc2000.Z80.Z80;
using LiveMachine = Hc2000.Machine.Machine;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>
/// Headless oracle per arch-1 refinement (626983, doc oracle-refine-20261013) + PO return (626995).
/// Boots the v2 whole-chip EPROM (M27C512) headlessly, feeds x/Return then PRINT USR 14446/Return
/// via InputController, and dumps matrix state, screen RAM, PC sequence, and FDC reads to emit
/// a named verdict: (a) input gap, (b) input lands but boot fails, or (c) CP/M warm boot reaches A>.
/// </summary>
public sealed partial class HeadlessOracle(Xunit.ITestOutputHelper output)
{
    private sealed class Trace
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Events = new();
        public readonly HashSet<ushort> Milestones = new(); // owner thread only
        public readonly System.Collections.Concurrent.ConcurrentDictionary<HcKey, byte> SeenKeys = new();
        public int[] PreviousKeys = new int[40];
        public readonly System.Collections.Concurrent.ConcurrentQueue<byte> Console = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<EntryFetch> EntryFetches = new();
        public int EntryFetchOverflow;
        public int ZeroVectorEntries;
        public readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> FdcCommands = new();
    }
    private sealed class OracleContext : IDisposable
    {
        public required EmulationWorker Worker { get; init; }
        public required CpuZ80 Cpu { get; init; }
        public required HcMemory Memory { get; init; }
        public required ComposedFloppySlot Slot { get; init; }
        public required MediaBridge Bridge { get; init; }
        public required InputQueue Queue { get; init; }
        public required byte[] Rom { get; init; }
        public required Trace Trace { get; init; }
        public OwnerCopySlots<BaudOwnerReceipt>? BaudOwner { get; init; }
        public int TapSeq;
        public void Dispose()
        {
            BaudOwner?.Close();
            Worker.Dispose();
        }
    }

    private enum DiagnosticPurpose { CommandStart, PostLoop }
    private enum DiagnosticState { Empty, Pending, Claimed, Published, Cancelled, Missing, Failed }
    private sealed record DiagnosticRequest(Guid Epoch, int Id, DiagnosticPurpose Purpose,
        DateTime RequestedUtc, DateTime DeadlineUtc);
    private sealed record BaudOwnerReceipt(DiagnosticRequest Request, long MasterTick,
        long CpuTStates, ushort Pc, PagingLatches Latches, MemoryLocation AtDf0c,
        MemoryLocation AtDf0d, byte LogicalDf0c, byte LogicalDf0d,
        byte RamDf0c, byte RamDf0d, byte RamFf0c, byte RamFf0d);

    // Two single-use slots. Only protocol transitions use gate; owner copying is outside it.
    private sealed class OwnerCopySlots<TReceipt>(Guid epoch,
        Func<TReceipt, DiagnosticRequest> requestOf) where TReceipt : class
    {
        private sealed class Slot
        {
            public DiagnosticState State;
            public DiagnosticRequest? Request;
            public readonly TaskCompletionSource<TReceipt?> Completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private readonly object gate = new();
        private readonly Slot[] slots = [new(), new()];
        private bool closed;
        private int pending;
        public bool HasPending => Volatile.Read(ref pending) != 0;
        private void UpdatePending() => Volatile.Write(ref pending,
            slots.Any(slot => slot.State == DiagnosticState.Pending) ? 1 : 0);
        public Task<TReceipt?> Request(DiagnosticPurpose purpose, DateTime deadline)
        {
            lock (gate)
            {
                var index = (int)purpose;
                if (closed || (uint)index >= 2 || slots[index].State != DiagnosticState.Empty)
                    throw new InvalidOperationException("Diagnostic request refused");
                var slot = slots[index];
                slot.Request = new(epoch, index + 1, purpose, DateTime.UtcNow, deadline);
                slot.State = DiagnosticState.Pending;
                UpdatePending();
                return slot.Completion.Task;
            }
        }

        public void Cancel(DiagnosticPurpose purpose)
        {
            lock (gate)
            {
                var slot = slots[(int)purpose];
                if (slot.State is DiagnosticState.Pending or DiagnosticState.Claimed)
                {
                    slot.State = DiagnosticState.Cancelled;
                    UpdatePending();
                    slot.Completion.TrySetCanceled();
                }
            }
        }

        public void Close()
        {
            lock (gate)
            {
                closed = true;
                Volatile.Write(ref pending, 0);
                foreach (var slot in slots)
                    if (slot.State is DiagnosticState.Empty or DiagnosticState.Pending or DiagnosticState.Claimed)
                    {
                        slot.State = DiagnosticState.Missing;
                        slot.Completion.TrySetCanceled();
                    }
            }
        }

        public DiagnosticState State(DiagnosticPurpose purpose)
        {
            lock (gate) return slots[(int)purpose].State;
        }

        public void Service(Guid ownerEpoch, Func<DiagnosticRequest, TReceipt> capture)
        {
            for (var i = 0; i < 2; i++)
            {
                DiagnosticRequest request;
                lock (gate)
                {
                    var slot = slots[i];
                    if (closed || ownerEpoch != epoch || slot.State != DiagnosticState.Pending) continue;
                    request = slot.Request!;
                    slot.State = DiagnosticState.Claimed;
                    UpdatePending();
                }
                TReceipt? receipt = null;
                try { receipt = capture(request); }
                catch (Exception) { /* Diagnostic failure only; never fault the machine worker. */ }
                lock (gate)
                {
                    var slot = slots[i];
                    if (closed || slot.State != DiagnosticState.Claimed || slot.Request != request) continue;
                    var valid = receipt is not null && requestOf(receipt) == request;
                    slot.State = valid ? DiagnosticState.Published : DiagnosticState.Failed;
                    slot.Completion.TrySetResult(valid ? receipt : null);
                }
            }
        }

    }

    // Owner instruction boundary only; ordinary memory access cannot advance the machine.
    private static BaudOwnerReceipt CaptureBaudOwner(DiagnosticRequest request,
        CpuZ80 cpu, HcMemory memory, long masterTick)
    {
        var ram = memory.Ram.Span;
        return new(request, masterTick, cpu.TStates, cpu.R.PC, memory.Latches,
            memory.Resolve(0xdf0c), memory.Resolve(0xdf0d),
            memory.Read(0xdf0c), memory.Read(0xdf0d),
            ram[0xdf0c], ram[0xdf0d], ram[0xff0c], ram[0xff0d]);
    }

    private static OracleContext Build(string assetRoot, bool captureConsole = false,
        bool captureFdc = false, bool captureBaudOwner = false)
    {
        var roms = ProfileRomSelection.LoadWholeChipV2(assetRoot);
        var profile = roms.Profile;
        var basic = roms.Basic;
        var cpm = roms.Cpm;
        var if1 = roms.If1;

        var slot = new ComposedFloppySlot();
        var queue = new InputQueue();
        var bridge = new MediaBridge(queue);
        RuntimeContext? context = null;
        MediaSession? session = null;
        var input = new InputController(queue, (control, _) =>
        {
            if (control.Kind == InputControlKind.Shutdown) context?.Shutdown();
        }, media: (command, _) =>
        {
            var floppy = slot.Controller ?? throw new InvalidOperationException("FDC not composed");
            session ??= new MediaSession(floppy.DriveA, floppy.DriveB);
            bridge.Publish(session.Apply(command));
        });

        CpuZ80? cpuRef = null;
        HcMemory? memRef = null;
        var trace = new Trace();
        var ownerEpoch = Guid.NewGuid();
        var baudOwner = captureBaudOwner
            ? new OwnerCopySlots<BaudOwnerReceipt>(ownerEpoch, receipt => receipt.Request) : null;
        using var ready = new ManualResetEventSlim();

        LiveMachine BuildMachine()
        {
            var scheduler = new DeviceScheduler();
            var clock = new FrameClock(scheduler, 1);
            var mapper = CpuTimeMapper.FromProfile(profile);
            var adapter = new CpuDeviceTimeAdapter(scheduler, mapper);
            var memory = new HcMemory(profile.Memory with { RomProvenance = "M27C512 whole-chip EPROM" },
                basic, cpm, if1);
            var keyboard = new KeyboardMatrix();
            var ula = new Ula(memory, keyboard, scheduler, clock, 2, profile.Video);
            var floppy = new FloppyController(scheduler, 100, new FloppyDrive(new()), new FloppyDrive(new()));
            slot.Publish(floppy);
            var driveLatch = new PortDevice(
                read: (_, _) => floppy.ReadLatch(),
                write: (_, value, _) => floppy.WriteLatch(value));
            var fdcStatus = new PortDevice(read: (_, _) => floppy.Fdc.ReadStatus());
            var fdcData = new PortDevice(
                read: (_, _) =>
                {
                    slot.NoteReadData();
                    return floppy.Fdc.ReadData();
                },
                write: (_, value, _) =>
                {
                    var commandByte = captureFdc && floppy.Fdc.Phase == FdcPhase.Command;
                    floppy.Fdc.WriteData(value);
                    if (commandByte)
                    {
                        // Owner-side read-only observation after an original guest FDC command completes.
                        var snapshot = floppy.Fdc.Inspect();
                        if ((snapshot.Phase != FdcPhase.Command || snapshot.Command.Count == 0) && snapshot.LastCommand.Count != 0 &&
                            (snapshot.LastCommand[0] & 0x1f) is 5 or 6)
                            trace.FdcCommands.Enqueue(snapshot.LastCommand.ToArray());
                    }
                });
            var idle = new PortDevice();
            var ports = new PortDecoder(HcIoProfile.Faithful(memory, ula, driveLatch, fdcStatus, fdcData, idle, idle));
            var machineBus = new HcMachineBus(memory, ports, adapter, scheduler);
            var timedBus = new CpuTimedBus(machineBus, adapter);
            IM1FetchBus observedBus = captureConsole
                ? new EntryFetchBus(timedBus, () => cpuRef!.R.C, memory.Resolve, trace.EntryFetches,
                    () => { Interlocked.Increment(ref trace.EntryFetchOverflow); })
                : timedBus;
            var cpu = new CpuZ80(observedBus);
            timedBus.Attach(cpu);
            clock.InterruptChanged += (_, asserted) => cpu.IntLine = asserted;
            cpuRef = cpu;
            memRef = memory;
            ready.Set();
            return new LiveMachine(scheduler, clock, adapter, ula, () =>
            {
                // Read-only BIOS CONOUT observation; never patch guest memory or inject BDOS results.
                if (captureConsole && cpu.R.PC == 0xe107 && trace.Console.Count < 8192)
                    trace.Console.Enqueue(cpu.R.C);
                if (captureConsole && cpu.R.PC == 0) Interlocked.Increment(ref trace.ZeroVectorEntries);
                cpu.Step();
            }, () => cpu.TStates);
        }

        var worker = new EmulationWorker(
            BuildMachine,
            ctx =>
            {
                context = ctx;
                input.ApplyPending(ctx.Machine.MasterTick, ctx.Machine.Keyboard);
                var keys = new int[40];
                for (var row = 0; row < 8; row++)
                {
                    var value = ctx.Machine.Keyboard.Read((ushort)(0xffff & ~(1 << (row + 8))));
                    for (var col = 0; col < 5; col++) keys[row * 5 + col] = (value & (1 << col)) == 0 ? 1 : 0;
                }
                if (!keys.SequenceEqual(trace.PreviousKeys))
                {
                    var held = Enumerable.Range(0, keys.Length).Where(i => keys[i] != 0).Select(i => (HcKey)i).ToArray();
                    foreach (var key in held) trace.SeenKeys.TryAdd(key, 0);
                    trace.Events.Enqueue($"matrix={string.Join(',', held)} PC={cpuRef!.R.PC:X4}");
                    if (trace.PreviousKeys[(int)HcKey.X] == 1 && keys[(int)HcKey.X] == 0)
                    {
                        var screen = memRef!.Ram.Span.Slice(memRef.VideoBase, 0x1b00).ToArray();
                        trace.Events.Enqueue($"screen RAM after X: base={memRef.VideoBase:X4}, nonzero={screen.Count(b => b != 0)}, sha256={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(screen))}, hex={Convert.ToHexString(screen)}");
                    }
                    trace.PreviousKeys = keys;
                }
                // Same natural owner boundary; no additional input, wait or machine step.
                if (ctx.Stopping || ctx.Resetting) baudOwner?.Close();
                else if (baudOwner is { HasPending: true })
                    baudOwner.Service(ownerEpoch, request => CaptureBaudOwner(request,
                        cpuRef!, memRef!, ctx.Machine.MasterTick));
                var pc = cpuRef!.R.PC;
                if ((pc is 0x386e or 0x387d or 0xf800 or 0xf80e or 0x8000 or 0xe000) && trace.Milestones.Add(pc))
                    trace.Events.Enqueue($"PC milestone={pc:X4}");
            },
            cleanup: ctx =>
            {
                // Reset/final cleanup permanently retires this Build's single-use epoch.
                baudOwner?.Close();
                input.ReleaseOwned(ctx.Machine.Keyboard);
            });

        // Wait for the machine to be composed
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)), "Machine composition timed out");
        Assert.NotNull(cpuRef);
        Assert.NotNull(memRef);

        return new OracleContext
        {
            Worker = worker,
            Cpu = cpuRef!,
            Memory = memRef!,
            Slot = slot,
            Bridge = bridge,
            Queue = queue,
            Rom = basic,
            Trace = trace,
            BaudOwner = baudOwner,
        };
    }

    // ── Test 1: BASIC boot + input — type x+Enter, verify CPU ran and screen has text ──
    [Fact]
    public async Task Test1_BasicBootAndInput()
    {
        var root = RomSelectionFixture.RepositoryRoot();
        var ctx = Build(root);
        using var worker = ctx.Worker;

        // Wait for BASIC banner, keeping the frame
        var bannerFrame = await WaitForDecodedFrame(worker, ctx.Rom, text =>
            text.Contains("HC2000", StringComparison.OrdinalIgnoreCase) || text.Contains("ICE FELIX", StringComparison.Ordinal),
            TimeSpan.FromSeconds(20));
        var bannerScreen = DecodeBasicScreen(bannerFrame, ctx.Rom);
        var initialPc = ctx.Cpu.R.PC;
        var initialTicks = ctx.Cpu.TStates;

        // In Sinclair K mode the physical X key enters CLEAR, not the literal x.
        await Tap(ctx, HcKey.X);
        var keyFrame = await WaitForDecodedFrame(worker, ctx.Rom,
            text => text.Contains("CLEAR", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        output.WriteLine($"Text after X, before Return:\n{DecodeBasicScreen(keyFrame, ctx.Rom)}");
        await Tap(ctx, HcKey.Enter);

        // Wait for a frame after the keystroke
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        VideoFrame? postFrame = null;
        while (DateTime.UtcNow < deadline)
        {
            if (worker.TakeLatestFrame() is { } f) { postFrame = f; break; }
            if (worker.Completion.IsCompleted) await worker.Completion;
            await Task.Delay(15);
        }
        Assert.NotNull(postFrame);
        var postScreen = DecodeBasicScreen(postFrame!, ctx.Rom);
        var postEchoPc = ctx.Cpu.R.PC;
        var postEchoTicks = ctx.Cpu.TStates;

        // Verdict checks:
        // 1. CPU was running before input (PC != 0xFFFF reset value)
        Assert.True(initialPc != 0xFFFF,
            $"TEST1: CPU at reset PC=FFFF — machine did not boot. Ticks={initialTicks}");

        Assert.True(ctx.Trace.SeenKeys.ContainsKey(HcKey.X), "Input gap: X never reached matrix");
        Assert.True(ctx.Trace.SeenKeys.ContainsKey(HcKey.Enter), "Input gap: Return never reached matrix");

        // 2. CPU continues running; PC movement alone is NOT proof of input.
        Assert.True(initialPc != postEchoPc,
            $"TEST1: PC did not move after input. PC={initialPc:X4} Ticks={initialTicks}\nScreen:\n{postScreen}");
        Assert.True(postEchoTicks > initialTicks,
            $"TEST1: Ticks did not advance. Pre={initialTicks} Post={postEchoTicks}");

        // 3. Screen has content (not grey/blank) — at least 2 distinct colors
        var postColors = PixelColorCounts(postFrame).Count;
        Assert.True(postColors >= 2,
            $"TEST1: post-input screen is grey ({postColors} color). PC={postEchoPc:X4}\nScreen:\n{postScreen}");

        // 4. Banner was visible before input (machine booted to BASIC)
        Assert.True(bannerScreen.Length > 0,
            $"TEST1: banner frame was empty. PC={initialPc:X4} Ticks={initialTicks}");

        // 5. Post-input screen has visible text (BASIC responded)
        var nonSpaceLines = postScreen.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        Assert.True(nonSpaceLines.Count > 0,
            $"TEST1: post-input screen is all spaces. PC={postEchoPc:X4} Ticks={postEchoTicks}\nScreen:\n{postScreen}");

        // Log for the verdict
        output.WriteLine(string.Join('\n', ctx.Trace.Events));
        output.WriteLine($"Post-input text:\n{postScreen}");
        output.WriteLine($"TEST1 VERDICT: PC {initialPc:X4}→{postEchoPc:X4}, Ticks {initialTicks}→{postEchoTicks}, " +
            $"Colors={postColors}, TextLines={nonSpaceLines.Count}. Input landed, CPU ran, screen has text.");
    }

    // ── Test 2: CP/M warm boot — PRINT USR 14446 + Enter ─────────────────────
    [Fact]
    public Task Test2_CpmWarmBoot_PrintUsr14446() => CpmWarmBoot();

    [Fact]
    public Task OriginalDOnI04WholeChipMatchesMountedDirectoryAndAllocation() => CpmWarmBoot(proveD: true);

    [Fact]
    public Task OriginalDipOnI04WholeChipQueryRefusalPreservesMedia() => CpmWarmBoot(proveDip: true);

    [Fact]
    public Task OriginalDipOnI04WholeChipAffirmativeCopyAndVerify() => CpmWarmBoot(proveDipCopy: true);

    private async Task CpmWarmBoot(bool proveD = false, bool proveDip = false, bool proveDipCopy = false, bool proveMbasic = false,
        Func<OracleContext, string, Task>? prove = null)
    {
        var root = RomSelectionFixture.RepositoryRoot();
        var diskPath = Path.Combine(root, "disks", "cpm", "CPM22-HC.DSK");
        Assert.True(File.Exists(diskPath), $"CPM22-HC.DSK not found at {diskPath}");

        var ctx = Build(root, captureConsole: proveD || proveDip || proveDipCopy || proveMbasic || prove is not null, captureFdc: proveDipCopy);
        using var worker = ctx.Worker;

        // Wait for BASIC banner
        await WaitForDecodedFrame(worker, ctx.Rom, text =>
            text.Contains("HC2000", StringComparison.OrdinalIgnoreCase) || text.Contains("ICE FELIX", StringComparison.Ordinal),
            TimeSpan.FromSeconds(20));

        // Exercise the first-key path in the same boot before the CP/M trigger.
        await Tap(ctx, HcKey.X);
        await Tap(ctx, HcKey.Enter);

        // Insert disk
        var disk = ImmutableArray.CreateRange(await File.ReadAllBytesAsync(diskPath));
        var inserted = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.InsertDisk, DiskSlot.A, disk, "CPM22-HC.DSK"));
        Assert.Null(inserted.Error);
        Assert.True(inserted.A.Present);

        // Record pre-boot state
        var preBootPc = ctx.Cpu.R.PC;
        var preBootTicks = ctx.Cpu.TStates;
        var preBootReadData = ctx.Slot.ReadData;

        // Type: P Space [Caps+Symbol] L Space 1 4 4 4 6 Enter
        await Tap(ctx, HcKey.P);
        await Tap(ctx, HcKey.Space);
        await Hold(ctx, HcKey.CapsShift, HcKey.SymbolShift);
        await Tap(ctx, HcKey.L);
        await Tap(ctx, HcKey.Space);
        await Tap(ctx, HcKey.D1);
        await Tap(ctx, HcKey.D4);
        await Tap(ctx, HcKey.D4);
        await Tap(ctx, HcKey.D4);
        await Tap(ctx, HcKey.D6);
        await Tap(ctx, HcKey.Enter);

        // Wait up to 75s for A> prompt or worker completion
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(75);
        string lastScreen = "(no frame)";
        bool sawPrompt = false;
        while (DateTime.UtcNow < deadline)
        {
            if (worker.TakeLatestFrame() is { } frame)
            {
                lastScreen = DecodeBasicScreen(frame, ctx.Rom);
                if (lastScreen.Contains("A>", StringComparison.Ordinal) || FrameShowsAPrompt(frame))
                {
                    sawPrompt = true;
                    break;
                }
            }
            if (worker.Completion.IsCompleted) await worker.Completion;
            await Task.Delay(20);
        }

        VideoFrame? settled = null;
        if (sawPrompt)
        {
            await WaitFramesKeeping(worker, 30, frame => settled = frame);
            Assert.NotNull(settled);
            Assert.True(FrameShowsAPrompt(settled!), "CP/M A> must persist after settling");
        }

        var postBootPc = ctx.Cpu.R.PC;
        var postBootTicks = ctx.Cpu.TStates;
        var postBootReadData = ctx.Slot.ReadData;
        var readDelta = postBootReadData - preBootReadData;

        output.WriteLine(string.Join('\n', ctx.Trace.Events));
        output.WriteLine($"CP/M: PC={postBootPc:X4}, FDC ReadData delta={readDelta}, prompt={sawPrompt}\n{lastScreen}");
        Assert.Contains("PC milestone=386E", ctx.Trace.Events);
        Assert.True(readDelta > 0,
            $"TEST2 FAIL (FDC leg): no FDC READ DATA after PRINT USR 14446.\n" +
            $"Pre: PC={preBootPc:X4} Ticks={preBootTicks} ReadData={preBootReadData}\n" +
            $"Post: PC={postBootPc:X4} Ticks={postBootTicks} ReadData={postBootReadData} (delta={readDelta})\n" +
            $"Last screen:\n{lastScreen}");

        Assert.True(sawPrompt,
            $"TEST2 FAIL (boot leg): CP/M warm boot did not reach A>.\n" +
            $"Pre: PC={preBootPc:X4} Ticks={preBootTicks}\n" +
            $"Post: PC={postBootPc:X4} Ticks={postBootTicks} ReadData={postBootReadData} (delta={readDelta})\n" +
            $"Last screen:\n{lastScreen}");

        if (proveD) await ProveOriginalD(ctx, diskPath);
        if (proveDip) await ProveOriginalDip(ctx, diskPath);
        if (proveDipCopy) await ProveOriginalDipCopy(ctx, diskPath);
        if (proveMbasic) await ProveOriginalMbasic(ctx, diskPath);
        if (prove is not null) await prove(ctx, diskPath);
    }

    private async Task ProveOriginalD(OracleContext ctx, string diskPath)
    {
        var source = await File.ReadAllBytesAsync(diskPath);
        var sourceHash = Hash(source);
        Assert.Equal("ff5a1c541c8aa80778a0f44d3ece398013084aafd9be1d3f32d2f68b774bb008", sourceHash);
        var before = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.ExportDisk));
        Assert.Null(before.Error);
        var parsed = Dsk.Load(before.Export.AsSpan());
        var fs = CpmFs.Open(parsed, "cpm-hc");
        var dpb = fs.Parameters;
        Assert.Equal("cpm-hc80", dpb.Name);
        var d = fs.Get("D.COM");
        Assert.Equal(4608, d.Length);
        Assert.Equal("fb8e4d11948e9b8c068f72a6527e9fb0b5264597fc94873fe85408a999d2767a", Hash(d));
        var files = fs.List();
        var visible = files.Where(file => file.User == 0 && !file.System).ToArray();

        // Independent of guest BDOS/CONOUT: walk all directory extents and word allocations.
        var logical = ReadLogical(parsed, dpb);
        Assert.Equal(ReadLogical(Dsk.Load(source), dpb), logical);
        var start = dpb.Off * dpb.Spt * 128;
        // The held preset advertises DSM=351, but 80x2x9x512 minus four sides
        // holds only 351 whole blocks (0..350). Preserve the discrepancy, don't count
        // an unbacked block as writable free space (same physical bound as CpmFs).
        var usableBlocks = Math.Min(dpb.Dsm + 1, (logical.Length - start) / dpb.BlockSize);
        Assert.Equal(fs.MaximumBlock + 1, usableBlocks);
        var allocated = new HashSet<int>(Enumerable.Range(0, dpb.DirectoryBlocks));
        var liveExtents = 0;
        for (var i = 0; i <= dpb.Drm; i++)
        {
            var entry = logical.AsSpan(start + i * 32, 32);
            if (entry[0] == 0xe5) continue;
            liveExtents++;
            for (var j = 16; j < 32; j += 2)
            {
                var block = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(j, 2));
                if (block == 0) continue;
                Assert.InRange((int)block, dpb.DirectoryBlocks, usableBlocks - 1);
                Assert.True(allocated.Add(block), $"Duplicate allocation block {block}");
            }
        }
        var freeK = (usableBlocks - allocated.Count) * dpb.BlockSize / 1024;
        output.WriteLine($"REFERENCE GAP: no held D manual/real-HC transcript; this is media consistency, NOT physical parity.");
        output.WriteLine($"I04 sourceSHA256={sourceHash}; D.COM bytes={d.Length} SHA256={Hash(d)}");
        var root = RomSelectionFixture.RepositoryRoot();
        var romHash = Hash(await File.ReadAllBytesAsync(Path.Combine(root, ProfileRomSelection.WholeChipSourcePath)));
        Assert.Equal("83e3213b049c5b849ee32e04da901eba06992d5aa1f44f76564f312c79748d2b", romHash);
        output.WriteLine($"M27C512 SHA256={romHash}");
        output.WriteLine($"Independent DPB: OFF={dpb.Off} SPT={dpb.Spt} DSM={dpb.Dsm} DRM={dpb.Drm} block={dpb.BlockSize}, physicalUsableBlocks={usableBlocks}; liveExtents={liveExtents}, allUsersFiles={files.Count}, reserved+allocated={allocated.Count}, freeK={freeK}, visibleUser0={visible.Length}");
        output.WriteLine($"Published-only freeK={(dpb.Dsm + 1 - allocated.Count) * dpb.BlockSize / 1024} is NOT physically backed; filesystem warnings: {string.Join(';', fs.Warnings)}");
        output.WriteLine($"Visible names: {string.Join(',', visible.Select(file => file.Name))}; system names: {string.Join(',', files.Where(file => file.System).Select(file => file.Name))}");
        var tickBefore = ctx.Worker.TakeLatestFrame()?.MasterTick ?? 0;
        await Tap(ctx, HcKey.D);
        await Tap(ctx, HcKey.Enter);
        VideoFrame? frame = null;
        await WaitFramesKeeping(ctx.Worker, 300, value => frame = value);
        var console = Encoding.ASCII.GetString(ctx.Trace.Console.ToArray());
        output.WriteLine($"D inputs=D,ENTER; 300 post-input presented frames; finalTick={frame!.MasterTick}; beforeTick={tickBefore}; CONOUT ({console.Length} chars):\n{console}");
        Assert.True(console.Length < 8192, "Bounded CONOUT observation overflow");
        Assert.Contains($"File Count: {visible.Length}, Free R/W Space: {freeK}K", console);
        Assert.Contains("Drive: A, User: 0, System Files Present", console);
        foreach (var file in visible)
        {
            var parts = file.Name.Split('.');
            Assert.Contains(parts[0].PadRight(8) + "." + parts[1].PadRight(3), console);
        }
        Assert.EndsWith("A>", console.TrimEnd());
        Assert.True(FrameShowsAPrompt(frame), "D must return to physical A> prompt");
        var after = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.ExportDisk));
        Assert.Null(after.Error);
        Assert.Equal(0, after.A.DirtySectors);
        Assert.Equal(logical, ReadLogical(Dsk.Load(after.Export.AsSpan()), dpb));
        Assert.Equal(sourceHash, Hash(await File.ReadAllBytesAsync(diskPath)));
        output.WriteLine($"Mounted post-D logicalSHA256={Hash(logical)} unchanged; dirtySectors={after.A.DirtySectors}; source preserved.");
    }

    private async Task ProveOriginalDip(OracleContext ctx, string diskPath)
    {
        // hc-cpm manual pp6–7: Q queries each transfer; only Y/y copies, otherwise not.
        // This supporting refusal case does not exercise V/copy, P/swaps or completion parity.
        var source = await File.ReadAllBytesAsync(diskPath);
        var sourceHash = Hash(source);
        Assert.Equal("ff5a1c541c8aa80778a0f44d3ece398013084aafd9be1d3f32d2f68b774bb008", sourceHash);
        var root = RomSelectionFixture.RepositoryRoot();
        var romHash = Hash(await File.ReadAllBytesAsync(Path.Combine(root, ProfileRomSelection.WholeChipSourcePath)));
        Assert.Equal("83e3213b049c5b849ee32e04da901eba06992d5aa1f44f76564f312c79748d2b", romHash);
        var before = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.ExportDisk));
        Assert.Null(before.Error);
        var disk = Dsk.Load(before.Export.AsSpan());
        var fs = CpmFs.Open(disk, "cpm-hc");
        var dip = fs.Get("DIP.COM");
        Assert.Equal(3328, dip.Length);
        Assert.Equal("0b20f9ad3b402a6c641a5757d61f9e9c87850561277fa0c92f78a5aebfb2f54e", Hash(dip));
        var logical = ReadLogical(disk, fs.Parameters);
        Assert.Equal(ReadLogical(Dsk.Load(source), fs.Parameters), logical);
        Assert.Equal(0, before.A.DirtySectors);
        Assert.DoesNotContain(fs.List(), file => file.User == 0 && file.Name == "DIPQ.COM");
        output.WriteLine($"I04 sourceSHA256={sourceHash}; M27C512={romHash}; DIP.COM user0 bytes={dip.Length} SHA256={Hash(dip)}");
        while (ctx.Trace.Console.TryDequeue(out _)) { }

        // Type DIP DIPQ.COM=D.COM[Q]; the destination does not exist before refusal.
        await Tap(ctx, HcKey.D);
        await Tap(ctx, HcKey.I);
        await Tap(ctx, HcKey.P);
        await Tap(ctx, HcKey.Space);
        await Tap(ctx, HcKey.D);
        await Tap(ctx, HcKey.I);
        await Tap(ctx, HcKey.P);
        await Tap(ctx, HcKey.Q);
        await Hold(ctx, HcKey.SymbolShift, HcKey.M); // .
        await Tap(ctx, HcKey.C);
        await Tap(ctx, HcKey.O);
        await Tap(ctx, HcKey.M);
        await Hold(ctx, HcKey.SymbolShift, HcKey.L); // =
        await Tap(ctx, HcKey.D);
        await Hold(ctx, HcKey.SymbolShift, HcKey.M); // .
        await Tap(ctx, HcKey.C);
        await Tap(ctx, HcKey.O);
        await Tap(ctx, HcKey.M);
        await Hold(ctx, HcKey.SymbolShift, HcKey.Y); // [
        await Tap(ctx, HcKey.Q);
        await Hold(ctx, HcKey.SymbolShift, HcKey.U); // ]
        await Tap(ctx, HcKey.Enter);
        await WaitFramesKeeping(ctx.Worker, 150, _ => { });
        var sourcePrompt = ConsoleText(ctx);
        output.WriteLine($"DIP source prompt transcript:\n{sourcePrompt}");
        Assert.Contains("Load Source Disk in Drive A:", sourcePrompt);
        // The source is already mounted on a disposable session copy; acknowledge its prompt.
        await Tap(ctx, HcKey.Enter);
        await WaitFramesKeeping(ctx.Worker, 150, _ => { });
        var query = ConsoleText(ctx);
        output.WriteLine($"DIP query raw CONOUT hex={Convert.ToHexString(ctx.Trace.Console.ToArray())}");
        output.WriteLine($"DIP query transcript (7-bit; original file attribute bits preserved in raw):\n{query}");
        Assert.Contains("A:DIPQ.COM=A:D.COM (y/n)", query);
        await Tap(ctx, HcKey.N);
        await Tap(ctx, HcKey.Enter);
        await WaitFramesKeeping(ctx.Worker, 150, _ => { });
        var refusal = ConsoleText(ctx);
        output.WriteLine($"DIP refusal transcript:\n{refusal}");
        Assert.Contains("(y/n) ? n", refusal);
        Assert.DoesNotContain("Load Destination", refusal);
        Assert.Contains("Load System Disk in Drive A:", refusal);
        // The original system disk was never removed; acknowledge its reload prompt.
        await Tap(ctx, HcKey.Enter);
        VideoFrame? finalFrame = null;
        await WaitFramesKeeping(ctx.Worker, 300, frame => finalFrame = frame);
        var transcript = ConsoleText(ctx);
        output.WriteLine($"DIP return transcript:\n{transcript}");
        Assert.EndsWith("A>", transcript.TrimEnd());
        Assert.NotNull(finalFrame);
        Assert.True(FrameShowsAPrompt(finalFrame!), "Refusal must return to physical A> prompt");
        var after = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.ExportDisk));
        Assert.Null(after.Error);
        Assert.Equal(0, after.A.DirtySectors);
        var postDisk = Dsk.Load(after.Export.AsSpan());
        Assert.Equal(logical, ReadLogical(postDisk, fs.Parameters));
        var postFs = CpmFs.Open(postDisk, "cpm-hc");
        Assert.DoesNotContain(postFs.List(), file => file.User == 0 && file.Name == "DIPQ.COM");
        Assert.Equal(fs.Get("D.COM"), postFs.Get("D.COM"));
        Assert.Equal(sourceHash, Hash(await File.ReadAllBytesAsync(diskPath)));
        output.WriteLine($"Independent unchanged-media oracle: logicalSHA256={Hash(logical)}, D.COM readback identical, DIPQ.COM absent before/after, dirtySectors=0, source preserved. Copy/verify, v3.2 completion, other occurrences and real/installed HC parity NOT proven.");
    }

    private async Task ProveOriginalDipCopy(OracleContext ctx, string diskPath)
    {
        // Supplied hc-cpm pp6–7: destination=source, Q accepts Y, V rereads written data.
        // Held v3.2 also documents P disk-change prompts: native A-slot swaps, no alias transport.
        var root = RomSelectionFixture.RepositoryRoot();
        var original = await File.ReadAllBytesAsync(diskPath);
        Assert.Equal("ff5a1c541c8aa80778a0f44d3ece398013084aafd9be1d3f32d2f68b774bb008", Hash(original));
        var rom = await File.ReadAllBytesAsync(Path.Combine(root, ProfileRomSelection.WholeChipSourcePath));
        Assert.Equal("83e3213b049c5b849ee32e04da901eba06992d5aa1f44f76564f312c79748d2b", Hash(rom));
        var evidence = Path.Combine(Path.GetTempPath(), "hc2000-dip-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        output.WriteLine($"evidence={evidence}; DB=none; original disk={Hash(original)}; ROM={Hash(rom)}");
        void Export(string name, byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(evidence, name), bytes);
            output.WriteLine($"{name}: bytes={bytes.Length} sha256={Hash(bytes)}");
        }
        async Task<MediaReply> ReadDisk(DiskSlot slot, string name)
        {
            var reply = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.ExportDisk, slot)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(reply.Error);
            Export(name, reply.Export.ToArray());
            return reply;
        }
        var aBefore = await ReadDisk(DiskSlot.A, "a-before.dsk");
        var sourceDisk = Dsk.Load(aBefore.Export.AsSpan());
        var sourceFs = CpmFs.Open(sourceDisk, "cpm-hc");
        var dpb = sourceFs.Parameters;
        var sourceLogical = ReadLogical(sourceDisk, dpb);
        Assert.Equal(ReadLogical(Dsk.Load(original), dpb), sourceLogical);
        var dip = sourceFs.Get("DIP.COM");
        Assert.Equal(3328, dip.Length);
        Assert.Equal("0b20f9ad3b402a6c641a5757d61f9e9c87850561277fa0c92f78a5aebfb2f54e", Hash(dip));
        var payload = sourceFs.Get("D.COM");
        Assert.Equal(4608, payload.Length);
        Assert.Equal("fb8e4d11948e9b8c068f72a6527e9fb0b5264597fc94873fe85408a999d2767a", Hash(payload));
        Export("source-d.com", payload);
        var blankDisk = Dsk.Create(sourceDisk.UniformGeometry());
        var blankBytes = blankDisk.Save();
        Export("destination-before.dsk", blankBytes);
        var blankFs = CpmFs.Open(blankDisk, "cpm-hc");
        Assert.Empty(blankFs.List());
        Assert.Equal(0, aBefore.A.DirtySectors);
        Assert.DoesNotContain(blankFs.List(), f => f.Name == "DIPV.COM");
        var blankLogical = ReadLogical(blankDisk, dpb);
        while (ctx.Trace.Console.TryDequeue(out _)) { }
        string ConsoleText() => new(ctx.Trace.Console.ToArray().Select(value => (char)(value & 0x7f)).ToArray());
        async Task Send(string text, string expected)
        {
            var start = ConsoleText().Length;
            output.WriteLine($"input={text.Replace("\n", "<CR>")}; expected={expected}; bound=75s; paste cadence=16frames/edge");
            ctx.Queue.Enqueue(new PasteText(text, 16 * 355680L));
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(75);
            while (DateTime.UtcNow < until && !ConsoleText()[start..].Contains(expected, StringComparison.Ordinal))
            {
                if (ctx.Worker.Completion.IsCompleted) await ctx.Worker.Completion;
                await Task.Delay(20);
            }
            var seen = ConsoleText()[start..];
            output.WriteLine($"seen={seen}; PC={ctx.Cpu.R.PC:X4}");
            Assert.Contains(expected, seen);
            Assert.DoesNotContain("ERROR", seen, StringComparison.OrdinalIgnoreCase);
            await WaitFrames(ctx.Worker, 40);
        }
        async Task Mount(byte[] bytes, string name, bool discard = false)
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            MediaReply view;
            do
            {
                view = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.Inspect)).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Null(view.Error);
                if (!view.A.Busy) break;
                await Task.Delay(20);
            } while (DateTime.UtcNow < until);
            output.WriteLine($"native swap A -> {name}; busy={view.A.Busy}, dirty={view.A.DirtySectors}, generation={view.A.Generation}; discardSavedCopy={discard}");
            Assert.False(view.A.Busy, "Existing native drive must stop/unload; no forced motor/head mutation is permitted.");
            var mounted = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.InsertDisk, DiskSlot.A,
                ImmutableArray.CreateRange(bytes), name, DiscardChanges: discard)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(mounted.Error);
            Assert.Equal(view.A.Generation + 1, mounted.A.Generation);
        }
        await Send("DIP DIPV.COM=D.COM[QVP]\n", "Load Source Disk in Drive A:");
        await Send("\n", "A:DIPV.COM=A:D.COM (y/n)");
        await Send("Y\n", "Load Destination Disk in Drive A:");
        var sourceAfter = await ReadDisk(DiskSlot.A, "source-after.dsk");
        Assert.Equal(sourceLogical, ReadLogical(Dsk.Load(sourceAfter.Export.AsSpan()), dpb));
        Assert.Equal(0, sourceAfter.A.DirtySectors);
        await Mount(blankBytes, "dip-destination-copy.dsk");
        while (ctx.Trace.FdcCommands.TryDequeue(out _)) { }
        await Send("\n", "Load System Disk in Drive A:");
        var transferCommands = ctx.Trace.FdcCommands.ToArray();
        var writes = transferCommands.Select((command, index) => (command, index))
            .Where(item => (item.command[0] & 0x1f) == 5).ToArray();
        var fdcText = string.Join('\n', transferCommands.Select(command => Convert.ToHexString(command)));
        File.WriteAllText(Path.Combine(evidence, "destination-fdc-commands.txt"), fdcText);
        output.WriteLine($"destination write/verify commands (unit/CHRN):\n{fdcText}");
        Assert.NotEmpty(writes);
        // V verifies payload, not the directory update. Every payload WRITE DATA must have
        // a subsequent guest READ DATA of the same unit/CHRN. No synthetic FDC commands.
        var firstPayloadSector = (dpb.Off * dpb.Spt * 128 + 2 * dpb.BlockSize) / 512;
        var payloadCoordinates = Enumerable.Range(firstPayloadSector, payload.Length / 512).Select(logicalSector =>
        {
            var track = logicalSector / dpb.Geometry.Sectors;
            return (C: track / dpb.Geometry.Sides, H: track % dpb.Geometry.Sides,
                R: dpb.Skew[logicalSector % dpb.Geometry.Sectors]);
        }).ToArray();
        var payloadWrites = writes.Where(item => payloadCoordinates.Contains((item.command[2], item.command[3], item.command[4]))).ToArray();
        Assert.Equal(9, payloadWrites.Length);
        foreach (var (write, index) in payloadWrites)
            Assert.Contains(transferCommands.Skip(index + 1), read => (read[0] & 0x1f) == 6 &&
                read.AsSpan(1, 5).SequenceEqual(write.AsSpan(1, 5)));
        Assert.Contains("Copy Complete", ConsoleText());
        var destinationAfter = await ReadDisk(DiskSlot.A, "destination-after.dsk");
        await Mount(original, "system-working-copy.dsk", discard: true);
        await Send("\n", "A>");
        var transcript = ConsoleText();
        File.WriteAllText(Path.Combine(evidence, "console.txt"), transcript);
        File.WriteAllBytes(Path.Combine(evidence, "conout.bin"), ctx.Trace.Console.ToArray());
        output.WriteLine($"actual DIP transcript:\n{transcript}");
        var aAfter = await ReadDisk(DiskSlot.A, "system-after.dsk");
        var resultDisk = Dsk.Load(destinationAfter.Export.AsSpan());
        var resultFs = CpmFs.Open(resultDisk, "cpm-hc");
        var resultFile = Assert.Single(resultFs.List());
        Assert.Equal("DIPV.COM", resultFile.Name);
        Assert.Equal(0, resultFile.User);
        Assert.Equal(36, resultFile.Records);
        var result = resultFs.Get("DIPV.COM");
        Export("dipv.com", result);
        Assert.Equal(payload, result); // 36 complete 128-byte records, no EOF or allocation-slack trimming.
        Assert.Equal(Hash(payload), Hash(result));
        Assert.Equal(sourceLogical, ReadLogical(Dsk.Load(aAfter.Export.AsSpan()), dpb));
        Assert.Equal(payload, CpmFs.Open(Dsk.Load(aAfter.Export.AsSpan()), "cpm-hc").Get("D.COM"));
        Assert.Equal(0, aAfter.A.DirtySectors);
        Assert.True(destinationAfter.A.DirtySectors > 0);
        var resultLogical = ReadLogical(resultDisk, dpb);
        Assert.False(blankLogical.SequenceEqual(resultLogical));
        var directoryStart = dpb.Off * dpb.Spt * 128;
        var entry = resultLogical.AsSpan(directoryStart, 32).ToArray();
        Assert.Equal(0, entry[0]);
        Assert.Equal(36, entry[15]);
        var blocks = Enumerable.Range(0, 8).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(16 + i * 2, 2))).ToArray();
        Assert.Equal(new ushort[] { 2, 3, 4, 0, 0, 0, 0, 0 }, blocks);
        // On an empty disk first-fit allocation consumes blocks 2..4; 36 records use 9 sectors.
        // Only first directory entry and these payload bytes may change. Slack/system/free space is preserved.
        var dataStart = directoryStart + 2 * dpb.BlockSize;
        Assert.Equal(payload, resultLogical[dataStart..(dataStart + payload.Length)]);
        for (var i = 0; i < resultLogical.Length; i++)
            if (!(i >= directoryStart && i < directoryStart + 32) && !(i >= dataStart && i < dataStart + payload.Length))
                Assert.Equal(blankLogical[i], resultLogical[i]);
        var changedSectors = Enumerable.Range(0, resultLogical.Length / 512)
            .Where(i => !blankLogical.AsSpan(i * 512, 512).SequenceEqual(resultLogical.AsSpan(i * 512, 512))).ToArray();
        Assert.Equal(new[] { directoryStart / 512 }.Concat(Enumerable.Range(dataStart / 512, 9)), changedSectors);
        Assert.Equal(10, destinationAfter.A.DirtySectors);
        output.WriteLine($"expected/seen destination logical sector delta={string.Join(',', changedSectors)}; allocation2,3,4; directoryentry0 only; free/allocation slack/system bytes preserved");
        // Guest directory readback of the saved guest-written destination; no host file replacement.
        var readback = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.InsertDisk, DiskSlot.B,
            destinationAfter.Export, "dip-saved-readback.dsk")).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(readback.Error);
        await Send("DIR B:DIPV.COM\n", "A>");
        Assert.Contains("DIPV     COM", ConsoleText());
        File.WriteAllText(Path.Combine(evidence, "console.txt"), ConsoleText());
        File.WriteAllBytes(Path.Combine(evidence, "conout.bin"), ctx.Trace.Console.ToArray());
        var guestReadback = await ReadDisk(DiskSlot.B, "destination-readback.dsk");
        Assert.Equal(resultLogical, ReadLogical(Dsk.Load(guestReadback.Export.AsSpan()), dpb));
        Assert.Equal(0, guestReadback.B.DirtySectors);
        Assert.Equal(original, await File.ReadAllBytesAsync(diskPath));
        output.WriteLine($"why=original DIP accepted Y, 9 payload WRITE DATA commands each followed by matching guest READ DATA under V, Copy Complete/no error and guest DIR readback; independent exported output equals pinned source; source logical sectors unchanged, destination singleton36records/4608bytes; dirty source={sourceAfter.A.DirtySectors}, destination={destinationAfter.A.DirtySectors}; source logical={Hash(sourceLogical)}, destination before={Hash(blankLogical)}, after={Hash(resultLogical)}. Native single A-slot swaps; no installed or physical proof claimed.");
    }

    private static byte[] ReadLogical(Dsk disk, Dpb dpb) => disk.Tracks
        .OrderBy(track => track.Cylinder).ThenBy(track => track.Side)
        .SelectMany(track => dpb.Skew.SelectMany(id => track.Sectors.Single(sector => sector.R == id).Data))
        .ToArray();

    // CP/M directory names can retain high attribute bits (e.g. D.COM's read-only C).
    // Decode to seven-bit characters for text assertions only; retain the raw observation above.
    private static string ConsoleText(OracleContext ctx) =>
        Encoding.ASCII.GetString(ctx.Trace.Console.Select(value => (byte)(value & 0x7f)).ToArray());

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // ── Test 3: Grey-frame check — after first keystroke, screen must NOT be all-grey ─
    [Fact]
    public async Task Test3_GreyFrame_AfterKeystroke()
    {
        var root = RomSelectionFixture.RepositoryRoot();
        var ctx = Build(root);
        using var worker = ctx.Worker;

        // Wait for BASIC banner, keeping the frame
        var bannerFrame = await WaitForDecodedFrame(worker, ctx.Rom, text =>
            text.Contains("HC2000", StringComparison.OrdinalIgnoreCase) || text.Contains("ICE FELIX", StringComparison.Ordinal),
            TimeSpan.FromSeconds(20));
        var preScreen = DecodeBasicScreen(bannerFrame, ctx.Rom);
        var preColors = PixelColorCounts(bannerFrame).Count;

        // In Sinclair K mode the physical X key enters CLEAR, not the literal x.
        await Tap(ctx, HcKey.X);
        var keyFrame = await WaitForDecodedFrame(worker, ctx.Rom,
            text => text.Contains("CLEAR", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        output.WriteLine($"Text after X, before Return:\n{DecodeBasicScreen(keyFrame, ctx.Rom)}");
        await Tap(ctx, HcKey.Enter);

        // Wait a few frames, keeping the last
        VideoFrame? postFrame = null;
        await WaitFramesKeeping(worker, 20, f => postFrame = f);

        Assert.NotNull(postFrame);
        var postColors = PixelColorCounts(postFrame!).Count;
        var postScreen = DecodeBasicScreen(postFrame!, ctx.Rom);

        // A healthy frame has multiple colors (background + text).
        // A grey/blank frame would have 1-2 colors only.
        Assert.True(preColors >= 2,
            $"TEST3: pre-keystroke frame has {preColors} colors — banner not visible.\nScreen:\n{preScreen}");

        Assert.True(postColors >= 2,
            $"TEST3 FAIL: post-keystroke frame is grey (all one color).\n" +
            $"Pre colors: {preColors}, Post colors: {postColors}\n" +
            $"Post screen:\n{postScreen}");
    }

    [Fact]
    public async Task Test4_WholeChipGuestPipRoundTrip()
    {
        // CP/M 2.2 manual CH1 §1.6.4, Table 1-4: destination=source[OV].
        // O ignores text EOF; V rereads the destination after writing.
        var root = RomSelectionFixture.RepositoryRoot();
        var path = Path.Combine(root, "disks/cpm/CPM22-HC.DSK");
        var originalDisk = await File.ReadAllBytesAsync(path);
        var evidence = Path.Combine(Path.GetTempPath(), "hc2000-pip-wholechip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        void Export(string name, byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(evidence, name), bytes);
            output.WriteLine($"{name}: bytes={bytes.Length} sha256={Hash(bytes)}");
        }
        var sourceFs = Hc2000.Machine.Storage.CpmFs.Open(Hc2000.Machine.Storage.Dsk.Load(originalDisk), "cpm-hc80");
        Assert.DoesNotContain(sourceFs.List(), f => f.Name is "ROUND.COM" or "RETURN.COM");
        var source = sourceFs.Get("PIP.COM");
        Export("original.com", source);
        output.WriteLine($"evidence={evidence}; DB=none; source disk SHA256={Hash(originalDisk)}");
        using var ctx = Build(root, captureConsole: true);
        foreach (var relative in new[] { ProfileRomSelection.WholeChipSourcePath })
            output.WriteLine($"{relative}: sha256={Hash(File.ReadAllBytes(Path.Combine(root, relative)))}");
        await WaitForDecodedFrame(ctx.Worker, ctx.Rom, text => text.Contains("HC2000", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(20));
        async Task Mount(DiskSlot slot, byte[] bytes)
        {
            var reply = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.InsertDisk, slot, ImmutableArray.CreateRange(bytes), "disposable.dsk")).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(reply.Error);
        }
        await Mount(DiskSlot.A, originalDisk);
        var blank = Hc2000.Machine.Storage.Dsk.Create(new Hc2000.Machine.Storage.DiskGeometry(80, 2, 9, 512)).Save();
        Export("b-before.dsk", blank);
        await Mount(DiskSlot.B, blank);
        await Tap(ctx, HcKey.P); await Tap(ctx, HcKey.Space);
        await Hold(ctx, HcKey.CapsShift, HcKey.SymbolShift);
        await Tap(ctx, HcKey.L); await Tap(ctx, HcKey.Space);
        foreach (var key in new[] { HcKey.D1, HcKey.D4, HcKey.D4, HcKey.D4, HcKey.D6, HcKey.Enter }) await Tap(ctx, key);
        string ConsoleText() => new(ctx.Trace.Console.ToArray().Select(value => (char)value).ToArray());
        async Task WaitConsole(int start, string expected)
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(75);
            while (DateTime.UtcNow < until && !ConsoleText()[start..].Contains(expected, StringComparison.Ordinal))
            {
                if (ctx.Worker.Completion.IsCompleted) await ctx.Worker.Completion;
                await Task.Delay(20);
            }
            var seen = ConsoleText()[start..];
            output.WriteLine($"expected={expected}; seen={seen}; PC={ctx.Cpu.R.PC:X4}");
            Assert.Contains(expected, seen);
            Assert.DoesNotContain("ERROR", seen, StringComparison.OrdinalIgnoreCase);
        }
        await WaitConsole(0, "A>");
        Assert.Contains("CP/M ver 2.2", ConsoleText());
        await WaitFrames(ctx.Worker, 40);
        async Task Send(string text, string expected = "A>")
        {
            var start = ConsoleText().Length;
            output.WriteLine($"command={text.TrimEnd()}; bound=75s; cadence=16 frames/edge");
            ctx.Queue.Enqueue(new PasteText(text, 16 * 355680L));
            await WaitConsole(start, expected);
            await WaitFrames(ctx.Worker, 40);
        }
        await Send("PIP B:ROUND.COM=A:PIP.COM[OV]\n");
        await Send("DIR B:\n");
        Assert.Contains("ROUND    COM", ConsoleText());
        await Send("PIP A:RETURN.COM=B:ROUND.COM[OV]\n");
        await Send("DIR A:\n");
        Assert.Contains("RETURN   COM", ConsoleText());
        async Task<byte[]> ReadDisk(DiskSlot slot, string name)
        {
            var reply = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.ExportDisk, slot)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(reply.Error);
            var bytes = reply.Export.ToArray();
            Export(name, bytes);
            return bytes;
        }
        var a = Hc2000.Machine.Storage.CpmFs.Open(Hc2000.Machine.Storage.Dsk.Load(await ReadDisk(DiskSlot.A, "a-after.dsk")), "cpm-hc80");
        var b = Hc2000.Machine.Storage.CpmFs.Open(Hc2000.Machine.Storage.Dsk.Load(await ReadDisk(DiskSlot.B, "b-after.dsk")), "cpm-hc80");
        Assert.Single(b.List());
        Assert.Equal(sourceFs.List().Count + 1, a.List().Count);
        var round = b.Get("ROUND.COM"); var returned = a.Get("RETURN.COM");
        Export("round.com", round); Export("return.com", returned);
        Assert.Equal(7424, source.Length); // 58 complete CP/M 128-byte records, including final-record padding.
        Assert.Equal(source, round); Assert.Equal(source, returned); Assert.Equal(source, a.Get("PIP.COM"));
        Assert.Equal(Hash(source), Hash(round)); Assert.Equal(Hash(source), Hash(returned));
        Assert.Equal(originalDisk, await File.ReadAllBytesAsync(path));
        output.WriteLine("why=original guest PIP [OV] returned without error; V guest reread plus DIR and host-export equality; 7424=58*128, no EOF/padding trimming; allocation slack excluded. Real-machine and desktop demo remain unverified.");
        File.WriteAllText(Path.Combine(evidence, "console.txt"), ConsoleText());
    }

    [Fact]
    public async Task Test5_WholeChipGuestLoadConvertsPinnedHex()
    {
        // CP/M 2.2 manual CH1 §1.6.3: LOAD X reads X.HEX, writes X.COM from 100H.
        // Test data only: BDOS function 2 prints L then RET. No host HEX conversion.
        byte[] payload = [0x0e, 0x02, 0x1e, 0x4c, 0xcd, 0x05, 0x00, 0xc9];
        const string hex = ":080100000E021E4CCD0500C9E2\r\n:00000001FF\r\n";
        foreach (var line in hex.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var record = Convert.FromHexString(line[1..]);
            Assert.Equal(0, record.Sum(x => (int)x) & 255);
        }
        var root = RomSelectionFixture.RepositoryRoot();
        var path = Path.Combine(root, "disks/cpm/CPM22-HC.DSK");
        var originalDisk = await File.ReadAllBytesAsync(path);
        var evidence = Path.Combine(Path.GetTempPath(), "hc2000-load-wholechip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        void Export(string name, byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(evidence, name), bytes);
            output.WriteLine($"{name}: bytes={bytes.Length} sha256={Hash(bytes)}");
        }
        var sourceFs = Hc2000.Machine.Storage.CpmFs.Open(Hc2000.Machine.Storage.Dsk.Load(originalDisk), "cpm-hc80");
        Assert.DoesNotContain(sourceFs.List(), f => f.Name is "LDFIX.COM" or "LDFIX.HEX");
        var source = sourceFs.Get("LOAD.COM");
        Assert.Equal("c885d061a5dcb3830ab1c29a13e34d3c0560cc3faf569bc55b195c0330a9cbd6", Hash(source));
        Export("load-original.com", source);
        Export("fixture.hex", System.Text.Encoding.ASCII.GetBytes(hex));
        Export("expected-payload.bin", payload);
        var prepared = Hc2000.Machine.Storage.Dsk.Load(originalDisk);
        Hc2000.Machine.Storage.CpmFs.Open(prepared, "cpm-hc80").Put("LDFIX.HEX", System.Text.Encoding.ASCII.GetBytes(hex));
        Export("a-before.dsk", prepared.Save());
        output.WriteLine($"evidence={evidence}; DB=none; source disk SHA256={Hash(originalDisk)}");
        using var ctx = Build(root, captureConsole: true);
        foreach (var relative in new[] { ProfileRomSelection.WholeChipSourcePath })
            output.WriteLine($"{relative}: sha256={Hash(File.ReadAllBytes(Path.Combine(root, relative)))}");
        await WaitForDecodedFrame(ctx.Worker, ctx.Rom, text => text.Contains("HC2000", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(20));
        async Task Mount(DiskSlot slot, byte[] bytes)
        {
            var reply = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.InsertDisk, slot, ImmutableArray.CreateRange(bytes), "disposable.dsk")).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(reply.Error);
        }
        await Mount(DiskSlot.A, prepared.Save());
        await Tap(ctx, HcKey.P); await Tap(ctx, HcKey.Space);
        await Hold(ctx, HcKey.CapsShift, HcKey.SymbolShift);
        await Tap(ctx, HcKey.L); await Tap(ctx, HcKey.Space);
        foreach (var key in new[] { HcKey.D1, HcKey.D4, HcKey.D4, HcKey.D4, HcKey.D6, HcKey.Enter }) await Tap(ctx, key);
        string ConsoleText() => new(ctx.Trace.Console.ToArray().Select(value => (char)value).ToArray());
        async Task WaitConsole(int start, string expected)
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(75);
            while (DateTime.UtcNow < until && !ConsoleText()[start..].Contains(expected, StringComparison.Ordinal))
            {
                if (ctx.Worker.Completion.IsCompleted) await ctx.Worker.Completion;
                await Task.Delay(20);
            }
            var seen = ConsoleText()[start..];
            output.WriteLine($"expected={expected}; seen={seen}; PC={ctx.Cpu.R.PC:X4}");
            Assert.Contains(expected, seen);
            Assert.DoesNotContain("ERROR", seen, StringComparison.OrdinalIgnoreCase);
        }
        await WaitConsole(0, "A>");
        Assert.Contains("CP/M ver 2.2", ConsoleText());
        await WaitFrames(ctx.Worker, 40);
        async Task Send(string text, string expected = "A>")
        {
            var start = ConsoleText().Length;
            output.WriteLine($"command={text.TrimEnd()}; bound=75s; cadence=16 frames/edge");
            ctx.Queue.Enqueue(new PasteText(text, 16 * 355680L));
            await WaitConsole(start, expected);
            await WaitFrames(ctx.Worker, 40);
        }
        await Send("LOAD LDFIX\n");
        Assert.Contains("FIRST ADDRESS 0100", ConsoleText());
        Assert.Contains("LAST  ADDRESS 0107", ConsoleText());
        Assert.Contains("BYTES READ    0008", ConsoleText());
        await Send("DIR LDFIX.COM\n");
        Assert.Contains("LDFIX    COM", ConsoleText());
        var runStart = ConsoleText().Length;
        await Send("LDFIX\n");
        Assert.Contains("L\r\nA>", ConsoleText()[runStart..]);
        // Passive readback only: the guest CCP loaded this COM from its disk into the TPA.
        // No memory writes, BDOS interception, or host conversion.
        var guestPayload = Enumerable.Range(0, payload.Length).Select(i => ctx.Memory.Read((ushort)(0x100 + i))).ToArray();
        Export("guest-tpa.bin", guestPayload);
        Assert.Equal(payload, guestPayload);
        async Task<byte[]> ReadDisk(DiskSlot slot, string name)
        {
            var reply = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.ExportDisk, slot)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(reply.Error);
            var bytes = reply.Export.ToArray();
            Export(name, bytes);
            return bytes;
        }
        var a = Hc2000.Machine.Storage.CpmFs.Open(Hc2000.Machine.Storage.Dsk.Load(await ReadDisk(DiskSlot.A, "a-after.dsk")), "cpm-hc80");
        Assert.Equal(sourceFs.List().Count + 2, a.List().Count);
        var converted = a.Get("LDFIX.COM");
        Export("ldfix.com", converted);
        Assert.Equal(128, converted.Length); // CP/M stores a complete record; only 8 bytes are payload.
        Assert.Equal(payload, converted[..payload.Length]);
        output.WriteLine($"padding={Convert.ToHexString(converted[payload.Length..])}");
        Assert.Equal(source, a.Get("LOAD.COM"));
        Assert.Equal(originalDisk, await File.ReadAllBytesAsync(path));
        output.WriteLine("why=original guest LOAD reports 0100..0107/0008 and returns; DIR sees COM; guest execution prints L; export matches independent 8-byte oracle. 120 remaining record bytes are padding, not payload. Physical/desktop parity unverified.");
        File.WriteAllText(Path.Combine(evidence, "console.txt"), ConsoleText());
    }

    [Fact]
    public Task Test6_WholeChipGuestBaudI04() => RunBaud("CPM22-HC.DSK",
        "ff5a1c541c8aa80778a0f44d3ece398013084aafd9be1d3f32d2f68b774bb008",
        "ddf4c3c62bb94e8c6000db2535ab83773d8d618f187209b0dae87ad8c06da88a", "A>", "Printer port set for ", captureBaudOwner: true);

    [Fact]
    public Task Test7_WholeChipGuestBaudI12() => RunBaud("CPMZ80.DSK",
        "66b8e4dedc08ddd872607efaecd2c2a4bf4ec8cb3869e7f3e468f966ccf12b87",
        "d8e6e0b34dce8e1236e236e6e1103f6a5c6101b775d3cd22fc58d09fa1ec2aa8", "A0>", "Serial port->set for ");

    // In CP/M (C7), the guest's DF0C/DF0D variable maps to physical FF0C/FF0D.
    // Firmware calls toggle C5/C7, so a live logical read can see unrelated DF0C.
    // After the command reaches the prompt, read the stored word, not that transient view.
    private static ushort ReadBaudDivisor(HcMemory memory) =>
        BinaryPrimitives.ReadUInt16LittleEndian(memory.Ram.Span.Slice(0xff0c, 2));

    private async Task RunBaud(string image, string diskHash, string programHash, string prompt, string message, bool captureBaudOwner = false)
    {
        // hc2000/source-manuals §8: hc-cpm PDF p04 (printed 6): seven rates,
        // @n, warm-boot persistence and IPL reset. §11.1 / hc-if1 p15 gives
        // divisor=floor(3500000/(26*rate))-2. No emulator result defines it.
        // The originals both store the divisor at DF0C (COM offset F8);
        // owner EPROM BIOS reads DF0C at E977/E9B8. This is RAM, not a UART.
        var root = RomSelectionFixture.RepositoryRoot();
        var path = Path.Combine(root, "disks/cpm", image);
        var original = await File.ReadAllBytesAsync(path);
        string Hash(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();
        Assert.Equal(diskHash, Hash(original));
        var fs = Hc2000.Machine.Storage.CpmFs.Open(Hc2000.Machine.Storage.Dsk.Load(original), "cpm-hc80");
        var binary = fs.Get("BAUD.COM", 0);
        Assert.Equal(512, binary.Length);
        Assert.Equal(programHash, Hash(binary));
        Assert.Equal(new byte[] { 0x22, 0x0c, 0xdf }, binary[0xf8..0xfb]);
        var evidence = Path.Combine(Path.GetTempPath(), "hc2000-baud-" + image + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        File.WriteAllBytes(Path.Combine(evidence, "original.com"), binary);
        File.WriteAllBytes(Path.Combine(evidence, "before.dsk"), original);
        output.WriteLine($"image={image}:user0/BAUD.COM; disk={diskHash}; binary={programHash}; evidence={evidence}; DB=none");
        output.WriteLine($"whole-chip={Hash(File.ReadAllBytes(Path.Combine(root, ProfileRomSelection.WholeChipSourcePath)))}");

        async Task Boot(OracleContext ctx)
        {
            await WaitForDecodedFrame(ctx.Worker, ctx.Rom, t => t.Contains("HC2000", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(20));
            var reply = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.InsertDisk, DiskSlot.A,
                ImmutableArray.CreateRange(original), image + "-copy")).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(reply.Error);
            await Tap(ctx, HcKey.P); await Tap(ctx, HcKey.Space);
            await Hold(ctx, HcKey.CapsShift, HcKey.SymbolShift);
            await Tap(ctx, HcKey.L); await Tap(ctx, HcKey.Space);
            foreach (var k in new[] { HcKey.D1, HcKey.D4, HcKey.D4, HcKey.D4, HcKey.D6, HcKey.Enter }) await Tap(ctx, k);
            await WaitConsole(ctx, 0, prompt);
            await WaitFrames(ctx.Worker, 40);
        }
        string Text(OracleContext ctx) => new(ctx.Trace.Console.ToArray().Select(value => (char)value).ToArray());
        ushort Divisor(OracleContext ctx) => ReadBaudDivisor(ctx.Memory);
        async Task<string> WaitConsole(OracleContext ctx, int start, string expected)
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(75);
            while (DateTime.UtcNow < until && !Text(ctx)[start..].Contains(expected, StringComparison.Ordinal))
            {
                if (ctx.Worker.Completion.IsCompleted) await ctx.Worker.Completion;
                await Task.Delay(20);
            }
            var seen = Text(ctx)[start..];
            output.WriteLine($"expected={expected}; seen={seen}; PC={ctx.Cpu.R.PC:X4}; physical-FF0C={Divisor(ctx):X4}");
            Assert.Contains(expected, seen);
            Assert.DoesNotContain("ERROR", seen, StringComparison.OrdinalIgnoreCase);
            return seen;
        }
        using (var ctx = Build(root, captureConsole: true, captureBaudOwner: captureBaudOwner))
        {
            await Boot(ctx);
            Assert.Equal(12, Divisor(ctx)); // owner EPROM E07E template, default 9600.
            async Task<string> Send(string command)
            {
                var start = Text(ctx).Length;
                output.WriteLine($"command={command.TrimEnd()}; bound=75s; cadence=16 frames/edge");
                ctx.Queue.Enqueue(new PasteText(command, 16 * 355680L));
                var seen = await WaitConsole(ctx, start, prompt);
                await WaitFrames(ctx.Worker, 40);
                return seen;
            }
            foreach (var rate in new[] { 110, 150, 300, 600, 1200, 4800, 9600 })
            {
                var seen = await Send($"BAUD {rate}\n");
                Assert.Contains(message + rate + " baud", seen); // binary-specific wording, NOT physical oracle.
                var expected = 3500000 / (26 * rate) - 2;
                var observed = Divisor(ctx);
                output.WriteLine($"why=manual rate {rate}, independent divider expected={expected}, physical-FF0C={observed}");
                Assert.Equal(expected, observed);
            }
            var custom = await Send("BAUD @1234\n");
            Assert.Contains(message + "1234 baud", custom);
            var saved = Divisor(ctx);
            Assert.Equal(3500000 / (26 * 1234) - 2, saved);
            Assert.Contains("Invalid baud rate", await Send("BAUD 1234\n"));
            Assert.Equal(saved, Divisor(ctx));
            // Default-off: enabling Build's private handle requires a separate diagnostic scope.
            var baseline = RequestBaudOwner(ctx.BaudOwner, DiagnosticPurpose.CommandStart);
            Task<BaudOwnerReceipt?>? post = null;
            try
            {
                Assert.Contains("No baud rate present", await Send("BAUD\n"));
                post = RequestBaudOwner(ctx.BaudOwner, DiagnosticPurpose.PostLoop);
                Assert.Equal(saved, Divisor(ctx));
            }
            finally
            {
                // Never wait for a copy ahead of preservation, Ctrl-C or teardown.
                if (ctx.BaudOwner is { } owner)
                {
                    ReportBaudOwner(owner, DiagnosticPurpose.CommandStart, baseline);
                    ReportBaudOwner(owner, DiagnosticPurpose.PostLoop, post);
                }
            }
            // Use actual CP/M Ctrl-C, not BDOS/PC injection, for a warm boot.
            var zeroEntries = Volatile.Read(ref ctx.Trace.ZeroVectorEntries);
            await Send("\u0003");
            Assert.True(Volatile.Read(ref ctx.Trace.ZeroVectorEntries) > zeroEntries, "Ctrl-C must enter CP/M warm-boot vector 0000");
            Assert.Equal(saved, Divisor(ctx));
            output.WriteLine($"why=warm boot preserves manually predicted @1234 divider {saved}");
            var reply = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.ExportDisk, DiskSlot.A)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(reply.Error);
            var after = reply.Export.ToArray();
            File.WriteAllBytes(Path.Combine(evidence, "after.dsk"), after);
            // Media export sets its own 14-byte DSK creator field (22h..2Fh).
            // Everything else, including every sector and its metadata, must match.
            output.WriteLine($"export sha256={Hash(after)}; creator-before={Convert.ToHexString(original[0x22..0x30])}; creator-after={Convert.ToHexString(after[0x22..0x30])}");
            Assert.Equal(original.Length, after.Length);
            Assert.Equal(original[..0x22], after[..0x22]);
            Assert.Equal(original[0x30..], after[0x30..]);
            Assert.Equal(binary, Hc2000.Machine.Storage.CpmFs.Open(Hc2000.Machine.Storage.Dsk.Load(after), "cpm-hc80").Get("BAUD.COM", 0));
            File.WriteAllText(Path.Combine(evidence, "console.txt"), Text(ctx));
        }
        // New whole-chip machine = power-on IPL, not a software warm boot.
        using (var cold = Build(root, captureConsole: true))
        {
            await Boot(cold);
            Assert.Equal(12, Divisor(cold));
            output.WriteLine("why=power-on IPL restores owner BIOS 9600 divider 000C; prior @1234 not retained");
            File.WriteAllText(Path.Combine(evidence, "cold-console.txt"), Text(cold));
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        output.WriteLine("coverage=original unchanged variant, all seven manual rates, @1234, rejected unlisted rate, missing parameter, Ctrl-C warm boot, new-machine IPL, byte-identical source and canonical mounted sectors (creator header normalized). GAP=serial waveform/peer/printer throughput and exact-version real-HC transcript, other inputs, installed desktop/physical parity; no runtime or serial adapter changes.");
    }

    private static Task<BaudOwnerReceipt?>? RequestBaudOwner(
        OwnerCopySlots<BaudOwnerReceipt>? owner, DiagnosticPurpose purpose)
    {
        if (owner is null) return null;
        try { return owner.Request(purpose, DateTime.UtcNow + TimeSpan.FromSeconds(75)); }
        catch (InvalidOperationException) { return null; } // Diagnostic refusal cannot replace the gate.
    }

    private void ReportBaudOwner(OwnerCopySlots<BaudOwnerReceipt> owner,
        DiagnosticPurpose purpose, Task<BaudOwnerReceipt?>? task)
    {
        var label = purpose == DiagnosticPurpose.CommandStart ? "BEFORE_NO_ARGUMENT" : "AFTER_NO_ARGUMENT";
        var receipt = task is { IsCompletedSuccessfully: true } ? task.Result : null;
        output.WriteLine($"BAUD supplement {label}: state={owner.State(purpose)}; receipt={(receipt is null ? "UNAVAILABLE" : "OWNER_COPY")}; request is not service; service is not legacy failed-read tick or writer/cause proof");
        if (receipt is null) return;
        output.WriteLine($"BAUD request epoch={receipt.Request.Epoch}; id={receipt.Request.Id}; requested={receipt.Request.RequestedUtc:O}; deadline-metadata={receipt.Request.DeadlineUtc:O}; service master={receipt.MasterTick}; cpu={receipt.CpuTStates}; PC={receipt.Pc:X4}; latches={receipt.Latches}; DF0C={receipt.AtDf0c}:{receipt.LogicalDf0c:X2}; DF0D={receipt.AtDf0d}:{receipt.LogicalDf0d:X2}; physical DF0C/DF0D={receipt.RamDf0c:X2}/{receipt.RamDf0d:X2}; FF0C/FF0D={receipt.RamFf0c:X2}/{receipt.RamFf0d:X2}; supplemental interval only");
    }

    [WholeChipDdtFact]
    public async Task Test6_WholeChipOriginalDdtFillAndDisplay()
    {
        // Reuse fb1eae6's oracle: hc-cpm PDF pp4-5 (printed 7-8), section 9.
        // Fs,f,bc fills an inclusive interval; D{s,{f}} displays HEX/ASCII.
        // Only guest DDT writes scratch TPA RAM; this test observes, never injects.
        var root = RomSelectionFixture.RepositoryRoot();
        var path = Path.Combine(root, "disks/cpm/CPMZ80.DSK");
        var chipPath = Path.Combine(root, ProfileRomSelection.WholeChipSourcePath);
        var originalDisk = await File.ReadAllBytesAsync(path);
        var originalChip = await File.ReadAllBytesAsync(chipPath);
        string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var evidence = Path.Combine(Path.GetTempPath(), "hc2000-ddt-wholechip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        void Export(string name, byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(evidence, name), bytes);
            output.WriteLine($"{name}: bytes={bytes.Length} sha256={Hash(bytes)}");
        }
        var media = Dsk.Load(originalDisk);
        var source = CpmFs.Open(media).Get("DDT.COM", user: 0);
        Assert.Equal("66b8e4dedc08ddd872607efaecd2c2a4bf4ec8cb3869e7f3e468f966ccf12b87", Hash(originalDisk));
        Assert.Equal(4864, source.Length);
        Assert.Equal("bc4b0f085f8d1e37caf2a13850c70afa07acb626b22b1a7cb63368b3334dd448", Hash(source));
        output.WriteLine($"evidence={evidence}; DB=none; source geometry={media.UniformGeometry()}; disk={Hash(originalDisk)}; wholeEPROM={Hash(originalChip)}");
        Export("ddt-original.com", source);
        using var ctx = Build(root, captureConsole: true);
        await WaitForDecodedFrame(ctx.Worker, ctx.Rom, text => text.Contains("HC2000", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(20));
        var mount = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.InsertDisk, DiskSlot.A,
            ImmutableArray.CreateRange(originalDisk), "disposable-I12.dsk")).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(mount.Error);
        await Tap(ctx, HcKey.P); await Tap(ctx, HcKey.Space);
        await Hold(ctx, HcKey.CapsShift, HcKey.SymbolShift);
        await Tap(ctx, HcKey.L); await Tap(ctx, HcKey.Space);
        foreach (var key in new[] { HcKey.D1, HcKey.D4, HcKey.D4, HcKey.D4, HcKey.D6, HcKey.Enter }) await Tap(ctx, key);
        string ConsoleText() => new(ctx.Trace.Console.ToArray().Select(value => (char)value).ToArray());
        async Task WaitConsole(int start, string expected)
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(75);
            while (DateTime.UtcNow < until && !ConsoleText()[start..].Contains(expected, StringComparison.Ordinal))
            {
                if (ctx.Worker.Completion.IsCompleted) await ctx.Worker.Completion;
                await Task.Delay(20);
            }
            var seen = ConsoleText()[start..];
            File.WriteAllText(Path.Combine(evidence, "console.txt"), ConsoleText());
            output.WriteLine($"expected={expected}; seen={seen}; PC={ctx.Cpu.R.PC:X4}; bound=75s");
            Assert.Contains(expected, seen);
            Assert.DoesNotContain("ERROR", seen, StringComparison.OrdinalIgnoreCase);
        }
        await WaitConsole(0, "A0>"); // I12's actual CCP, not CPM22-HC's A>.
        await WaitFrames(ctx.Worker, 40);
        async Task Send(string text, string expected)
        {
            var start = ConsoleText().Length;
            output.WriteLine($"command={text.TrimEnd()}; cadence=16 frames/edge");
            ctx.Queue.Enqueue(new PasteText(text, 16 * 355680L));
            await WaitConsole(start, expected);
            await WaitFrames(ctx.Worker, 40);
        }
        await Send("DDT\n", "\n-");
        Assert.Contains("DDT VERS 2.2", ConsoleText()); // observation, not a real-HC version oracle
        byte before = ctx.Memory.Read(0x00FF), after = ctx.Memory.Read(0x0110);
        await Send("F0100,010F,5A\n", "\n-");
        var expectedBytes = Enumerable.Repeat((byte)0x5A, 16).ToArray();
        var readback = Enumerable.Range(0, 16).Select(i => ctx.Memory.Read((ushort)(0x0100 + i))).ToArray();
        Export("expected-memory.bin", expectedBytes);
        Export("guest-memory.bin", readback);
        Assert.Equal(expectedBytes, readback);
        Assert.Equal(before, ctx.Memory.Read(0x00FF));
        Assert.Equal(after, ctx.Memory.Read(0x0110));
        output.WriteLine($"adjacent unchanged: 00FF={before:X2}, 0110={after:X2}");
        await Send("D0100,010F\n", "0100 " + string.Join(" ", Enumerable.Repeat("5A", 16)));
        var exported = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.ExportDisk, DiskSlot.A)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(exported.Error);
        var mountedAfter = exported.Export.ToArray();
        Export("a-after.dsk", mountedAfter);
        var readbackMedia = Dsk.Load(mountedAfter);
        // MediaSession exports a regenerated DSK header/creator, not source container bytes.
        // Compare every sector payload independently of those presentation metadata bytes.
        Assert.Equal(media.UniformGeometry(), readbackMedia.UniformGeometry());
        var beforeSectors = media.ToRaw();
        var afterSectors = readbackMedia.ToRaw();
        Export("source-sectors.bin", beforeSectors);
        Export("mounted-sectors.bin", afterSectors);
        Assert.Equal(beforeSectors, afterSectors);
        Assert.Equal(source, CpmFs.Open(readbackMedia).Get("DDT.COM", user: 0));
        Assert.Equal(originalDisk, await File.ReadAllBytesAsync(path));
        Assert.Equal(originalChip, await File.ReadAllBytesAsync(chipPath));
        output.WriteLine("why=original DDT F writes exactly sixteen 5A bytes with adjacent sentinels unchanged; D prints the independently prescribed HEX row; mounted/source media, COM and whole EPROM preserved. Launch/banner are observations only; exact-version physical reference and installed desktop parity remain unverified.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task Tap(OracleContext ctx, HcKey key)
    {
        var src = "oracle:" + Interlocked.Increment(ref ctx.TapSeq);
        ctx.Queue.Enqueue(new KeyTransition(src, new KeyChord(key), true));
        await WaitFrames(ctx.Worker, 8);
        ctx.Queue.Enqueue(new KeyTransition(src, new KeyChord(key), false));
        await WaitFrames(ctx.Worker, 8);
    }

    private static async Task Hold(OracleContext ctx, HcKey a, HcKey b)
    {
        var sa = "oracle:" + Interlocked.Increment(ref ctx.TapSeq);
        var sb = "oracle:" + Interlocked.Increment(ref ctx.TapSeq);
        ctx.Queue.Enqueue(new KeyTransition(sa, new KeyChord(a), true));
        ctx.Queue.Enqueue(new KeyTransition(sb, new KeyChord(b), true));
        await WaitFrames(ctx.Worker, 8);
        ctx.Queue.Enqueue(new KeyTransition(sa, new KeyChord(a), false));
        ctx.Queue.Enqueue(new KeyTransition(sb, new KeyChord(b), false));
        await WaitFrames(ctx.Worker, 8);
    }

    private static async Task WaitFrames(EmulationWorker worker, int count)
    {
        var seen = 0;
        long tick = -1;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4 + count * 0.05);
        while (seen < count && DateTime.UtcNow < deadline)
        {
            if (worker.TakeLatestFrame() is { } frame && frame.MasterTick > tick)
            {
                tick = frame.MasterTick;
                seen++;
            }
            else await Task.Delay(1);
        }
        Assert.True(seen >= count, $"Expected {count} frames; saw {seen}.");
    }

    private static async Task<VideoFrame> WaitForDecodedFrame(EmulationWorker worker, byte[] rom, Func<string, bool> predicate, TimeSpan timeout)
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
            await Task.Delay(5);
        }
        throw new TimeoutException("Screen did not reach the expected text.\n" + last);
    }

    private static async Task WaitFramesKeeping(EmulationWorker worker, int count, Action<VideoFrame> keep)
    {
        var seen = 0;
        long tick = -1;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4 + count * 0.05);
        while (seen < count && DateTime.UtcNow < deadline)
        {
            if (worker.TakeLatestFrame() is { } frame && frame.MasterTick > tick)
            {
                tick = frame.MasterTick;
                seen++;
                keep(frame);
            }
            else await Task.Delay(1);
        }
        Assert.True(seen >= count, $"Expected {count} frames; saw {seen}.");
    }

    private static Dictionary<uint, int> PixelColorCounts(VideoFrame? frame)
    {
        var counts = new Dictionary<uint, int>();
        if (frame is null) return counts;
        foreach (var px in frame.Pixels.Span)
            counts[px] = counts.GetValueOrDefault(px) + 1;
        return counts;
    }

    // ── Screen decoding (same as GuiDiskAttachTests) ────────────────────────

    private static string DecodeBasicScreen(VideoFrame frame, byte[] rom)
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

    private static bool FrameShowsAPrompt(VideoFrame frame)
    {
        if (frame.Width != 352 || frame.Height != 296) return false;
        ReadOnlySpan<byte> glyphA = [0x00, 0x02, 0x05, 0x05, 0x07, 0x05, 0x05, 0x00];
        ReadOnlySpan<byte> glyphGt = [0x00, 0x00, 0x04, 0x02, 0x01, 0x02, 0x04, 0x00];
        var pixels = frame.Pixels.Span;
        for (var row = 0; row < 24; row++)
        {
            for (var col = 0; col < 63; col += 2)
            {
                if (GlyphMatches(pixels, frame.Width, col, row, glyphA)
                    && GlyphMatches(pixels, frame.Width, col + 1, row, glyphGt))
                    return true;
            }
        }
        return false;
    }

    private static bool GlyphMatches(ReadOnlySpan<uint> pixels, int width, int col, int row, ReadOnlySpan<byte> glyph)
    {
        const int originX = 48;
        const int originY = 40;
        var cellX = originX + (col / 2) * 8;
        var cellY = originY + row * 8;
        var counts = new Dictionary<uint, int>();
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
            {
                var color = pixels[(cellY + y) * width + cellX + x];
                counts[color] = counts.GetValueOrDefault(color) + 1;
            }
        var background = counts.MaxBy(pair => pair.Value).Key;
        for (var y = 0; y < 8; y++)
        {
            byte bits = 0;
            for (var x = 0; x < 4; x++)
                if (pixels[(cellY + y) * width + originX + col * 4 + x] != background)
                    bits |= (byte)(0x8 >> x);
            if (bits != glyph[y]) return false;
        }
        return true;
    }
}

public sealed class WholeChipDdtFactAttribute : FactAttribute
{
    public WholeChipDdtFactAttribute([System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null, [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        var root = RomSelectionFixture.RepositoryRoot();
        foreach (var (relative, size) in new[]
        {
            (ProfileRomSelection.WholeChipSourcePath, 65536L),
            ("disks/cpm/CPMZ80.DSK", 0L)
        })
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path) || (size > 0 && new FileInfo(path).Length != size)
                || (File.Exists(path) && new FileInfo(path).Length < 1024))
                Skip = "Canonical asset absent or not LFS-materialized: " + relative;
        }
    }
}
