using System.Collections.Immutable;
using Hc2000.App.Input;
using Hc2000.App.Audio;
using Hc2000.App.Media;
using Hc2000.App.Debugging;
using Hc2000.Machine;
using Hc2000.Machine.Expansion;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Snapshots;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Snapshots;
using Hc2000.App.Snapshots;
using Hc2000.Machine.Video;
using Hc2000.Z80;
using CpuZ80 = Hc2000.Z80.Z80;
using LiveMachine = Hc2000.Machine.Machine;

namespace Hc2000.App.Runtime;

/// <summary>Composition of a verified canonical BASIC ROM set with the existing real machine seams.</summary>
public static class BasicMachineComposition
{
    // Historical v1 fixtures only (friend test assembly); no App launch or recovery path calls this.
    internal static EmulationWorker CreateWorker(SelectedProfileRomSet selection, InputQueue inputQueue, MediaBridge? media = null)
    {
        return CreateWorker(Prepare(selection), inputQueue, media, slot: null);
    }

    /// <summary>Validates/copies before replacing a running session; no path is read after this step.</summary>
    internal static PreparedBasicMachine Prepare(SelectedProfileRomSet selection)
    {
        var roms = ProfileRomSelection.CopyVerifiedV1(selection);
        return new PreparedBasicMachine(roms);
    }

    /// <summary>Composes a faithful v2 BASIC machine directly from the whole-chip EPROM dump.</summary>
    public static PreparedBasicMachine PrepareWholeChipV2((MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms,
        HcsWavPolicy? snapshotPolicy = null)
    {
        // The whole chip IS the ROM map: main ROM 0x8000..0xFFFF (v2/basic.rom), CP/M half
        // 0xC000..0xFFFF, and the IF1 shadow ROM 0x4000..0x7FFF. No per-file slices, no padding,
        // no substitution -- the shadow page is the v2 IF1 image.
        roms.Profile.Validate();
        if (roms.Profile.Name == "hc2000-v2-paged-expansion-v1")
        {
            // Trusted composition opt-in, before a worker or GiB arena exists; never file-derived.
            snapshotPolicy ??= HcsWavPolicy.Default with { TransientBytes = 16L * 0x44000000 };
            snapshotPolicy.ValidateExpansionStep1Admission();
        }
        return new PreparedBasicMachine(roms, snapshotPolicy: snapshotPolicy);
    }

    /// <summary>Same verified whole-chip ROM map, with one exact existing RAM policy.
    /// No flags, combined devices, turbo or alternative firmware are inferred from a label.</summary>
    public static PreparedBasicMachine PrepareRamWholeChipV2(
        (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms, string profileId, bool textOverlay = false)
    {
        roms.Profile.Validate();
        if (roms.Profile.Name != MachineProfile.Hc2000V2.Name)
            throw new ArgumentException("Hardware preparation requires the verified faithful v2 ROM map.", nameof(roms));
        var profile = profileId switch
        {
            "hc2000-v2" => MachineProfile.Hc2000V2,
            "hc2000-v2-ram-banks-v1" => MachineProfile.Hc2000V2RamBanksV1,
            "hc2000-v2-paged-v1" => MachineProfile.Hc2000V2PagedV1,
            "hc2000-v2-paged-m-v1" => MachineProfile.Hc2000V2PagedMTransferV1,
            "hc2000-v2-paged-m-v2" => MachineProfile.Hc2000V2PagedMTransferV2,
            "hc2000-v2-paged-expansion-v1" => MachineProfile.Hc2000V2PagedExpansionV1,
            _ => throw new ArgumentException("Unsupported cold RAM profile.", nameof(profileId))
        };
        // Cold adoption: the derived record copy carries the adopted choice; the static profile is never mutated.
        var adopted = profile with { Video = profile.Video with { TextOverlay = textOverlay } };
        adopted.Validate();
        return PrepareWholeChipV2((adopted, roms.Basic, roms.Cpm, roms.If1));
    }

    /// <summary>Prepare host bytes BEFORE constructing the worker. No live machine is replaced.
    /// Installation occurs only in its owner-thread cold NEW factory, before its first instruction.</summary>
    public static PreparedBasicMachine PrepareHddWholeChipV2(
        (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms, IEnumerable<HddInitialMedia> images, bool textOverlay = false)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (roms.Profile.Name != MachineProfile.Hc2000V2.Name)
            throw new ArgumentException("HDD cold configuration requires the whole-chip faithful v2 ROM map.", nameof(roms));
        var owned = images.Select(image =>
        {
            if (image.Drive is < 2 or > 5 || image.Bytes.IsDefault || image.Bytes.Length != HddMediaAdapter.ImageLength)
                throw new ArgumentException("Invalid initial HDD slot or raw image length.", nameof(images));
            return image with { Bytes = ImmutableArray.CreateRange(image.Bytes.AsSpan().ToArray()) };
        }).ToImmutableArray();
        if (owned.Select(image => image.Drive).Distinct().Count() != owned.Length)
            throw new ArgumentException("Duplicate initial HDD drive.", nameof(images));
        // Cold adoption: the derived record copy carries the adopted choice; the static profile is never mutated.
        var adopted = MachineProfile.Hc2000V2HddV1 with { Video = MachineProfile.Hc2000V2HddV1.Video with { TextOverlay = textOverlay } };
        adopted.Validate();
        return new((adopted, roms.Basic, roms.Cpm, roms.If1), owned);
    }

    // Selected native volumes only; direct immutable raw/profile-off inputs keep their original path.
    internal static PreparedBasicMachine PrepareAdmittedHddWholeChipV2(
        (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms,
        HddInitialMedia image, HddColdAdmission admission)
        => PrepareAdmittedHddImagesWholeChipV2(roms, [image], admission);

    internal static PreparedBasicMachine PrepareAdmittedHddMapWholeChipV2(
        (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms,
        HddColdVolume volume)
        => PrepareAdmittedHddImagesWholeChipV2(roms, volume.MappedMedia, volume.Admission, guestAckEnabled: true);

    private static PreparedBasicMachine PrepareAdmittedHddImagesWholeChipV2(
        (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms,
        IEnumerable<HddInitialMedia> images, HddColdAdmission admission, bool guestAckEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(admission);
        var copied = PrepareHddWholeChipV2(roms, images);
        var selected = copied.Roms;
        // The resident guest installer queries the existing acknowledgement ABI even at cold boot.
        // This does not grant guest-safe live remapping or snapshot restore. Writable selection is a separate explicit opt-in.
        if (guestAckEnabled) selected.Profile = MachineProfile.Hc2000V2HddGuestV1;
        var prepared = new PreparedBasicMachine(selected, copied.InitialHdd, admission);
        admission.BindPrepared(prepared);
        return prepared;
    }

    public static EmulationWorker CreateWorker(PreparedBasicMachine prepared, InputQueue inputQueue, MediaBridge? media = null,
        HcsBridge? snapshots = null, bool audioEnabled = false, string? soundDump = null)
        => CreateWorker(prepared, inputQueue, media, slot: null, snapshots, audioEnabled, soundDump: soundDump);

    internal static EmulationWorker CreateWorker(PreparedBasicMachine prepared, InputQueue inputQueue, MediaBridge? media,
        HcsBridge? snapshots, bool audioEnabled, DiskWritebackOwnerLedger? writeback, string? soundDump = null)
        => CreateWorker(prepared, inputQueue, media, slot: null, snapshots, audioEnabled, writeback, soundDump);

    /// <summary>Same production worker. <paramref name="slot"/> publishes the composed FDC so a
    /// test can observe READ DATA without touching the owner-thread machine.</summary>
    internal static EmulationWorker CreateWorker(PreparedBasicMachine prepared, InputQueue inputQueue,
        MediaBridge? media, ComposedFloppySlot? slot, HcsBridge? snapshots = null, bool audioEnabled = false,
        DiskWritebackOwnerLedger? writeback = null, string? soundDump = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(inputQueue);
        slot ??= new ComposedFloppySlot();
        RuntimeContext? runtimeContext = null;
        MediaSession? session = null;
        FloppyDrive? driveA = null;
        FloppyDrive? driveB = null;
        TapeTransport? liveTape = null;
        TapeDeckState? retainedTape = null;
        BlockDiskPort? liveHdd = null;
        // The current machine's CPU/RAM for request-gated debugger boundary copies.
        CpuZ80? liveCpu = null; HcMemory? liveMemory = null;
        BlockDiskPortState? retainedHdd = null;
        HddMediaAdapter? hddAdapter = null;
        bool coldNew = true;
        BeeperAudioOutput? audio = null;
        string? audioReason = audioEnabled ? null : "Host audio disabled.";
        var earEdges = new List<BeeperEdge>();
        bool monitoring = true;
        (LiveMachine Machine, CpuZ80 Cpu, HcMemory Memory, FloppyController Controller, Ula Ula, Action ActivateMonitor)? replacement = null;
        var input = new InputController(inputQueue, (control, _) =>
        {
            if (writeback?.Frozen == true && control.Kind != InputControlKind.Shutdown) return;
            if (control.Kind == InputControlKind.Shutdown)
            {
                if (writeback?.Frozen == true && writeback.ShutdownPermitted != true) return;
                runtimeContext?.Shutdown();
                writeback?.OnShutdown();
            }
            if (control.Kind == InputControlKind.Pause) { runtimeContext?.Pause(); audio?.Pause(); }
            if (control.Kind == InputControlKind.Resume) runtimeContext?.Resume();
            if (control.Kind == InputControlKind.Step) runtimeContext?.Step();
            if (control.Kind == InputControlKind.SetSpeed) runtimeContext?.SetSpeed((SpeedMode)control.SpeedPercent);
            if (control.Kind == InputControlKind.Reset && runtimeContext is not null)
            {
                var capture = liveTape!.Capture();
                if (!capture.Success) throw new InvalidOperationException($"Tape reset transfer refused: {capture.Failure!.Detail}");
                retainedTape = capture.Value;
                // Spec 14: reset discards the session, never publishes its dirty image.
                runtimeContext.Machine.TapeVolume?.Cancel();
                hddAdapter?.RetireGuestSwitch();
                retainedHdd = liveHdd?.Capture(); // Same media, not a new-image initialization exception.
                runtimeContext.Reset();
            }
        }, media: media is null ? null : (command, _) =>
        {
            var floppy = slot.Controller ?? throw new InvalidOperationException(
                "Media command before the faithful FDC was composed.");
            session ??= new MediaSession(floppy.DriveA, floppy.DriveB, liveTape, manualDeck: true, hdd: hddAdapter,
                tapeVolume: () => runtimeContext?.Machine.TapeVolume); // The delegate resolves the CURRENT machine after every reset.
            var rawHddObservation = command.Action == MediaAction.Hdd
                && command.Hdd?.Action is HddAction.Inspect or HddAction.CaptureSave or HddAction.CancelSave;
            if (command.Action == MediaAction.Writeback || writeback?.Frozen == true && command.Action != MediaAction.Inspect && !rawHddObservation)
            {
                var viewed = session.Apply(new(MediaAction.Inspect));
                var ownerReply = command.Action == MediaAction.Writeback
                    ? writeback?.Apply(command.Writeback, session, runtimeContext, inputQueue)
                        ?? new WritebackOwnerReply(false, false, Error: "Writeback owner unavailable.")
                    : new WritebackOwnerReply(false, true, Error: "Writeback freeze: media change refused.");
                media.Publish(viewed with { RequestId = command.RequestId, Error = ownerReply.Error, Writeback = ownerReply });
                return;
            }
            if (command.Action == MediaAction.MonitorTape) { monitoring = command.Value; if (audio is not null) audio.TapeMonitoringEnabled = monitoring; }
            var reply = session.Apply(command);
            var view = liveTape!.Deck.Observe();
            earEdges.Add(new(runtimeContext!.Machine.MasterTick, view.EarLevel,
                view.Mode == TapeDeckMode.Playing && !view.Paused && view.HoldReason == TapeHoldReason.None));
            media.Publish(reply with { Tape = reply.Tape is null ? null : reply.Tape with
                { AudioAvailable = audio?.Available == true, AudioReason = audioReason ?? (audio?.Available == true ? null : "Host audio unavailable.") } });
        }, snapshot: snapshots is null ? null : (command, _) =>
        {
            if (writeback?.Frozen == true)
            {
                snapshots.Publish(new(command.RequestId, Error: "Writeback freeze: snapshot refused."));
                return;
            }
            if (prepared.Roms.Profile.BlockDisk is BlockDiskPolicy.HddV1 or BlockDiskPolicy.HddGuestV1)
            {
                snapshots.Publish(new(command.RequestId, Error: "HDD logical media is not yet composed into HCS; Save/Load unavailable on this profile."));
                return;
            }
            if (command.Action != HcsAction.Load)
            {
                // Deliberately AFTER the retained HDD profile-shape refusal above. This
                // additional gate is ready for a later format slice; it enables no HCS lane.
                if (command.Action == HcsAction.Save)
                {
                    try
                    {
                        hddAdapter?.ValidateSessionSnapshotSave();
                        runtimeContext!.Machine.ValidateSessionSnapshotSave();
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or HcsException)
                    {
                        snapshots.Publish(new(command.RequestId, Error: ex.Message));
                        return;
                    }
                }
                snapshots.Publish(new HcsSession(runtimeContext!.Machine).Apply(command));
                return;
            }
            var view = liveTape!.Deck.Observe();
            if (!command.DiscardChanges && (view.A.Dirty || view.B.Dirty || driveA!.DirtySectors.Count != 0 || driveB!.DirtySectors.Count != 0))
            {
                snapshots.Publish(new(command.RequestId, Error: "DirtyConfirmationRequired: save edits or explicitly allow discard before snapshot Load."));
                return;
            }
            // Validate on a detached real composition. Never load into the running machine,
            // never publish its FDC or audio, and never step a candidate before adoption.
            var candidateSlot = new ComposedFloppySlot();
            Ula? candidateUla = null;
            bool adopted = false;
            var candidate = CreateMachine(prepared.Roms, candidateSlot, new(new()), new(new()),
                earEdges.Add, out var candidateCpu, out var candidateMemory,
                observeUla: value => candidateUla = value, canMonitor: () => adopted, snapshotPolicy: prepared.SnapshotPolicy);
            var loaded = new HcsSession(candidate).Apply(command);
            if (loaded.Applied)
            {
                replacement = (candidate, candidateCpu, candidateMemory, candidateSlot.Controller!, candidateUla!, () => adopted = true);
                runtimeContext!.Reset();
            }
            snapshots.Publish(loaded);
        });
        prepared.ColdAdmission?.MarkEnqueued(prepared);
        HcsLimits? admission = null;
        try
        {
        admission = inputQueue.BindSnapshotRuntime(prepared);
        snapshots?.BindAdmission(inputQueue, admission);
        var worker = new EmulationWorker(
            () =>
            {
                try
                {
                prepared.ColdAdmission?.EnterOwner();
                if (replacement is { } loaded)
                {
                    replacement = null; retainedTape = null;
                    liveCpu = loaded.Cpu; liveMemory = loaded.Memory;
                    driveA = loaded.Controller.DriveA; driveB = loaded.Controller.DriveB;
                    slot.Publish(loaded.Controller); liveTape = loaded.Machine.Tape!;
                    session = new(driveA, driveB, liveTape, manualDeck: true);
                    session.RestoredMetadata(liveTape.Capture().Value.Cassette);
                    earEdges.Clear(); loaded.ActivateMonitor();
                    audio!.Reset(loaded.Machine.MasterTick, loaded.Ula.Beeper.Speaker, loaded.Ula.Beeper.Microphone);
                    var view = liveTape.Deck.Observe();
                    earEdges.Add(new(loaded.Machine.MasterTick, view.EarLevel, view.Mode == TapeDeckMode.Playing && !view.Paused && view.HoldReason == TapeHoldReason.None));
                    return loaded.Machine;
                }
                // All calls run on the same owner. Keep media (including dirty overlays and
                // MediaSession names), not the CPU, RAM, scheduler or powered drive mechanics.
                driveA ??= new FloppyDrive(new());
                driveB ??= new FloppyDrive(new());
                driveA.PowerCycle();
                driveB.PowerCycle();
                earEdges.Clear();
                audio ??= BeeperAudioOutput.Open(FrameTiming.MasterHz, audioEnabled, reason => audioReason = reason, soundDump);
                audio.Reset(); audio.TapeMonitoringEnabled = monitoring;
                LiveMachine machine;
                if (coldNew)
                {
                    coldNew = false;
                    machine = CreateColdNewMachine(prepared, slot, driveA, driveB, earEdges.Add, value => liveHdd = value,
                        (c, m) => { liveCpu = c; liveMemory = m; });
                    if (liveHdd is not null) hddAdapter = new(liveHdd, prepared.InitialHdd);
                }
                else
                {
                    machine = CreateMachine(prepared.Roms, slot, driveA, driveB, earEdges.Add,
                        out var freshCpu, out var freshMemory, observeHdd: value => liveHdd = value,
                        snapshotPolicy: prepared.SnapshotPolicy);
                    liveCpu = freshCpu; liveMemory = freshMemory;
                    if (retainedHdd is not null)
                    {
                        liveHdd!.Restore(retainedHdd);
                        liveHdd.Reset();
                        retainedHdd = null;
                    }
                    if (liveHdd is not null) hddAdapter?.Rebind(liveHdd);
                }
                liveTape = machine.Tape!;
                if (retainedTape is not null)
                {
                    var restored = liveTape.Restore(retainedTape);
                    if (!restored.Success) throw new InvalidOperationException($"Tape reset adoption refused: {restored.Failure!.Detail}");
                    liveTape.OnMicChanged(machine.MasterTick, false);
                    retainedTape = null;
                }
                session?.RebindTape(liveTape);
                return machine;
                }
                catch
                {
                    // A failed owner factory cannot run again on this worker.
                    prepared.ColdAdmission?.RejectAfterOwnerDrain();
                    throw;
                }
            },
            context =>
            {
                runtimeContext = context;
                // RuntimeContext now owns the NEW machine and the media adapter is installed;
                // this boundary precedes its first instruction, even when immediately paused.
                prepared.ColdAdmission?.AcknowledgeInstalled(prepared, context.Machine, hddAdapter);
                input.ApplyPending(context.Machine.MasterTick, context.Machine.Keyboard);
                if (!context.Resetting && !context.Stopping) hddAdapter?.PumpGuestSwitch();
            },
            audio: (frame, speed, cancellation) =>
            {
                var paced = audio!.ProcessFrame(frame.StartMasterTick, frame.EndMasterTick,
                    frame.BeeperEdges.ToArray(), speed, cancellation, earEdges.ToArray());
                earEdges.Clear();
                return paced;
            },
            cleanup: context =>
            {
                try
                {
                    hddAdapter?.RetireGuestSwitch();
                    input.ReleaseOwned(context.Machine.Keyboard);
                    if (!context.Resetting) audio?.Dispose();
                }
                finally
                {
                    if (!context.Resetting) prepared.ColdAdmission?.RejectAfterOwnerDrain();
                }
            },
            executionAllowed: () => liveHdd?.GuestSwitchHoldsCpu != true,
            debugSnapshot: _ =>
            {
                // Request-gated boundary copy; runs on the worker's owner thread only.
                if (liveCpu is not { } cpu || liveMemory is not { } memory) return null;
                var state = new Z80SnapshotSeam(cpu).Capture();
                return new DebuggerSession($"HC2000 {prepared.Roms.Profile.Name} (boundary copy)")
                    .Boundary(DebuggerSession.RegisterView(state), memory, slot.Controller?.Fdc, state.TStates);
            });
        inputQueue.TrackSnapshotRuntime(admission, worker.Completion);
        _ = worker.Completion.ContinueWith(_ =>
        {
            snapshots?.Disconnect();
            inputQueue.RetireSnapshotRuntime(admission);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return worker;
        }
        catch
        {
            if (admission is not null) inputQueue.RetireSnapshotRuntime(admission);
            // Constructor failure occurs before any worker was started.
            prepared.ColdAdmission?.RejectBeforeOwnerStarted();
            throw;
        }
    }

    // Never called by Reset or HCS. No CPU instruction has run when this factory returns.
    internal static LiveMachine CreateColdNewMachine(PreparedBasicMachine prepared, ComposedFloppySlot slot,
        FloppyDrive driveA, FloppyDrive driveB, Action<BeeperEdge> monitor, Action<BlockDiskPort> observeHdd,
        Action<CpuZ80, HcMemory>? observeCpu = null)
    {
        prepared.ColdAdmission?.VerifyFreshOwnerCandidate(prepared);
        BlockDiskPort? controller = null;
        var machine = CreateMachine(prepared.Roms, slot, driveA, driveB, monitor,
            out var cpu, out var memory, observeHdd: value => { controller = value; observeHdd(value); },
            snapshotPolicy: prepared.SnapshotPolicy);
        observeCpu?.Invoke(cpu, memory);
        prepared.ColdAdmission?.ValidateBeforeAdoption(prepared, machine);
        foreach (var image in prepared.InitialHdd)
            controller!.AttachHdd(image.Drive, image.Bytes.AsSpan(), image.WriteProtected);
        return machine;
    }

    private static LiveMachine CreateMachine((MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms,
        ComposedFloppySlot slot, FloppyDrive driveA, FloppyDrive driveB, Action<BeeperEdge> monitor,
        out CpuZ80 cpu, out HcMemory memory,
        Action<Ula>? observeUla = null, Func<bool>? canMonitor = null, Action<BlockDiskPort>? observeHdd = null,
        HcsWavPolicy? snapshotPolicy = null)
    {
        var profile = roms.Profile;
        var scheduler = new DeviceScheduler();
        var clock = new FrameClock(scheduler, 1);
        var mapper = CpuTimeMapper.FromProfile(profile);
        var adapter = new CpuDeviceTimeAdapter(scheduler, mapper);
        memory = new HcMemory(profile.Memory with { RomProvenance = $"canonical verified {profile.Name} ROM set" },
            roms.Basic, roms.Cpm, roms.If1);
        var keyboard = new KeyboardMatrix();
        var ula = new Ula(memory, keyboard, scheduler, clock, 2, profile.Video);
        observeUla?.Invoke(ula);
        // Empty at first boot; mounted media survives Reset. Instant remains false.
        var floppy = new FloppyController(scheduler, 100, driveA, driveB);
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
            write: (_, value, _) => floppy.Fdc.WriteData(value));
        var idle = new PortDevice();
        var mappings = profile.Memory.RamBanks is not null
            ? HcIoProfile.WithRamBanks(memory, ula, driveLatch, fdcStatus, fdcData, idle, idle)
            : profile.Memory.RamPaged is not null
                ? HcIoProfile.WithPagedRam(memory, ula, driveLatch, fdcStatus, fdcData, idle, idle)
                : HcIoProfile.Faithful(memory, ula, driveLatch, fdcStatus, fdcData, idle, idle);
        BlockDiskPort? hdd = null;
        if (profile.BlockDisk is BlockDiskPolicy.HddV1 or BlockDiskPolicy.HddGuestV1)
        {
            hdd = new BlockDiskPort(memory, floppy, hddEnabled: true,
                guestAckEnabled: profile.BlockDisk == BlockDiskPolicy.HddGuestV1);
            mappings = HcIoProfile.WithBlockDisk(profile, memory, ula, driveLatch, fdcStatus, fdcData, idle, idle, hdd);
            observeHdd?.Invoke(hdd);
        }
        // Spec 14 deck control on its own profile; a reset builds a new port, so its selector starts at 0.
        var tapeControl = profile.TapeControl ? new TapeControlPort() : null;
        if (tapeControl is not null)
            mappings = HcIoProfile.WithTapeControl(profile, memory, ula, driveLatch, fdcStatus, fdcData, idle, idle, tapeControl);
        ExpansionController? expansionRoot = null;
        if (profile.Name == "hc2000-v2-paged-expansion-v1")
        {
            expansionRoot = new(memory.EnableExpansionArena(profile));
            mappings = ExpansionIoProfile.Compose(profile, memory,
                HcIoProfile.WithPagedRam(memory, ula, driveLatch, fdcStatus, fdcData, idle, idle), expansionRoot);
        }
        var ports = expansionRoot is null ? new PortDecoder(mappings) : new PortDecoder(mappings, expansionRoot, expansionRoot.MainOrigin);
        var machineBus = new HcMachineBus(memory, ports, adapter, scheduler);
        var timedBus = new CpuTimedBus(machineBus, adapter);
        cpu = new CpuZ80(timedBus);
        // The out parameter cannot be captured in the lambdas below; use the same instance via a local.
        var newCpu = cpu;
        timedBus.Attach(cpu);
        clock.InterruptChanged += (_, asserted) => newCpu.IntLine = asserted;
        var frontier = expansionRoot is null ? null : new ExpansionFrontierDriver(scheduler, clock, mapper, expansionRoot, memory, ports, cpu);

        // Authentic power-on: reset PC/latches, no RAM program, screen writes or ROM routine jump.
        var machine = new LiveMachine(scheduler, clock, adapter, ula,
            () => { hdd?.NotifyGuestInstruction(); newCpu.Step(); }, () => newCpu.TStates, frontier);
        var tape = machine.EnableTape(200);
        tapeControl?.Attach(tape);
        if (profile.Name == "hc2000-v2-paged-tape-v2") machine.EnableTapeVolume(profile, memory);
        // EnableTape owns the one real ULA MIC subscription; App adds only EAR observation.
        ula.TapeInputChanged += (tick, level) =>
        {
            if (canMonitor?.Invoke() == false) return;
            var view = tape.Deck.Observe();
            monitor(new(tick, level, view.Mode == TapeDeckMode.Playing && !view.Paused && view.HoldReason == TapeHoldReason.None));
        };
        machine.AttachSnapshotState(profile, clock, memory, mapper, new Z80SnapshotSeam(cpu), 2, floppy, snapshotPolicy);
        return machine;
    }
}

/// <summary>Detached and re-verified machine image; only BasicMachineComposition can construct it.</summary>
public sealed class PreparedBasicMachine
{
    internal HddColdAdmission? ColdAdmission { get; }
    internal (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) Roms { get; }
    internal ImmutableArray<HddInitialMedia> InitialHdd { get; }
    internal HcsWavPolicy SnapshotPolicy { get; }
    internal PreparedBasicMachine((MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms,
        ImmutableArray<HddInitialMedia> initialHdd = default, HddColdAdmission? coldAdmission = null, HcsWavPolicy? snapshotPolicy = null)
    {
        ColdAdmission = coldAdmission;
        Roms = roms;
        InitialHdd = initialHdd.IsDefault ? [] : initialHdd;
        SnapshotPolicy = snapshotPolicy ?? HcsWavPolicy.Default;
    }
}

/// <summary>Cross-thread publication of the FDC composed on the emulation owner.
/// ReadData counts port 0x87 reads, not status polls.</summary>
internal sealed class ComposedFloppySlot
{
    private FloppyController? controller;
    private int readData;
    public FloppyController? Controller => Volatile.Read(ref controller);
    public int ReadData => Volatile.Read(ref readData);
    internal void Publish(FloppyController value) => Volatile.Write(ref controller, value);
    internal void NoteReadData() => Interlocked.Increment(ref readData);
}
