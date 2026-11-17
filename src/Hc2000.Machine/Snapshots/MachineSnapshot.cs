using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hc2000.Machine.Expansion;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Snapshots;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;

namespace Hc2000.Machine.Snapshots;

/// <summary>Logical .hcs machine snapshot. <see cref="Save"/> captures
/// the live machine at a drained instruction boundary into a versioned .hcs document;
/// <see cref="Load"/> validates the document against this machine's profile and composition and
/// restores every component onto a fresh machine. All cross-state consistency (device rosters,
/// rom binding, event/state pairing) is checked here against the live machine; the base
/// <see cref="HcsSchema"/> owns container, field and clock-identity checks.</summary>
public sealed class MachineSnapshot(
    MachineProfile profile,
    DeviceScheduler scheduler,
    FrameClock clock,
    CpuTimeMapper mapper,
    HcMemory memory,
    Ula ula,
    KeyboardMatrix keyboard,
    ICpuSnapshotSeam cpu,
    int flashDeviceId,
    TapeTransport? tape,
    FloppyController? floppy,
    TapeVolumeSession? tapeVolume = null,
    ExpansionFrontierDriver? expansion = null, BlockDiskPort? hdd = null)
{
    private const string DevicePrefix = "device-";

    /// <summary>A runtime speed switch replaces the shared mapper; the snapshot must persist the one in force.</summary>
    internal void InstallMapper(CpuTimeMapper next)
    {
        ArgumentNullException.ThrowIfNull(next);
        mapper = next;
    }
    private const string KindPrefix = "kind-";
    private const int FdcDriveUnitOffset = 2; // fdc base id + 1 motor, + 2..+5 drive units
    private readonly int owner = Environment.CurrentManagedThreadId;
    private int externalResources;

    /// <summary>Host composition holds a lease for each live, non-rewindable resource.
    /// Leases and their release belong to the machine owner and never enter the snapshot.</summary>
    public IDisposable AcquireExternalState()
    {
        VerifyOwner();
        externalResources = checked(externalResources + 1);
        return new ExternalStateLease(this);
    }

    /// <summary>Admission before replacing this machine with a detached snapshot candidate.</summary>
    public void ValidateSessionSnapshotLoad() => CheckExternalState();

    private void VerifyOwner()
    {
        if (owner != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Snapshot external state belongs to the machine owner.");
    }

    private void CheckExternalState()
    {
        VerifyOwner();
        if (externalResources != 0) Fail(HcsError.ExternalStateUnsupported, "live host resource");
    }

    private sealed class ExternalStateLease(MachineSnapshot snapshot) : IDisposable
    {
        private bool released;
        public void Dispose()
        {
            snapshot.VerifyOwner();
            if (released) return;
            released = true;
            snapshot.externalResources--;
        }
    }

    /// <summary>Captures the logical machine state as a .hcs document. The machine must be at a
    /// drained instruction boundary and the FDC must be idle; anything else fails with
    /// <see cref="HcsError.StateUnsupported"/>.</summary>
    public byte[] Save(HcsWavPolicy? wavPolicy = null, HcsLimits? limits = null)
    {
        CheckExternalState();
        return SaveCore(wavPolicy, limits ?? DefaultLimits(), null, false);
    }

    /// <summary>Streaming HCS1 output avoids a second GiB-sized container allocation.</summary>
    public void Save(Stream destination, HcsWavPolicy? wavPolicy = null, HcsLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        CheckExternalState();
        _ = SaveCore(wavPolicy, limits ?? DefaultLimits(), null, false, destination);
    }

    private HcsLimits DefaultLimits() => HcsLimits.ForProfile(profile);

    /// <summary>Read-only session-save admission, before any RAM or container copy.</summary>
    public void ValidateSessionSnapshotSave(HcsWavPolicy? wavPolicy = null)
    {
        CheckExternalState();
        _ = CaptureAdmission(wavPolicy, DefaultLimits());
    }

    private (CpuLogicalState Cpu, JsonElement? Expansion, JsonElement? X5, JsonElement? X2, JsonElement? X3, JsonElement? X4) CaptureAdmission(HcsWavPolicy? wavPolicy, HcsLimits limits)
    {
        if (MachineSchema.HasHdd(profile) != (hdd is not null)) Fail(HcsError.StateUnsupported, "HDD composition");
        hdd?.ValidateLogicalSnapshot();
        wavPolicy?.Reserve(limits.TotalBytes);
        var stores = (long)memory.Ram.Length + memory.Rom.Length + memory.ShadowRam.Length + memory.ShadowRom.Length;
        if (stores > limits.TotalBytes) Fail(HcsError.LimitExceeded, "minimum machine stores");
        var frontier = ExpansionSchema.Enabled(profile) || ExpansionSchema.X5(profile);
        if (frontier != (expansion is not null)) Fail(HcsError.StateUnsupported, "expansion composition");
        if (expansion is not null && expansion.UnitCount != ExpansionArena.ComputeCount + profile.ExpansionDriverCount)
            Fail(HcsError.StateUnsupported, "expansion-x5 composition");
        if (ExpansionSchema.X5(profile) && !expansion!.ManagedMapComposed)
            Fail(HcsError.StateUnsupported, "managed map composition");
        if (profile.CpNet && (!memory.HasCpNetStagingClaim() || expansion?.X0?.CpNet is null))
            Fail(HcsError.StateUnsupported, "cpnet staging composition");
        var logical = cpu.Capture();
        // The save boundary is the committed CPU's exact time, not its ceiling: a fractional rate
        // lands mid-tick, and the ceiling would refuse a drained boundary. The non-expansion machine
        // drains exactly to it; the expansion machine may have advanced devices past it.
        if (expansion is null ? scheduler.LogicalNow != mapper.ExactTime() : scheduler.LogicalNow < mapper.ExactTime())
            Fail(HcsError.StateUnsupported, "boundary");
        if (mapper.CpuTStates != logical.TStates) Fail(HcsError.StateUnsupported, "cpu");
        if (expansion is null) return (logical, null, null, null, null, null);
        var x2 = CaptureX2();
        var x3 = CaptureX3();
        var x4 = CaptureX4();
        if (ExpansionSchema.X5(profile))
        {
            // The full-X5 file is admitted atomically before Save writes anything: the step-1
            // component with the X5 context plus the cross-component ownership and driver timing.
            var (compute, x5) = expansion.CaptureX5FullState();
            var encoded = ExpansionSchema.ValidateCaptured(compute, logical, scheduler, mapper, memory.Latches.PagedBank, x5);
            return (logical, encoded, ExpansionX5Schema.Encode(x5), x2, x3, x4);
        }
        var state = expansion.CaptureState();
        var encodedState = ExpansionSchema.ValidateCaptured(state, logical, scheduler, mapper, memory.Latches.PagedBank);
        return (logical, encodedState, null, x2, x3, x4);
    }

    private JsonElement? CaptureX2()
    {
        if (expansion?.X2 is not { } card) return null;
        if (!ExpansionSchema.X5(profile) || expansion.X0 is null)
            throw new HcsException(HcsError.StateUnsupported, "expansion-x2 composition");
        try
        {
            var state = card.CaptureMetadata();
            card.ValidateMetadata(state, CapturePlatform(expansion.X0), scheduler.Now, scheduler.PendingEvents);
            return ExpansionX2Schema.ValidateCaptured(state);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new HcsException(HcsError.StateUnsupported, "expansion-x2: " + error.Message);
        }
    }

    private ExpansionX0.ExpansionX0State CapturePlatform(ExpansionX0 platform)
    {
        var card = expansion?.X3;
        var state = platform.CaptureState(id => card is not null && id == card.CardId);
        if (card is not null)
        {
            if (!ExpansionSchema.X5(profile)) throw new ArgumentException("X3 requires full X5.");
            var (_, x5) = expansion!.CaptureX5FullState();
            card.ValidateMetadata(card.CaptureMetadata(), state, scheduler.Now, scheduler.PendingEvents,
                page => memory.TryDmaPageEpoch(page, out _), owner => card.SavedOwnerAllowed(owner, x5));
        }
        return state;
    }

    private JsonElement? CaptureX3()
    {
        if (expansion?.X3 is not { } card) return null;
        if (!ExpansionSchema.X5(profile) || expansion.X0 is null)
            throw new HcsException(HcsError.StateUnsupported, "expansion-x3 composition");
        try
        {
            _ = CapturePlatform(expansion.X0);
            return ExpansionX3Schema.ValidateCaptured(card.CaptureMetadata());
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new HcsException(HcsError.StateUnsupported, "expansion-x3: " + error.Message);
        }
    }

    private JsonElement? CaptureX4()
    {
        if (expansion?.X4 is not { } card) return null;
        if (!ExpansionSchema.X5(profile) || expansion.X0 is null)
            throw new HcsException(HcsError.StateUnsupported, "expansion-x4 composition");
        try
        {
            var state = card.CaptureMetadata();
            var (_, x5) = expansion.CaptureX5FullState();
            card.ValidateMetadata(state, CapturePlatform(expansion.X0), scheduler.Now, scheduler.PendingEvents,
                owner => card.SavedOwnerAllowed(owner, x5));
            return ExpansionX4Schema.ValidateCaptured(state);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new HcsException(HcsError.StateUnsupported, "expansion-x4: " + error.Message);
        }
    }

    private JsonElement? CaptureX0(ExpansionX0 x0)
    {
        try
        {
            return ExpansionX0Schema.ValidateCaptured(CapturePlatform(x0), scheduler.Now);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        { Fail(HcsError.StateUnsupported, "expansion-x0: " + e.Message); }
        return null;
    }

    private byte[] SaveCore(HcsWavPolicy? wavPolicy, HcsLimits limits, TapeCassetteImage? replacement, bool validateOnly, Stream? destination = null)
    {
        // Reserve the complete bounded copy plan BEFORE Capture/asset ToArray/MemoryStream allocation.
        var (cpuState, expansionState, x5State, x2State, x3State, x4State) = CaptureAdmission(wavPolicy, limits);
        // A machine composed without the X0 services saves the v1 form (absent component);
        // a façade on a non-expansion profile is a composition error.
        if (expansion?.X0 is not null && !ExpansionX0Schema.Enabled(profile))
            Fail(HcsError.StateUnsupported, "expansion-x0 composition");
        var x0Element = expansion?.X0 is { } x0Platform ? CaptureX0(x0Platform) : null;
        var fdcSnap = default(FdcDiagnosticSnapshot);
        byte sp1 = 0, sp2 = 0;
        int[] units = [];
        if (floppy is not null && !floppy.Fdc.TryIdleSnapshot(out fdcSnap, out sp1, out sp2, out units))
            Fail(HcsError.StateUnsupported, "fdc");

        TapeDeckState? tapeState = null;
        if (tape is not null && replacement is null)
        {
            var captured = tape.Capture();
            if (!captured.Success) Fail(HcsError.StateUnsupported, "tape capture");
            tapeState = captured.Value;
        }

        if (replacement is not null)
        {
            if (tape is null) Fail(HcsError.StateUnsupported, "cassette without tape composition");
            // Pure resource preview: do not Capture/flush/anchor the prior live cassette on a refusal.
            var zero = TapeNativeCodec.Rational(default(TapeTime));
            var component = new TapeComponent2Dto(
                CassetteAssetId: TapeSnapshotContract.CassetteAssetId, ActiveSide: 0, Position: zero,
                Mode: "stopped", Paused: false, HoldReason: "none", ConsumedStop: null,
                CounterZeroTurns: ["0", "0"], Tabs: [true, true], Dirty: [false, false],
                EditRevision: ["0", "0"], SavedRevision: ["0", "0"], FastWind: false,
                MediaIdentity: ulong.MaxValue.ToString(CultureInfo.InvariantCulture),
                AnchorTick: scheduler.Now.ToString(CultureInfo.InvariantCulture), AnchorPosition: zero,
                WaveformLevel: false, MicLevel: ula.Beeper.Microphone,
                RecordStart: null, RecordHead: null, RecordLevel: ula.Beeper.Microphone,
                NextPosition: null, IdealDue: null, PendingKind: null, PendingToken: "0");
            tapeState = new(component, replacement);
        }

        var blobs = new List<byte[]>();
        var assets = new JsonArray();
        void Asset(string id, string kind, string format, string provenance, byte[] data)
        {
            blobs.Add(data);
            assets.Add(new JsonObject
            {
                ["id"] = id,
                ["kind"] = kind,
                ["format"] = format,
                ["blobId"] = blobs.Count,
                ["byteLength"] = data.Length.ToString(CultureInfo.InvariantCulture),
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(data)),
                ["rawGeometry"] = null,
                ["provenance"] = provenance
            });
        }

        var driveA = floppy?.DriveA;
        var driveB = floppy?.DriveB;
        if (driveA?.BaseMedia is { } aBase)
            Asset("disk-a-base", "disk-base", "raw1", "unmodified drive-a media at the last mount", aBase);
        Asset("disk-a-current", "disk-current", "raw1", "drive-a current media export",
            driveA?.Image is null ? [] : driveA.ExportMedia());
        if (driveB?.BaseMedia is { } bBase)
            Asset("disk-b-base", "disk-base", "raw1", "unmodified drive-b media at the last mount", bBase);
        Asset("disk-b-current", "disk-current", "raw1", "drive-b current media export",
            driveB?.Image is null ? [] : driveB.ExportMedia());
        var hddState = hdd?.Capture();
        if (hddState is not null)
            for (var i = 0; i < 4; i++)
                Asset("hdd-" + (char)('c' + i), "buffer", "raw1", "logical HDD slot; no host binding", hddState.Slots[i].Bytes);
        // The byte[] route writes synchronously into a private exact-sized output
        // buffer, with no caller-controlled stream callbacks. Borrow paged RAM only
        // for that non-reentrant operation instead of retaining another GiB copy.
        // External streams still receive detached capture data before any Write.
        ArraySegment<byte> ramSegment = default;
        var borrowRam = destination is null && profile.Memory.RamPaged is not null
            && System.Runtime.InteropServices.MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)memory.Ram, out ramSegment)
            && ramSegment.Offset == 0 && ramSegment.Count == ramSegment.Array!.Length;
        var capturedRam = borrowRam ? ramSegment.Array! : memory.Ram.Span.ToArray();
        Asset("ram", "ram", "raw1", "main ram", capturedRam);
        Asset("rom", "rom", "raw1", "main rom", memory.Rom.ToArray());
        Asset("shadow-ram", "ram", "raw1", "shadow ram", memory.ShadowRam.Span.ToArray());
        Asset("shadow-rom", "rom", "raw1", "shadow rom", memory.ShadowRom.ToArray());
        if (tapeState?.Cassette is { } cassette)
        {
            var native = TapeNativeCodec.Write(cassette, new(), wavPolicy?.TapeLimits);
            if (!native.Success) Fail(HcsError.StateUnsupported, "tape native capture");
            Asset(TapeSnapshotContract.CassetteAssetId, TapeSnapshotContract.CassetteAssetKind,
                TapeSnapshotContract.CassetteAssetFormat, "exact two-sided cassette", native.Value);
            foreach (var source in cassette.SourceAssets)
                Asset(source.Id, "buffer", TapeNativeCodec.FormatName(source.Format) + "1", "original tape input", source.Bytes.ToArray());
        }

        var stores = new JsonArray
        {
            new JsonObject { ["id"] = "ram", ["assetId"] = "ram" },
            new JsonObject { ["id"] = "shadow-ram", ["assetId"] = "shadow-ram" }
        };

        var components = new JsonArray();
        void Component(string id, JsonObject state) => components.Add(new JsonObject
        {
            ["id"] = id, ["modelId"] = id, ["modelVersion"] = 1, ["state"] = state
        });
        // Component ids must stay in ordinal order (HcsSchema.Sorted). cpnet-staging sorts before drive-a.
        if (profile.CpNet) Component(CpNetStagingSchema.ComponentId, CpNetStagingSchema.Encode());
        if (driveA is not null) Component("drive-a", DriveState(driveA));
        if (driveB is not null) Component("drive-b", DriveState(driveB));
        if (expansionState is { } expansionElement)
            Component(ExpansionSchema.ComponentId, JsonNode.Parse(expansionElement.GetRawText())!.AsObject());
        if (x0Element is { } x0e)
            Component(ExpansionX0Schema.ComponentId, JsonNode.Parse(x0e.GetRawText())!.AsObject());
        if (x2State is { } x2Element)
            Component(ExpansionX2Schema.ComponentId, JsonNode.Parse(x2Element.GetRawText())!.AsObject());
        if (x3State is { } x3Element)
            Component(ExpansionX3Schema.ComponentId, JsonNode.Parse(x3Element.GetRawText())!.AsObject());
        if (x4State is { } x4Element)
            Component(ExpansionX4Schema.ComponentId, JsonNode.Parse(x4Element.GetRawText())!.AsObject());
        // The full-X5 file carries the driver roster and managed maps as a second, separately
        // validated component; the compatibility profile saves the v1 form (step-1 alone).
        if (x5State is { } x5Element)
            Component(ExpansionX5Schema.ComponentId, JsonNode.Parse(x5Element.GetRawText())!.AsObject());
        if (floppy is not null) components.Add(new JsonObject
        {
            ["id"] = "fdc", ["modelId"] = "fdc", ["modelVersion"] = 2, ["state"] = FdcState(fdcSnap!, sp1, sp2, units)
        });
        Component("frame-clock", new JsonObject
        {
            ["completedFrames"] = clock.CompletedFrames.ToString(CultureInfo.InvariantCulture),
            ["intAsserted"] = clock.IntAsserted
        });
        if (hddState is not null)
        {
            var slots = new JsonArray();
            for (var i = 0; i < 4; i++)
                slots.Add(new JsonObject { ["assetId"] = "hdd-" + (char)('c' + i),
                    ["generation"] = hddState.Slots[i].Generation, ["writeProtected"] = hddState.Slots[i].WriteProtected,
                    ["dirty"] = hddState.Slots[i].Dirty });
            Component(MachineSchema.HddComponentId, new JsonObject { ["pointer"] = hddState.Pointer,
                ["status"] = hddState.Status, ["slots"] = slots });
        }
        var (pressed, joysticks) = keyboard.Snapshot();
        Component("keyboard", new JsonObject
        {
            ["pressed"] = KeyArray(pressed, 50),
            ["joysticks"] = KeyArray(joysticks, 2)
        });
        var memoryState = new JsonObject
        {
            ["port7e"] = (uint)(memory.Latches.Port7E & 0x0f),
            ["cpm"] = memory.Latches.Cpm,
            ["shadow"] = memory.Latches.Shadow
        };
        if (profile.Memory.RamPaged is not null && memory.Latches.PagedEnabled)
        {
            var mTransfer = PagedRamPolicy.IsMTransfer(profile.Memory.RamPaged.PolicyId);
            var pagedState = new JsonObject
            {
                ["v"] = mTransfer ? 2 : 1, ["bank"] = memory.Latches.PagedBank,
                ["screen"] = (memory.Latches.PagedControl & 8) != 0,
                ["rom"] = (memory.Latches.PagedControl & 0x10) != 0,
                ["locked"] = (memory.Latches.PagedControl & 0x20) != 0,
                ["ramBytes"] = PagedRamPolicy.ExtraBytes.ToString(CultureInfo.InvariantCulture),
                ["ramSha256"] = Convert.ToHexStringLower(SHA256.HashData(capturedRam.AsSpan(0x10000)))
            };
            if (mTransfer)
            {
                pagedState["view"] = "m-transfer";
                pagedState["transfer"] = memory.Latches.PagedTransfer;
            }
            memoryState["paged"] = pagedState;
        }
        if (tapeVolume is not null) memoryState["tapeVolume"] = JsonSerializer.SerializeToNode(tapeVolume.CaptureState());
        Component("memory", memoryState);
        if (tapeState is not null) components.Add(new JsonObject
        {
            ["id"] = "tape", ["modelId"] = "tape", ["modelVersion"] = 2,
            ["state"] = JsonSerializer.SerializeToNode(tapeState.Component)
        });
        Component("ula", new JsonObject
        {
            ["feLatch"] = (uint)(ula.FeLatch & 0x1f),
            ["flashInverted"] = ula.FlashInverted,
            ["beeperTick"] = ula.Beeper.MasterTick.ToString(CultureInfo.InvariantCulture),
            ["beeperSpeaker"] = ula.Beeper.Speaker,
            ["beeperMicrophone"] = ula.Beeper.Microphone,
            ["tapeIn"] = ula.TapeIn
        });

        var cpuSection = cpuState.ToJson(expansion is not null);
        var clockSection = new JsonObject
        {
            ["epoch"] = "0",
            ["now"] = scheduler.Now.ToString(CultureInfo.InvariantCulture),
            ["masterHz"] = profile.MasterHz.ToString(CultureInfo.InvariantCulture),
            ["dividerNumerator"] = mapper.Numerator.ToString(CultureInfo.InvariantCulture),
            ["dividerDenominator"] = mapper.Denominator.ToString(CultureInfo.InvariantCulture),
            ["mapperWhole"] = mapper.WholeTicks.ToString(CultureInfo.InvariantCulture),
            ["mapperRemainder"] = mapper.Remainder.ToString(CultureInfo.InvariantCulture),
            // The remainder's own denominator: a fresh epoch writes the ratio denominator, a runtime
            // speed switch writes the lcm of the anchor's denominator and the ratio's.
            ["mapperRemainderDenominator"] = mapper.RemainderDenominator.ToString(CultureInfo.InvariantCulture),
            ["lastCpuTStates"] = mapper.CpuTStates.ToString(CultureInfo.InvariantCulture),
            ["nextSequence"] = scheduler.NextSequence.ToString(CultureInfo.InvariantCulture),
            ["deviceGenerations"] = Generations()
        };
        var events = new JsonArray();
        foreach (var e in scheduler.PendingEvents)
        {
            events.Add(new JsonObject
            {
                ["dueTick"] = e.DueTick.ToString(CultureInfo.InvariantCulture),
                ["phase"] = (uint)(int)e.Phase,
                ["deviceId"] = DeviceId(e.DeviceId),
                ["kind"] = Kind(e.Kind),
                ["generation"] = e.Generation.ToString(CultureInfo.InvariantCulture),
                ["sequence"] = e.InsertionSequence.ToString(CultureInfo.InvariantCulture),
                ["payload"] = new JsonObject { ["value"] = e.Payload.ToString(CultureInfo.InvariantCulture) },
                // The exact due (floor + fraction) of a CPU frontier event; device events have none
                // and stay at their integer tick. Legacy documents omit it.
                ["dueLogical"] = e.DueTime is { } due ? new JsonObject
                {
                    ["whole"] = due.WholeTicks.ToString(CultureInfo.InvariantCulture),
                    ["numerator"] = due.FractionNumerator.ToString(CultureInfo.InvariantCulture),
                    ["denominator"] = due.FractionDenominator.ToString(CultureInfo.InvariantCulture)
                } : null
            });
        }

        var definition = MachineSchema.Definition(profile);
        var manifest = new JsonObject
        {
            ["profile"] = new JsonObject
            {
                ["id"] = profile.Name,
                ["version"] = profile.Version,
                ["fingerprint"] = HcsCodec.ProfileFingerprint(definition),
                ["definition"] = JsonNode.Parse(definition.GetRawText())
            },
            ["assets"] = assets,
            ["cpu"] = cpuSection,
            ["clock"] = clockSection,
            ["stores"] = stores,
            ["components"] = components,
            ["events"] = events,
            ["pendingOutput"] = new JsonArray()
        };
        var logicalManifest = JsonDocument.Parse(manifest.ToJsonString()).RootElement.Clone();
        var schema = new MachineSchema(profile, wavPolicy);
        if (validateOnly)
        {
            // Use the codec's exact framing arithmetic; retain its existing admission bound.
            var manifestBytes = HcsCodec.CanonicalJson(logicalManifest, limits).Length;
            long wireBytes = 64L + manifestBytes;
            foreach (var blob in blobs) wireBytes = checked(wireBytes + 12L + blob.Length);
            if (wireBytes > limits.TotalBytes)
            {
                var lengths = string.Join(", ", assets.Select(a => $"{a!["id"]}={a["byteLength"]}"));
                Fail(HcsError.LimitExceeded,
                    $"file (total={wireBytes}; preview={limits.TotalBytes}; accepted={HcsLimits.ForProfile(profile).TotalBytes}; manifest={manifestBytes}; assets: {lengths})");
            }
        }
        byte[]? exactBuffer = null;
        if (destination is null && !validateOnly)
        {
            // One exact-sized buffer holds the whole container: the codec writes into it directly,
            // so no second GiB-sized array is ever allocated (the old MemoryStream + ToArray doubled it).
            long wireBytes = 64L + HcsCodec.CanonicalJson(logicalManifest, limits).Length;
            foreach (var blob in blobs) wireBytes = checked(wireBytes + 12L + blob.Length);
            if (wireBytes > limits.TotalBytes || wireBytes > Array.MaxLength) Fail(HcsError.LimitExceeded, "file");
            exactBuffer = new byte[(int)wireBytes];
        }
        using Stream ownedStream = destination is not null || validateOnly ? Stream.Null : new MemoryStream(exactBuffer!, writable: true);
        var stream = destination ?? ownedStream;
        if (profile.Memory.RamPaged is not null) HcsCodec.WriteOwned(stream, logicalManifest, blobs, schema, limits);
        else HcsCodec.Write(stream, logicalManifest, blobs, schema, limits);
        return validateOnly || destination is not null ? [] : exactBuffer!;
    }
    /// <summary>Adopts a .hcs document onto this fresh machine: container and profile validation
    /// first, then cross-state consistency against the live composition, then the component
    /// restore. Any mismatch fails with <see cref="HcsError"/>.</summary>
    internal TapeFailure? CheckCassette(TapeCassetteImage candidate, HcsWavPolicy? wavPolicy)
    {
        try
        {
            // Use the composition's exact Save/Load policy, including an explicit paged opt-out.
            // The 4096 headroom covers generation/manifest digits changed by the subsequent Insert.
            var limits = HcsLimits.ForProfile(profile);
            _ = SaveCore(wavPolicy, limits with { TotalBytes = limits.TotalBytes - 4096 }, candidate, true);
            return null;
        }
        catch (Exception e) when (e is HcsException or ArgumentException or OverflowException or InvalidOperationException)
        { return new(TapeError.BoundsExceeded, "Candidate cannot persist: " + e.Message); }
    }

    public void Load(byte[] file, HcsWavPolicy? wavPolicy = null, HcsLimits? limits = null)
    {
        using var stream = new MemoryStream(file, writable: false);
        Load(stream, wavPolicy, limits);
    }

    /// <summary>Adopts a seekable HCS1 stream without retaining a GiB-sized input copy.</summary>
    public void Load(Stream stream, HcsWavPolicy? wavPolicy = null, HcsLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        CheckExternalState();
        limits ??= DefaultLimits();
        wavPolicy?.Reserve(limits.TotalBytes);
        var frontier = ExpansionSchema.Enabled(profile) || ExpansionSchema.X5(profile);
        if (frontier != (expansion is not null)) Fail(HcsError.StateUnsupported, "expansion composition");
        if (expansion is not null && expansion.UnitCount != ExpansionArena.ComputeCount + profile.ExpansionDriverCount)
            Fail(HcsError.StateUnsupported, "expansion-x5 composition");
        if (ExpansionSchema.X5(profile) && !expansion!.ManagedMapComposed)
            Fail(HcsError.StateUnsupported, "managed map composition");
        if (scheduler.Now != 0 || mapper.CpuTStates != 0)
            Fail(HcsError.StateInconsistent, "fresh");
        var doc = HcsCodec.ReadForLoad(stream, new MachineSchema(profile, wavPolicy), limits);
        var m = doc.Manifest;

        // The schema admitted the document before any blob or store was read. The full-X5 restore
        // decodes the separately validated expansion-x5 component here, before the step-1 component
        // is read with the X5 context, so a malformed or mismatched roster or ownership is refused
        // before any scheduler, RAM, CPU or grant mutation below.
        ExpansionX5State? x5State = null;
        if (ExpansionSchema.X5(profile))
        {
            var x5Component = m.GetProperty("components").EnumerateArray()
                .Single(c => c.GetProperty("id").GetString() == ExpansionX5Schema.ComponentId);
            x5State = ExpansionX5Schema.Read(x5Component.GetProperty("state"));
        }
        var cpuState = CpuLogicalState.FromJson(m.GetProperty("cpu"), expansion is not null);
        var expansionState = expansion is null ? null : ExpansionSchema.Read(m, x5State is not null, x5State);
        var c = m.GetProperty("clock");
        var now = Counter(c, "now");
        var lastCpuT = Counter(c, "lastCpuTStates");
        var nextSequence = Counter(c, "nextSequence");
        var mapperWhole = Counter(c, "mapperWhole");
        var mapperRemainder = Counter(c, "mapperRemainder");
        // Absent in a legacy document: the remainder then shares the ratio denominator (fresh epoch).
        var mapperRemainderDenominator = c.TryGetProperty("mapperRemainderDenominator", out var drElement) && drElement.ValueKind != JsonValueKind.Null
            ? Counter(c, "mapperRemainderDenominator") : Counter(c, "dividerDenominator");
        var versionedClock = c.TryGetProperty("mapperRemainderDenominator", out _);
        if (Counter(c, "dividerNumerator") != mapper.Numerator || Counter(c, "dividerDenominator") != mapper.Denominator)
            Fail(HcsError.StateInconsistent, "clock");

        var generations = new List<(int Device, long Generation)>();
        foreach (var g in c.GetProperty("deviceGenerations").EnumerateArray())
            generations.Add((ParseDeviceId(g.GetProperty("id").GetString()!), Counter(g, "generation")));
        CheckRoster(generations);

        var events = new List<DeviceEvent>();
        var flashDue = 0L;
        var flashCount = 0;
        foreach (var e in m.GetProperty("events").EnumerateArray())
        {
            var device = ParseDeviceId(e.GetProperty("deviceId").GetString()!);
            var kind = ParseKind(e.GetProperty("kind").GetString()!);
            LogicalTime? dueLogical = null;
            if (e.TryGetProperty("dueLogical", out var dueElement) && dueElement.ValueKind == JsonValueKind.Object)
                dueLogical = new LogicalTime(Counter(dueElement, "whole"), Counter(dueElement, "numerator"), Counter(dueElement, "denominator"));
            events.Add(new DeviceEvent(
                Counter(e, "dueTick"), (EventPhase)Num(e, "phase"), device, kind,
                Counter(e, "generation"), Counter(e, "payload", "value"), Counter(e, "sequence"), dueLogical));
            if (device == flashDeviceId) { flashDue = Counter(e, "dueTick"); flashCount++; }
        }
        // The Ula flash is a free-running toggle: exactly one pending deadline at any drained boundary.
        if (flashCount != 1) Fail(HcsError.StateInconsistent, "flash");

        var components = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var versions = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var comp in m.GetProperty("components").EnumerateArray())
        {
            components[Text(comp, "id")] = comp.GetProperty("state");
            versions[Text(comp, "id")] = Num(comp, "modelVersion");
        }

        if (components.ContainsKey(MachineSchema.HddComponentId) != (hdd is not null))
            Fail(HcsError.StateInconsistent, "HDD composition");
        var restoredHdd = hdd is null ? null : MachineSchema.ReadHddState(components[MachineSchema.HddComponentId],
            id => doc.CopyBlob(AssetBlobId(m, id)));

        if (components.ContainsKey("tape") != (tape is not null) || components.ContainsKey("fdc") != (floppy is not null)
            || components.ContainsKey("drive-a") != (floppy is not null) || components.ContainsKey("drive-b") != (floppy is not null))
            Fail(HcsError.StateInconsistent, "composition");
        // The v1-save rule: a save without the expansion-x0 component restores the explicit empty
        // engine; a component without the expansion composition is inconsistent.
        if (components.ContainsKey(ExpansionX0Schema.ComponentId) && expansion?.X0 is null)
            Fail(HcsError.StateInconsistent, "expansion-x0 composition");
        JsonElement? x0Element = components.TryGetValue(ExpansionX0Schema.ComponentId, out var x0ComponentState) ? x0ComponentState : null;
        if (profile.CpNet && (x0Element is null || expansion?.X0?.CpNet is null || !memory.HasCpNetStagingClaim()
            || !components.ContainsKey(CpNetStagingSchema.ComponentId)))
            Fail(HcsError.StateInconsistent, "cpnet staging composition");
        var hasX2 = components.TryGetValue(ExpansionX2Schema.ComponentId, out var x2ComponentState);
        if (hasX2 != (expansion?.X2 is not null) || hasX2 && (x0Element is null || expansion?.X0 is null))
            Fail(HcsError.StateInconsistent, "expansion-x2 composition");
        var x2State = hasX2 ? ExpansionX2Schema.Read(x2ComponentState) : null;
        var hasX3 = components.TryGetValue(ExpansionX3Schema.ComponentId, out var x3ComponentState);
        if (hasX3 != (expansion?.X3 is not null) || hasX3 && (x0Element is null || expansion?.X0 is null || x5State is null))
            Fail(HcsError.StateInconsistent, "expansion-x3 composition");
        var x3State = hasX3 ? ExpansionX3Schema.Read(x3ComponentState) : null;
        var hasX4 = components.TryGetValue(ExpansionX4Schema.ComponentId, out var x4ComponentState);
        if (hasX4 != (expansion?.X4 is not null) || hasX4 && (x0Element is null || expansion?.X0 is null || x5State is null))
            Fail(HcsError.StateInconsistent, "expansion-x4 composition");
        var x4State = hasX4 ? ExpansionX4Schema.Read(x4ComponentState) : null;
        var assetIds = new HashSet<string>();
        foreach (var a in m.GetProperty("assets").EnumerateArray()) assetIds.Add(Text(a, "id"));
        if (assetIds.Contains("tape") != (tape is not null && versions["tape"] == 1) || !assetIds.Contains("ram")
            || !assetIds.Contains("rom") || !assetIds.Contains("shadow-ram") || !assetIds.Contains("shadow-rom"))
            Fail(HcsError.StateInconsistent, "composition");

        // Stores: sizes must match this machine; rom/shadow-rom bytes must bind to the live stores.
        foreach (var s in m.GetProperty("stores").EnumerateArray())
        {
            var id = Text(s, "id");
            var assetId = Text(s, "assetId");
            var asset = m.GetProperty("assets").EnumerateArray().First(a => Text(a, "id") == assetId);
            var len = (int)Counter(asset, "byteLength");
            if (id == "ram" && len != memory.Ram.Length) Fail(HcsError.StateInconsistent, "stores");
            if (id == "shadow-ram" && len != memory.ShadowRam.Length) Fail(HcsError.StateInconsistent, "stores");
        }
        if (!BlobEquals(doc.CopyBlob(AssetBlobId(m, "rom")), memory.Rom)) Fail(HcsError.StateInconsistent, "rom");
        if (!BlobEquals(doc.CopyBlob(AssetBlobId(m, "shadow-rom")), memory.ShadowRom)) Fail(HcsError.StateInconsistent, "rom");

        // FDC: composition binding and idle invariants (file-internal pairing of events and state).
        int[] units = [];
        var fdcSnap = default(FdcDiagnosticSnapshot);
        byte sp1 = 0, sp2 = 0;
        var driveImages = new Dictionary<string, Dsk?>();
        var driveBase = new Dictionary<string, byte[]?>();
        if (floppy is not null)
        {
            var f = components["fdc"];
            var t = f.GetProperty("timing");
            if (
                Num(t, "masterHz") != floppy.Timing.MasterHz || Num(t, "fdcz") != floppy.Timing.FdcHz
                || Num(t, "mfmBitsPerSecond") != floppy.Timing.MfmBitsPerSecond || Num(t, "rpm") != floppy.Timing.Rpm
                || Num(t, "indexPulseMicroseconds") != floppy.Timing.IndexPulseMicroseconds
                || f.GetProperty("instant").GetBoolean() != floppy.Instant
                || f.GetProperty("driveB80TrackJumper").GetBoolean() != floppy.DriveB80TrackJumper)
                Fail(HcsError.StateInconsistent, "fdc");
            sp1 = (byte)Num(f, "specify1"); sp2 = (byte)Num(f, "specify2");
            units = f.GetProperty("headLoadedUnits").EnumerateArray().Select(u => (int)u.GetUInt32()).ToArray();
            if (units.Distinct().Count() != units.Length) Fail(HcsError.StateInconsistent, "fdc");
            if (floppy.DriveA.HeadLoaded != units.Contains(0) || floppy.DriveB.HeadLoaded != units.Contains(1))
                Fail(HcsError.StateInconsistent, "fdc");
            var unload = events.Where(e => e.DeviceId == floppy.FdcDeviceId && e.Kind == 5).Select(e => (int)e.Payload).ToHashSet();
            if (!unload.SetEquals(units)) Fail(HcsError.StateInconsistent, "fdc");
            var motor = events.Count(e => e.DeviceId == floppy.MotorDeviceId && e.Kind == 0);
            // The motor monostable is high while its trigger (b3/D3) is held OR while the discharge tail is
            // pending. A held trigger leaves no scheduled event, so motorTimerRunning may be true with zero
            // motor events when D3 (latch bit 0x08) is asserted. At most one tail event may be pending.
            var d3Held = (f.GetProperty("latch").GetUInt32() & 0x08) != 0;
            if (motor is > 1 || f.GetProperty("motorTimerRunning").GetBoolean() != (motor == 1 || d3Held))
                Fail(HcsError.StateInconsistent, "fdc");
            if (events.Any(e => e.DeviceId >= floppy.FdcDeviceId + FdcDriveUnitOffset && e.DeviceId <= floppy.FdcDeviceId + 5))
                Fail(HcsError.StateInconsistent, "fdc");
            fdcSnap = new FdcDiagnosticSnapshot(
                fdcPhaseFromString(f.GetProperty("phase").GetString()!), (byte)Num(f, "mainStatus"),
                new ReadOnlyCollection<byte>([]),
                ByteList(f, "lastCommand"),
                (byte?)NumOrNull(f, "lastSt0"), (byte?)NumOrNull(f, "lastSt1"),
                (byte?)NumOrNull(f, "lastSt2"), (byte?)NumOrNull(f, "lastSt3"),
                ByteList(f, "pcn"),
                Array.AsReadOnly(f.GetProperty("pendingInterrupts").EnumerateArray().Select(p => ((byte)p.GetProperty("s").GetUInt32(), (byte)p.GetProperty("c").GetUInt32())).ToArray()));
            driveImages["drive-a"] = DriveMedia(m, doc, "disk-a-current", "disk-a-base");
            driveImages["drive-b"] = DriveMedia(m, doc, "disk-b-current", "disk-b-base");
            driveBase["drive-a"] = AssetBlob(m, doc, "disk-a-base");
            driveBase["drive-b"] = AssetBlob(m, doc, "disk-b-base");
            // The "disk-*-base" provenance assets are optional: present only when the drive was mounted.
            if (!assetIds.Contains("disk-a-current") || !assetIds.Contains("disk-b-current"))
                Fail(HcsError.StateInconsistent, "composition");
        }

        TapeVolumeState? volumeState = null;
        bool hasVolumeState = components["memory"].TryGetProperty("tapeVolume", out var volumeJson);
        if (hasVolumeState != (tapeVolume is not null)) Fail(HcsError.StateInconsistent, "tape volume composition");
        if (hasVolumeState)
        {
            try
            {
                volumeState = volumeJson.Deserialize<TapeVolumeState>() ?? throw new ArgumentException("volume state");
                tapeVolume!.ValidateState(volumeState);
                var pagingEngaged = profile.Name == MachineProfile.Hc2000V2FullWorkstationV1.Name || volumeState.WindowEngaged;
                if (pagingEngaged != components["memory"].TryGetProperty("paged", out _))
                    throw new ArgumentException("Tape window engagement disagrees with paging state.");
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or OverflowException)
            { Fail(HcsError.StateInconsistent, "tape volume metadata"); }
        }
        // Stage the entire tape (including legacy conversion and event pairing) before adoption.
        TapeDeckCore? tapeCandidate = null;
        var tapeEvents = events.Where(e => tape is not null && e.DeviceId == tape.DeviceId).ToArray();
        if (tapeEvents.Length > 1) Fail(HcsError.StateInconsistent, "tape events");
        if (tape is not null)
        {
            if (!tape.CanAdoptHcs) Fail(HcsError.StateInconsistent, "fresh tape");
            var tp = components["tape"];
            var gen = generations.Single(g => g.Device == tape.DeviceId).Generation;
            var pending = tapeEvents.SingleOrDefault();
            TapeDeckState detached;
            if (versions["tape"] == 1)
            {
                if (Counter(tp, "pendingToken") != (pending?.InsertionSequence ?? 0)) Fail(HcsError.StateInconsistent, "tape1 token");
                CheckTapeAsset("tape", "tape", "tap1");
                var blob = doc.CopyBlob(AssetBlobId(m, "tape"));
                var media = blob.Length == 0 ? null : UnframeMedia(blob);
                var oldState = TapeName(Text(tp, "state"));
                var oldTarget = Int128.Parse(Signed(tp, "idealTarget"));
                var oldRemaining = Int128.Parse(Signed(tp, "remaining"));
                // tape1 Snapshot() stored the dormant stop/resume remainder even while PLAY
                // advanced target. Cursor's logical observation derives the active residue.
                if (oldState == TapeState.Playing && (oldRemaining <= 0 || oldRemaining > (Int128)3_496_500 * FrameTiming.MasterHz))
                    Fail(HcsError.StateInconsistent, "tape1 dormant remainder");
                var residue = oldState == TapeState.Playing ? checked(oldTarget - (Int128)now * TapeTransport.ReferenceHz) : oldRemaining;
                var cursor = new TapeCursor(oldState, (int)Num(tp, "blockIndex"), (TapeStage)Num(tp, "stage"),
                    (int)Num(tp, "pilotConsumed"), (int)Num(tp, "nextBit"), (int)Num(tp, "half"), tp.GetProperty("waveformLevel").GetBoolean(),
                    oldTarget, residue, Counter(tp, "mediaIdentity"), gen);
                var migrated = tape.Migrate(new(cursor, media is null && cursor.State == TapeState.Unloaded ? [] : [new("tape-source-0", TapeFormat.Tap, TapeLegacyMigration.RawTap(media ?? []))], now, tape.DeviceId, gen, pending), new());
                if (!migrated.Success) Fail(migrated.Failure!.Code is TapeError.CapacityExceeded or TapeError.BoundsExceeded ? HcsError.StateUnsupported : HcsError.StateInconsistent, "tape1 migration");
                detached = migrated.Value with { Component = migrated.Value.Component with { MicLevel = components["ula"].GetProperty("beeperMicrophone").GetBoolean() } };
                // Preserve sequence/generation, adopt the verified new logical boundary exactly once.
                events.RemoveAll(e => e.DeviceId == tape.DeviceId);
                pending = detached.Component.PendingKind is { } kind ? new DeviceEvent(
                    TapeNativeCodec.Rational(detached.Component.IdealDue!).Ceiling(), EventPhase.SignalEdge, tape.DeviceId, (int)kind,
                    gen, 0, checked((long)TapeNativeCodec.Unsigned(detached.Component.PendingToken))) : null;
                if (pending is not null) events.Add(pending);
            }
            else
            {
                var component = TapeComponentCodec.Read(tp);
                var sources = new List<TapeSourceAsset>();
                foreach (var asset in m.GetProperty("assets").EnumerateArray().Where(a => Text(a, "id").StartsWith("tape-source-", StringComparison.Ordinal)))
                {
                    var id = Text(asset, "id"); var format = Text(asset, "format");
                    if (Text(asset, "kind") != "buffer" || format is not ("tap1" or "tzx1" or "pzx1" or "csw1" or "wav1")) Fail(HcsError.AssetMismatch, "tape source");
                    sources.Add(new(id, TapeNativeCodec.FormatValue(format[..^1]), doc.CopyBlob(AssetBlobId(m, id))));
                }
                TapeCassetteImage? cassette = null;
                if (component.CassetteAssetId is not null)
                {
                    if (component.CassetteAssetId != TapeSnapshotContract.CassetteAssetId) Fail(HcsError.AssetMismatch, "cassette reference");
                    CheckTapeAsset(TapeSnapshotContract.CassetteAssetId, TapeSnapshotContract.CassetteAssetKind, TapeSnapshotContract.CassetteAssetFormat);
                    var native = TapeNativeCodec.Read(doc.CopyBlob(AssetBlobId(m, TapeSnapshotContract.CassetteAssetId)), sources, new(), wavPolicy?.TapeLimits);
                    if (!native.Success) Fail(native.Failure!.Code switch
                    {
                        TapeError.UnsupportedVersion => HcsError.UnsupportedVersion,
                        TapeError.BoundsExceeded => HcsError.LimitExceeded,
                        _ => HcsError.StateInconsistent
                    }, "native tape");
                    cassette = native.Value;
                }
                else if (assetIds.Contains(TapeSnapshotContract.CassetteAssetId) || sources.Count != 0) Fail(HcsError.AssetMismatch, "orphan tape assets");
                detached = new(component, cassette);
            }
            var ulaState = components["ula"];
            if (detached.Component.WaveformLevel != ulaState.GetProperty("tapeIn").GetBoolean()
                || detached.Component.MicLevel != ulaState.GetProperty("beeperMicrophone").GetBoolean()
                || detached.Component.MicLevel != ((Num(ulaState, "feLatch") & 8) != 0)) Fail(HcsError.StateInconsistent, "tape ULA pins");
            if (volumeState is not null) detached = detached with { MediaEpoch = volumeState.DeckEpoch };
            var staged = tape.ValidateRestoredState(detached, now, pending, gen);
            if (!staged.Success) Fail(staged.Failure!.Code == TapeError.RuntimeUnavailable ? HcsError.StateUnsupported : HcsError.StateInconsistent, "tape state");
            tapeCandidate = staged.Value;
        }
        else if (assetIds.Contains(TapeSnapshotContract.CassetteAssetId) || assetIds.Any(id => id.StartsWith("tape-source-", StringComparison.Ordinal)))
            Fail(HcsError.AssetMismatch, "uncomposed tape assets");
        // Prove the clock identity before changing any live scheduler/store/device. A legacy document
        // (no remainder denominator) is the zero epoch: lastCpuT * ratio. A versioned one carries the
        // exact anchor, which a runtime speed switch re-bases, so it is never re-derived from the counter.
        if (!versionedClock)
        {
            var mapped = (System.Numerics.BigInteger)lastCpuT * mapper.Numerator;
            if (mapped / mapper.Denominator != mapperWhole || mapped % mapper.Denominator != mapperRemainder) Fail(HcsError.StateInconsistent, "clock");
        }
        else if (mapperRemainderDenominator % mapper.Denominator != 0 || mapperRemainder >= mapperRemainderDenominator
            || mapperWhole > now)
            Fail(HcsError.StateInconsistent, "clock");

        void CheckTapeAsset(string id, string kind, string format)
        {
            var rows = m.GetProperty("assets").EnumerateArray().Where(a => Text(a, "id") == id).ToArray();
            if (rows.Length != 1 || Text(rows[0], "kind") != kind || Text(rows[0], "format") != format) Fail(HcsError.AssetMismatch, "tape asset");
        }

        byte[]? restoredPageOwners = null;
        if (expansionState is not null)
        {
            try { restoredPageOwners = expansion!.PrepareGrantAdoption(expansionState, x5State); }
            catch (ArgumentException) { Fail(HcsError.StateInconsistent, "expansion arena ownership"); }
            var savedBank = components["memory"].GetProperty("paged").GetProperty("bank").GetUInt16();
            if (!memory.ExpansionArena!.IsDevicePageMainMappable(savedBank))
                Fail(HcsError.StateInconsistent, "expansion arena main bank");
        }
        bool SavedDmaPageAllowed(ushort page)
        {
            if (restoredPageOwners is null || restoredPageOwners[page] != 0 || memory.IsCardReservedPage(page) ||
                memory.ClaimedPagedRanges.Any(range => page >= range.FirstPage && page < range.FirstPage + range.PageCount))
                return false;
            return true;
        }
        bool SavedDmaTransferAllowed(ExpansionX0.X0DmaChannelState transfer) =>
            restoredPageOwners is not null && restoredPageOwners[transfer.Page] == 0 &&
            !memory.ClaimedPagedRanges.Any(range => transfer.Page >= range.FirstPage && transfer.Page < range.FirstPage + range.PageCount) &&
            memory.ExpansionArena!.AllowsDma(transfer.CardId, transfer.Direction, transfer.Page, transfer.Offset, transfer.Count);
        if (expansion?.X0 is { } liveX0)
        {
            try
            {
                if (x0Element is { } x0Json)
                    liveX0.ValidateState(ExpansionX0Schema.Read(x0Json), now, events, SavedDmaTransferAllowed);
                else liveX0.ValidateEmpty(now, events);
            }
            catch (ArgumentException) { Fail(HcsError.StateInconsistent, "expansion-x0"); }
        }
        if (x2State is not null)
        {
            try { expansion!.X2!.ValidateMetadata(x2State, ExpansionX0Schema.Read(x0Element!.Value), now, events); }
            catch (ArgumentException) { Fail(HcsError.StateInconsistent, "expansion-x2"); }
        }
        if (x3State is not null)
        {
            var card = expansion!.X3!;
            try { card.ValidateMetadata(x3State, ExpansionX0Schema.Read(x0Element!.Value), now, events,
                SavedDmaPageAllowed, owner => card.SavedOwnerAllowed(owner, x5State!)); }
            catch (ArgumentException) { Fail(HcsError.StateInconsistent, "expansion-x3"); }
        }
        if (x4State is not null)
        {
            var card = expansion!.X4!;
            try { card.ValidateMetadata(x4State, ExpansionX0Schema.Read(x0Element!.Value), now, events,
                owner => card.SavedOwnerAllowed(owner, x5State!)); }
            catch (ArgumentException) { Fail(HcsError.StateInconsistent, "expansion-x4"); }
        }
        // The mapper takes the document's clock identity before anything reads it: the machine
        // resumes at the saved boundary and the expansion frontier is re-derived at that rate.
        try { mapper.Restore(lastCpuT, mapperWhole, mapperRemainder, mapperRemainderDenominator); }
        catch (ArgumentOutOfRangeException) { Fail(HcsError.StateInconsistent, "clock"); }
        if (mapper.WholeTicks != mapperWhole || mapper.Remainder != mapperRemainder || mapper.RemainderDenominator != mapperRemainderDenominator)
            Fail(HcsError.StateInconsistent, "clock");
        // The machine resumes at the saved boundary, not at the committed CPU's time: a drained
        // save rests exactly on the committed CPU's exact time (whole == now, the fraction in the
        // remainder); a partial save admitted devices ahead of the committed CPU (whole < now) and
        // the machine resumes at the document's now tick, as the legacy path did.
        var boundary = mapperWhole == now ? mapper.ExactTime() : LogicalTime.Tick(now);
        try { scheduler.ValidateRestore(boundary, events, nextSequence, generations); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        { Fail(HcsError.StateInconsistent, "scheduler"); }
        // ---- restore (one validated event set; no counter-by-counter generation replay) ----
        scheduler.RestoreEvents(boundary, events, nextSequence, generations);
        if (expansionState is not null)
        {
            cpu.Restore(cpuState);
            var mainBank = components["memory"].GetProperty("paged").GetProperty("bank").GetUInt16();
            if (x5State is null) expansion!.AdoptState(expansionState, mainBank, events);
            else expansion!.AdoptX5FullState(expansionState, x5State, mainBank, events);
            if (expansion.X0 is { } x0Platform)
            {
                try
                {
                    if (x0Element is { } x0Json) x0Platform.AdoptState(ExpansionX0Schema.Read(x0Json), now, events);
                    else x0Platform.AdoptEmpty(now, events);
                }
                catch (ArgumentException) { Fail(HcsError.StateInconsistent, "expansion-x0"); }
            }
            else if (x0Element is not null)
                Fail(HcsError.StateInconsistent, "expansion-x0 composition");
        }
        if (restoredHdd is not null) hdd!.Restore(restoredHdd);
        doc.CopyBlobTo(AssetBlobId(m, "ram"), memory.Ram.Span);
        if (x2State is not null)
            expansion!.X2!.AdoptMetadata(x2State, ExpansionX0Schema.Read(x0Element!.Value), now, events);
        if (x3State is not null)
        {
            var card = expansion!.X3!;
            card.AdoptMetadata(x3State, ExpansionX0Schema.Read(x0Element!.Value), now, events,
                SavedDmaPageAllowed, owner => card.SavedOwnerAllowed(owner, x5State!));
        }
        if (x4State is not null)
        {
            var card = expansion!.X4!;
            card.AdoptMetadata(x4State, ExpansionX0Schema.Read(x0Element!.Value), now, events,
                owner => card.SavedOwnerAllowed(owner, x5State!));
        }
        doc.CopyBlob(AssetBlobId(m, "shadow-ram")).CopyTo(memory.ShadowRam.Span);
        var mem = components["memory"];
        memory.RestoreLatches(new PagingLatches((byte)Num(mem, "port7e"),
            mem.GetProperty("cpm").GetBoolean(), mem.GetProperty("shadow").GetBoolean(),
            PagedBank: mem.TryGetProperty("paged", out var paged) && paged.ValueKind == JsonValueKind.Object ? paged.GetProperty("bank").GetUInt16() : (ushort)0,
            PagedControl: paged.ValueKind == JsonValueKind.Object ? (byte)((paged.GetProperty("screen").GetBoolean() ? 8 : 0)
                | (paged.GetProperty("rom").GetBoolean() ? 0x10 : 0) | (paged.GetProperty("locked").GetBoolean() ? 0x20 : 0)) : (byte)0,
            PagedEnabled: memory.Profile.RamPaged is not null && paged.ValueKind == JsonValueKind.Object,
            PagedTransfer: PagedRamPolicy.IsMTransfer(memory.Profile.RamPaged?.PolicyId)
                && paged.ValueKind == JsonValueKind.Object && paged.GetProperty("transfer").GetBoolean()));
        var fc = components["frame-clock"];
        clock.Restore(Counter(fc, "completedFrames"), fc.GetProperty("intAsserted").GetBoolean());
        var u = components["ula"];
        ula.RestoreSnapshot((byte)Num(u, "feLatch"), u.GetProperty("flashInverted").GetBoolean(),
            new BeeperEdge(Counter(u, "beeperTick"), u.GetProperty("beeperSpeaker").GetBoolean(),
                u.GetProperty("beeperMicrophone").GetBoolean()),
            u.GetProperty("tapeIn").GetBoolean(), flashDue);
        var kb = components["keyboard"];
        keyboard.Restore(IntList(kb, "pressed"), IntList(kb, "joysticks"));
        if (tape is not null && tapeCandidate is not null) tape.AdoptValidatedState(tapeCandidate);
        if (tapeVolume is not null && volumeState is not null) tapeVolume.RestoreState(volumeState);
        if (floppy is not null)
        {
            RestoreDrive(floppy.DriveA, "drive-a");
            RestoreDrive(floppy.DriveB, "drive-b");
            floppy.RestoreIdle((byte)Num(components["fdc"], "latch"), components["fdc"].GetProperty("motorTimerRunning").GetBoolean());
            floppy.Fdc.RestoreIdle(fdcSnap!, sp1, sp2,
            [
                units.Contains(0) ? floppy.DriveA : null,
                units.Contains(1) ? floppy.DriveB : null,
                null, null
            ]);
        }
        if (expansion is null) cpu.Restore(cpuState);
        return;

        void RestoreDrive(FloppyDrive drive, string id)
        {
            var s = components[id];
            var dirty = s.GetProperty("dirtySectors").EnumerateArray()
                .Select(d => new DirtySector((int)Num(d, "c"), (int)Num(d, "s"), (int)Num(d, "i"))).ToList();
            drive.RestoreSnapshot(driveImages[id], (int)Num(s, "headPosition"), s.GetProperty("headLoaded").GetBoolean(),
                s.GetProperty("motorOn").GetBoolean(), Counter(s, "rotationOrigin"),
                s.GetProperty("writeProtected").GetBoolean(), s.GetProperty("diskChanged").GetBoolean(),
                dirty, driveBase[id]);
        }
    }

    // ---- capture helpers ----

    private static JsonObject DriveState(FloppyDrive drive)
    {
        var o = new JsonObject
        {
            ["headPosition"] = (uint)drive.HeadPosition,
            ["headLoaded"] = drive.HeadLoaded,
            ["motorOn"] = drive.MotorOn,
            ["rotationOrigin"] = drive.RotationOrigin.ToString(CultureInfo.InvariantCulture),
            ["writeProtected"] = drive.WriteProtected,
            ["diskChanged"] = drive.DiskChanged
        };
        var dirty = new JsonArray();
        foreach (var d in drive.DirtySectors)
            dirty.Add(new JsonObject { ["c"] = (uint)d.Cylinder, ["s"] = (uint)d.Side, ["i"] = (uint)d.SectorIndex });
        o["dirtySectors"] = dirty;
        return o;
    }

    private JsonObject FdcState(FdcDiagnosticSnapshot snap, byte sp1, byte sp2, int[] units)
    {
        var o = new JsonObject
        {
            ["phase"] = snap.Phase.ToString().ToLowerInvariant(),
            ["mainStatus"] = (uint)snap.MainStatus,
            ["specify1"] = (uint)sp1,
            ["specify2"] = (uint)sp2,
            ["pcn"] = ByteList(snap.PresentCylinders),
            ["latch"] = (uint)(floppy!.Latch & 0x1f),
            ["motorTimerRunning"] = floppy.MotorTimerRunning,
            ["lastCommand"] = ByteList(snap.LastCommand),
            ["lastSt0"] = snap.LastSt0 is byte st0 ? (uint)st0 : null,
            ["lastSt1"] = snap.LastSt1 is byte st1 ? (uint)st1 : null,
            ["lastSt2"] = snap.LastSt2 is byte st2 ? (uint)st2 : null,
            ["lastSt3"] = snap.LastSt3 is byte st3 ? (uint)st3 : null,
            ["instant"] = floppy.Instant,
            ["timing"] = new JsonObject
            {
                ["masterHz"] = floppy.Timing.MasterHz,
                ["fdcz"] = floppy.Timing.FdcHz,
                ["mfmBitsPerSecond"] = floppy.Timing.MfmBitsPerSecond,
                ["rpm"] = floppy.Timing.Rpm,
                ["indexPulseMicroseconds"] = floppy.Timing.IndexPulseMicroseconds
            },
            ["driveB80TrackJumper"] = floppy.DriveB80TrackJumper
        };
        var loaded = new JsonArray();
        foreach (var u in units) loaded.Add((uint)u);
        o["headLoadedUnits"] = loaded;
        var ints = new JsonArray();
        if (snap.PendingInterrupts is { Count: > 0 })
            foreach (var (st, cyl) in snap.PendingInterrupts)
                ints.Add(new JsonObject { ["s"] = (uint)st, ["c"] = (uint)cyl });
        o["pendingInterrupts"] = ints;
        return o;
    }

    private JsonArray Generations()
    {
        var ids = new List<int> { clock.DeviceId, flashDeviceId };
        if (expansion is not null)
            for (var id = 0; id <= expansion.UnitCount; id++) ids.Add(ExpansionFrontierDriver.FirstCpuDeviceId + id);
        if (expansion?.X0 is { } x0) ids.AddRange(x0.Dma.DeviceIds);
        if (expansion?.X2 is { } x2) ids.Add(x2.DeviceId);
        if (expansion?.X3 is { } x3) { ids.Add(x3.ClockDevice); ids.Add(x3.PumpDevice); }
        if (expansion?.X4 is { } x4) ids.Add(x4.EngineDevice);
        if (tape is not null) ids.Add(tape.DeviceId);
        if (floppy is not null)
            for (var d = floppy.FdcDeviceId; d <= floppy.FdcDeviceId + 5; d++) ids.Add(d);
        var rows = ids.Select(i => new JsonObject
        {
            ["id"] = DeviceId(i),
            ["generation"] = scheduler.Generation(i).ToString(CultureInfo.InvariantCulture)
        }).OrderBy(r => r["id"]!.GetValue<string>(), StringComparer.Ordinal).ToArray();
        // Sorted by the id string ordinal (the base schema requires ordinal id order).
        var arr = new JsonArray();
        foreach (var r in rows) arr.Add(r);
        return arr;
    }

    private static JsonArray KeyArray(int[] values, int count)
    {
        if (values.Length != count) throw new InvalidOperationException("keyboard capture mismatch.");
        var a = new JsonArray();
        foreach (var v in values) a.Add((uint)v);
        return a;
    }

    // ---- load helpers ----

    private Dsk? DriveMedia(JsonElement m, HcsDocument doc, string currentId, string baseId)
    {
        var current = doc.CopyBlob(AssetBlobId(m, currentId));
        var hasBase = m.GetProperty("assets").EnumerateArray().Any(a => Text(a, "id") == baseId);
        if (current.Length == 0)
        {
            if (hasBase) Fail(HcsError.StateInconsistent, "media");
            return null;
        }
        try
        {
            return Dsk.Load(current);
        }
        catch (Exception e)
        {
            Fail(HcsError.StateInconsistent, "media");
            throw new InvalidOperationException("unreachable", e);
        }
    }

    private static IReadOnlyList<byte[]>? UnframeMedia(byte[] blob)
    {
        if (blob.Length < 4) Fail(HcsError.InvalidFormat, "tape-media");
        var count = BinaryPrimitives.ReadInt32LittleEndian(blob);
        if (count is < 0 or > 4096 || blob.Length < 4L + 4L * count) Fail(HcsError.InvalidFormat, "tape-media");
        var blocks = new List<byte[]>(count);
        var off = 4;
        for (var i = 0; i < count; i++)
        {
            var len = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(off));
            off += 4;
            if (len < 0 || len > 65535 || (long)off + len > blob.Length) Fail(HcsError.InvalidFormat, "tape-media");
            blocks.Add(blob.AsSpan(off, len).ToArray());
            off += len;
        }
        if (off != blob.Length) Fail(HcsError.InvalidFormat, "tape-media");
        return blocks;
    }

    private void CheckRoster(List<(int Device, long Generation)> generations)
    {
        var live = new HashSet<int> { clock.DeviceId, flashDeviceId };
        if (expansion is not null)
            for (var id = 0; id <= expansion.UnitCount; id++) live.Add(ExpansionFrontierDriver.FirstCpuDeviceId + id);
        if (expansion?.X0 is { } x0) foreach (var id in x0.Dma.DeviceIds) live.Add(id);
        if (expansion?.X2 is { } x2) live.Add(x2.DeviceId);
        if (expansion?.X3 is { } x3) { live.Add(x3.ClockDevice); live.Add(x3.PumpDevice); }
        if (expansion?.X4 is { } x4) live.Add(x4.EngineDevice);
        if (tape is not null) live.Add(tape.DeviceId);
        if (floppy is not null)
            for (var d = floppy.FdcDeviceId; d <= floppy.FdcDeviceId + 5; d++) live.Add(d);
        var file = new HashSet<int>();
        foreach (var (device, _) in generations)
            if (!file.Add(device)) Fail(HcsError.StateInconsistent, "devices");
        if (!file.SetEquals(live)) Fail(HcsError.StateInconsistent, "devices");
    }

    private static string DeviceId(int id) => DevicePrefix + id.ToString(CultureInfo.InvariantCulture);
    private static int ParseDeviceId(string id)
    {
        if (!id.StartsWith(DevicePrefix))
            Fail(HcsError.InvalidFormat, "deviceId");
        if (!long.TryParse(id[DevicePrefix.Length..], NumberStyles.None,
                CultureInfo.InvariantCulture, out var v))
            Fail(HcsError.InvalidFormat, "deviceId");
        if (v is < 0 or > int.MaxValue)
            Fail(HcsError.InvalidFormat, "deviceId");
        return (int)v;
    }

    private static string Kind(int kind) => KindPrefix + kind.ToString(CultureInfo.InvariantCulture);
    private static int ParseKind(string kind)
    {
        if (!kind.StartsWith(KindPrefix))
            Fail(HcsError.InvalidFormat, "kind");
        if (!long.TryParse(kind[KindPrefix.Length..], NumberStyles.None,
                CultureInfo.InvariantCulture, out var v))
            Fail(HcsError.InvalidFormat, "kind");
        if (v < 0)
            Fail(HcsError.InvalidFormat, "kind");
        return (int)v;
    }

    private static string StateName(TapeState state) => state switch
    {
        TapeState.Unloaded => "unloaded",
        TapeState.Stopped => "stopped",
        TapeState.Playing => "playing",
        TapeState.AtEnd => "at-end",
        _ => throw new InvalidOperationException("tape state.")
    };

    private static TapeState TapeName(string name) => name switch
    {
        "unloaded" => TapeState.Unloaded,
        "stopped" => TapeState.Stopped,
        "playing" => TapeState.Playing,
        "at-end" => TapeState.AtEnd,
        _ => throw new InvalidOperationException("tape state.")
    };

    private static JsonArray ByteList(ReadOnlyCollection<byte> values)
    {
        var a = new JsonArray();
        foreach (var v in values) a.Add((uint)v);
        return a;
    }

    private static FdcPhase fdcPhaseFromString(string s) => s switch
    {
        "reset" => FdcPhase.Reset,
        "command" => FdcPhase.Command,
        "execution" => FdcPhase.Execution,
        "result" => FdcPhase.Result,
        _ => throw new ArgumentException($"Unknown FdcPhase '{s}'")
    };

    private static ReadOnlyCollection<byte> ByteList(JsonElement e, string key)
    {
        var list = new List<byte>();
        foreach (var v in e.GetProperty(key).EnumerateArray()) list.Add((byte)v.GetUInt32());
        return new ReadOnlyCollection<byte>(list);
    }

    private static IReadOnlyList<int> IntList(JsonElement e, string key)
    {
        var list = new List<int>();
        foreach (var v in e.GetProperty(key).EnumerateArray()) list.Add((int)v.GetUInt32());
        return list;
    }

    private static bool BlobEquals(byte[] blob, ReadOnlyMemory<byte> store)
        => blob.AsSpan().SequenceEqual(store.Span);

    private static uint AssetBlobId(JsonElement m, string assetId)
        => m.GetProperty("assets").EnumerateArray().First(a => Text(a, "id") == assetId).GetProperty("blobId").GetUInt32();

    // The "disk-*-base" provenance asset is optional: returns null when the drive was unmounted.
    private static byte[]? AssetBlob(JsonElement m, HcsDocument doc, string assetId)
    {
        var asset = m.GetProperty("assets").EnumerateArray().FirstOrDefault(a => Text(a, "id") == assetId);
        return asset.ValueKind == JsonValueKind.Undefined ? null : doc.CopyBlob(asset.GetProperty("blobId").GetUInt32());
    }

    // ---- value readers (the base schema already validated the shapes) ----

    private static long Counter(JsonElement e, string key)
        => long.Parse(e.GetProperty(key).GetString()!, NumberStyles.None, CultureInfo.InvariantCulture);

    private static long Counter(JsonElement parent, string objKey, string key)
        => Counter(parent.GetProperty(objKey), key);

    private static uint Num(JsonElement e, string key) => e.GetProperty(key).GetUInt32();
    private static uint? NumOrNull(JsonElement e, string key)
        => e.GetProperty(key).ValueKind == JsonValueKind.Null ? null : e.GetProperty(key).GetUInt32();
    private static string Text(JsonElement e, string key) => e.GetProperty(key).GetString()!;
    private static string Signed(JsonElement e, string key) => e.GetProperty(key).GetString()!;

    private static void Fail(HcsError error, string what) => HcsCodec.Fail(error, what);
}
