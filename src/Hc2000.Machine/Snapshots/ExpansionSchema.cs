using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hc2000.Machine.Expansion;
using Hc2000.Machine.Timing;
using Hc2000.Z80.Frontier;
using Cpu = Hc2000.Z80.Z80;

namespace Hc2000.Machine.Snapshots;

/// <summary>Profile-specific bounded metadata; never owns or copies a RAM asset.</summary>
internal static class ExpansionSchema
{
    internal const string ComponentId = "expansion-cpu-step1";
    internal const int MetadataBytes = 1024 * 1024, CpuBytes = 4096;
    internal static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };
    internal static bool Enabled(MachineProfile profile) =>
        profile.Name is "hc2000-v2-paged-expansion-v1" or "hc2000-v2-cpnet-v1" or "hc2000-v2-combined-workstation-v1" or "hc2000-v2-combined-workstation-v2";

    /// <summary>The full-X5 identity: the step-1 component keeps its 16 compute units, the
    /// separate expansion-x5 component carries the driver roster and the managed maps, and the
    /// main event binds to the main map's recorded fault.</summary>
    internal static bool X5(MachineProfile profile) => profile.Name is "hc2000-v2-paged-expansion-x5-v1"
        or "hc2000-v2-paged-expansion-x5-hdd-guest-v1" or "hc2000-v2-full-workstation-v1";

    internal static JsonElement Encode(ExpansionSnapshotState state)
    {
        var element = JsonSerializer.SerializeToElement(state, Options);
        if (Encoding.UTF8.GetByteCount(element.GetRawText()) > MetadataBytes)
            throw new HcsException(HcsError.LimitExceeded, "expansion metadata");
        return element;
    }

    internal static JsonElement ValidateCaptured(ExpansionSnapshotState state, CpuLogicalState cpu,
        DeviceScheduler scheduler, CpuTimeMapper mapper, ushort mainBank, ExpansionX5State? x5 = null)
    {
        var encoded = Encode(state);
        CheckCpuSize(encoded.GetProperty("Main"));
        foreach (var unit in encoded.GetProperty("Units").EnumerateArray()) CheckCpuSize(unit.GetProperty("Cpu"));
        static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        var manifest = JsonSerializer.SerializeToElement(new
        {
            cpu = cpu.ToJson(true),
            components = new[] { new { id = "memory", state = new { paged = new { bank = mainBank } } } },
            clock = new
            {
                now = Number(scheduler.Now), dividerNumerator = Number(mapper.Numerator),
                dividerDenominator = Number(mapper.Denominator), mapperWhole = Number(mapper.WholeTicks),
                mapperRemainder = Number(mapper.Remainder),
                mapperRemainderDenominator = Number(mapper.RemainderDenominator),
                lastCpuTStates = Number(mapper.CpuTStates),
                deviceGenerations = scheduler.DeviceGenerations.Select(g => new { id = "device-" + g.DeviceId })
            },
            events = scheduler.PendingEvents.Select(e => new
            {
                deviceId = "device-" + e.DeviceId, dueTick = Number(e.DueTick), phase = (uint)e.Phase,
                kind = "kind-" + e.Kind, payload = new { value = Number(e.Payload) },
                dueLogical = e.DueTime is { } due ? new { whole = Number(due.WholeTicks), numerator = Number(due.FractionNumerator), denominator = Number(due.FractionDenominator) } : null
            })
        });
        if (x5 is not null)
        {
            // The capture path admits the full-X5 file the same way the load path does: the step-1
            // component with the X5 context (main fault, driver events) plus the cross-component
            // admission, before Save writes anything.
            var decoded = ExpansionX5Schema.Read(ExpansionX5Schema.Encode(x5));
            Validate(state, manifest, decoded, true);
            ExpansionX5Schema.ValidateHcs(state, decoded, manifest);
        }
        else Validate(state, manifest);
        return encoded;
    }

    internal static ExpansionSnapshotState Read(JsonElement manifest, bool x5Profile = false, ExpansionX5State? x5 = null)
    {
        try
        {
            var component = manifest.GetProperty("components").EnumerateArray().Single(c => c.GetProperty("id").GetString() == ComponentId);
            var element = component.GetProperty("state");
            if (Encoding.UTF8.GetByteCount(element.GetRawText()) > MetadataBytes) throw Invalid();
            var units = element.GetProperty("Units");
            // The step-1 component keeps its 16 compute units; driver units 17..16+n belong to the
            // separate expansion-x5 component (design x5-slice-design v2).
            if (units.GetArrayLength() != ExpansionArena.ComputeCount) throw Invalid();
            CheckCpuSize(element.GetProperty("Main"));
            foreach (var unit in units.EnumerateArray())
            {
                CheckCpuSize(unit.GetProperty("Cpu"));
                foreach (var field in new[] { "Inbox", "Outbox", "Staging" })
                    CheckEncodedSize(unit.GetProperty("Mailbox").GetProperty(field), ExpansionMailbox.FrameBytes);
            }
            CheckEncodedSize(element.GetProperty("Root").GetProperty("Staging"), ExpansionController.HeaderBytes + ExpansionController.MaxRequestPayload);
            var response = element.GetProperty("Root").GetProperty("Response");
            if (response.ValueKind != JsonValueKind.Null) CheckEncodedSize(response, ExpansionController.HeaderBytes + ExpansionController.MaxObservePayload);
            var s = element.Deserialize<ExpansionSnapshotState>(Options) ?? throw Invalid();
            // Reject missing/redundant contradictory register aliases, duplicate or ignored fields,
            // and any noncanonical dead state rather than silently repairing an input document.
            if (!JsonElement.DeepEquals(element, Encode(s))) throw Invalid();
            Validate(s, manifest, x5, x5Profile);
            return s;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or
            KeyNotFoundException or NullReferenceException or OverflowException or FormatException)
        { throw new HcsException(HcsError.StateInconsistent, "expansion metadata: " + e.GetType().Name); }
    }

    private static void CheckCpuSize(JsonElement cpu)
    {
        if (Encoding.UTF8.GetByteCount(cpu.GetRawText()) > CpuBytes) throw Invalid();
    }
    private static void CheckEncodedSize(JsonElement value, int bytes)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 4 * ((bytes + 2) / 3)) throw Invalid();
    }
    private static ArgumentException Invalid() => new("Invalid expansion state.");
    private static long Counter(JsonElement e, string name) => long.Parse(e.GetProperty(name).GetString()!, NumberStyles.None, CultureInfo.InvariantCulture);

    internal static CpuLogicalState Logical(FrontierState s)
    {
        var r = s.Registers;
        return new(r.AF, r.BC, r.DE, r.HL, r.IX, r.IY, r.SP, r.PC, r.AFShadow, r.BCShadow,
            r.DEShadow, r.HLShadow, r.WZ, r.I, r.R, r.Q, r.IFF1, r.IFF2, s.Halted, s.IntLine,
            s.NmiPending, s.LdAirPending, s.InterruptBug, r.IM, (byte)s.EiDelay, s.TStates);
    }

    private static void Validate(ExpansionSnapshotState s, JsonElement manifest, ExpansionX5State? x5 = null, bool x5Profile = false)
    {
        if (s.Version != 1 || s.MainKind != "main" || s.Units is not { Length: ExpansionArena.ComputeCount }) throw Invalid();
        Cpu.ValidateFrontierState(s.Main);
        if (s.Main.Pending is null || Logical(s.Main) != CpuLogicalState.FromJson(manifest.GetProperty("cpu"), true)) throw Invalid();
        var clock = manifest.GetProperty("clock"); var now = Counter(clock, "now");
        var pnum = Counter(clock, "dividerNumerator"); var den = Counter(clock, "dividerDenominator");
        var mapperWhole = Counter(clock, "mapperWhole"); var mapperRemainder = Counter(clock, "mapperRemainder");
        var mapperDen = clock.TryGetProperty("mapperRemainderDenominator", out var mapperDenElement)
            ? Counter(clock, "mapperRemainderDenominator") : den;
        var lastCpuT = Counter(clock, "lastCpuTStates");
        // The main frontier runs at the admitted rate, not a hardcoded 5/1. The clock section is the
        // anchor: exact time at the committed counter. A legacy document is the zero epoch (5/1,
        // integer), so the checks below reduce to the original integer identity for it.
        var mapper = new CpuTimeMapper(pnum, den);
        try { mapper.Restore(lastCpuT, mapperWhole, mapperRemainder, mapperDen); }
        catch (ArgumentOutOfRangeException) { throw Invalid(); }
        if (lastCpuT != s.Main.TStates || mapper.ExactTime().WholeTicks > now) throw Invalid();
        var mainPendingDue = mapper.ExactTimeAt(s.Main.Pending.Value.AtCpuTState);
        if (mainPendingDue <= mapper.ExactTime()) throw Invalid();
        var memory = manifest.GetProperty("components").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "memory").GetProperty("state");
        // A session that has not engaged its paged window saves without the paged latches (the
        // tape reservation is disengaged); the restore path treats the absent block as bank 0, so
        // the admission cross-check must read it the same way instead of refusing the document.
        var mainBank = memory.TryGetProperty("paged", out var paged) && paged.ValueKind == JsonValueKind.Object
            ? paged.GetProperty("bank").GetUInt16() : (ushort)0;
        var pages = new HashSet<ushort>();
        var live = s.Units.Where(u => u.ExchangeOwner != 0).ToArray();
        if (live.Length > 1) throw Invalid();
        var expected = new Dictionary<int, long>();
        if (x5?.Maps[0].Fault is null)
            expected.Add(ExpansionFrontierDriver.FirstCpuDeviceId, mainPendingDue.WholeTicks);
        for (var index = 0; index < s.Units.Length; index++)
        {
            var u = s.Units[index]; var grant = u.Grant;
            if (u.Kind is not ("compute" or "driver") || grant.UnitId != index + 1 || grant.ResourceEpoch == 0 || grant.MapEpoch == 0 ||
                u.ExecutionEpoch == 0 || !Enum.IsDefined(u.Lifecycle) || !Enum.IsDefined(u.SavedLifecycle) ||
                u.SavedLifecycle is ExpansionLifecycle.Paused or ExpansionLifecycle.Faulted ||
                !Enum.IsDefined(u.RequestedMailboxEffect) || u.HandoverHalt ||
                u.ExchangeOwner == 0 && u.ExchangePreviousBank != 0 ||
                u.ExchangeOwner != 0 && (u.ExchangeOwner != grant.UnitId ||
                    u.Lifecycle != ExpansionLifecycle.ServiceIdle || u.Cpu.Halted ||
                    u.Cpu.Registers.PC >= 0xc000 || u.Cpu.Registers.SP > 0xbfff || grant.Pages.Page3 != mainBank) ||
                u.CpuAnchor < 0 || u.MasterAnchor < 0 || u.Remainder != 0 ||
                u.CpuAnchor > u.Cpu.TStates || u.LastMasterTick > now ||
                checked(u.MasterAnchor + checked((u.Cpu.TStates - u.CpuAnchor) * 5)) != u.LastMasterTick) throw Invalid();
            for (var page = 0; page < 4; page++)
                if (grant.Pages[page] < ExpansionArena.FirstComputePage ||
                    grant.Pages[page] == mainBank && !(page == 3 && u.ExchangeOwner == grant.UnitId) ||
                    !pages.Add(grant.Pages[page])) throw Invalid();
            Cpu.ValidateFrontierState(u.Cpu); ExpansionMailbox.ValidateState(u.Mailbox);
            if (u.RequestedMailboxEffect != ExpansionMailboxEffect.None &&
                (u.Cpu.Pending is null || u.Lifecycle is not (ExpansionLifecycle.Running or ExpansionLifecycle.Paused))) throw Invalid();
            if (u.Lifecycle is ExpansionLifecycle.ResetStopped or ExpansionLifecycle.WaitingMailbox or ExpansionLifecycle.ServiceIdle && u.Cpu.Pending is not null) throw Invalid();
            if (u.Lifecycle == ExpansionLifecycle.ResetStopped && (u.Cpu.TStates != 0 || u.Cpu.Halted) ||
                u.Lifecycle == ExpansionLifecycle.WaitingMailbox && u.Mailbox.HasInbox) throw Invalid();
            if (u.Lifecycle is ExpansionLifecycle.Running or ExpansionLifecycle.Halted)
            {
                if (u.Cpu.Pending is null) throw Invalid();
                expected.Add(ExpansionFrontierDriver.FirstCpuDeviceId + index + 1,
                    checked(u.MasterAnchor + checked((u.Cpu.Pending.Value.AtCpuTState - u.CpuAnchor) * 5)));
            }
        }
        if (live.Length == 1 && pages.Contains(live[0].ExchangePreviousBank)) throw Invalid();
        ValidateRoot(s.Root, s.Units, x5);
        var generationIds = clock.GetProperty("deviceGenerations").EnumerateArray().Select(g => g.GetProperty("id").GetString()).ToHashSet();
        for (var id = 1000; id <= ExpansionFrontierDriver.FirstCpuDeviceId + ExpansionArena.ComputeCount; id++) if (!generationIds.Contains("device-" + id)) throw Invalid();
        foreach (var e in manifest.GetProperty("events").EnumerateArray())
        {
            var id = e.GetProperty("deviceId").GetString()!;
            var cpu = int.TryParse(id.AsSpan("device-".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= ExpansionFrontierDriver.FirstCpuDeviceId and <= ExpansionFrontierDriver.FirstCpuDeviceId + ExpansionArena.ComputeCount;
            if (!cpu)
            {
                if (x5Profile && x5 is not null && n >= ExpansionFrontierDriver.FirstCpuDeviceId + ExpansionArena.DriverFirst &&
                    n < ExpansionFrontierDriver.FirstCpuDeviceId + ExpansionArena.DriverFirst + x5.Drivers.Length) continue;
                if (e.GetProperty("phase").GetUInt32() == 5) throw Invalid();
                continue;
            }
            // The main frontier's exact due may sit mid-tick at a fractional rate, so its floor can
            // equal the boundary's floor: compare the exact dueLogical to the boundary. Side units
            // keep their faithful integer tick, so their floor must be strictly later.
            if (!expected.Remove(n, out var tick) || tick != Counter(e, "dueTick") ||
                e.GetProperty("phase").GetUInt32() != 5 || e.GetProperty("kind").GetString() != "kind-0" ||
                Counter(e.GetProperty("payload"), "value") != 0) throw Invalid();
            // The main frontier's exact due may sit mid-tick at a fractional rate, so its floor can
            // equal the boundary's floor: compare the exact dueLogical to the boundary. Side units
            // keep their faithful integer tick, so their floor must be strictly later.
            LogicalTime? exactDue = null;
            if (n == ExpansionFrontierDriver.FirstCpuDeviceId && e.TryGetProperty("dueLogical", out var dueEl) && dueEl.ValueKind == JsonValueKind.Object)
                exactDue = new LogicalTime(Counter(dueEl, "whole"), Counter(dueEl, "numerator"), Counter(dueEl, "denominator"));
            var dueAdmitted = exactDue is { } due
                ? due.WholeTicks == tick && due > LogicalTime.Tick(now) && due == mainPendingDue
                : tick > now;
            if (!dueAdmitted) throw Invalid();
        }
        if (expected.Count != 0) throw Invalid();
    }

    private static void ValidateRoot(ExpansionRootState r, ExpansionUnitState[] units, ExpansionX5State? x5)
    {
        if (r.Staging is null || r.Staging.Length > ExpansionController.HeaderBytes + ExpansionController.MaxRequestPayload ||
            r.ResponseCursor < 0 || !Enum.IsDefined(r.LastResult) || r.PendingId > units.Length + (x5?.Drivers.Length ?? 0) ||
            r.Overflow && r.Staging.Length != ExpansionController.HeaderBytes + ExpansionController.MaxRequestPayload ||
            r.Response is null && r.ResponseCursor != 0 || r.Collecting && (r.Response is not null || r.PendingId != 0)) throw Invalid();
        if (r.Response is { } response)
        {
            if (r.PendingId != 0 || response.Length is < 40 or > 69676 || r.ResponseCursor > response.Length ||
                response[0] != 1 || response[1] > 8 || response[2] > 6 || response[3] != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(4)) != response.Length - 40) throw Invalid();
        }
        if (r.PendingId == 0) { if (r.PendingOperation != 0) throw Invalid(); return; }
        var u = r.PendingId <= units.Length ? units[r.PendingId - 1] : x5!.Drivers[r.PendingId - 1 - units.Length];
        if (r.Latched != r.PendingId || r.Collecting || r.Response is not null || r.Overflow ||
            r.PendingOperation is not (2 or 6) || r.Staging.Length != 40 || r.Staging[0] != 1 ||
            r.Staging[1] != r.PendingOperation || r.Staging[2] != 0 || r.Staging[3] != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(r.Staging.AsSpan(4)) != 0 ||
            BinaryPrimitives.ReadUInt64LittleEndian(r.Staging.AsSpan(8)) == 0 ||
            BinaryPrimitives.ReadUInt64LittleEndian(r.Staging.AsSpan(16)) != u.Grant.ResourceEpoch ||
            BinaryPrimitives.ReadUInt64LittleEndian(r.Staging.AsSpan(24)) != u.ExecutionEpoch ||
            BinaryPrimitives.ReadUInt64LittleEndian(r.Staging.AsSpan(32)) != u.Grant.MapEpoch ||
            u.Lifecycle is not (ExpansionLifecycle.Running or ExpansionLifecycle.Halted)) throw Invalid();
    }
}
