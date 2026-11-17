using System.Globalization;
using System.Text;
using System.Text.Json;
using Hc2000.Machine.Expansion;
using Cpu = Hc2000.Z80.Z80;

namespace Hc2000.Machine.Snapshots;

internal static class ExpansionX5Schema
{
    internal const string ComponentId = "expansion-x5";
    internal const int MetadataBytes = 1024 * 1024;

    internal static JsonElement Encode(ExpansionX5State state)
    {
        var element = JsonSerializer.SerializeToElement(state, ExpansionSchema.Options);
        if (Encoding.UTF8.GetByteCount(element.GetRawText()) > MetadataBytes)
            throw new HcsException(HcsError.LimitExceeded, "expansion-x5 metadata");
        return element;
    }

    // Structural validation alone is not an HCS admission: the caller must also validate the
    // compute grants, scheduler events and saved main latch against this component before mutation.
    internal static ExpansionX5State Read(JsonElement element)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(element.GetRawText()) > MetadataBytes) throw Invalid();
            var drivers = element.GetProperty("Drivers");
            if (drivers.GetArrayLength() > ExpansionArena.DriverCount) throw Invalid();
            if (element.GetProperty("Maps").GetArrayLength() != drivers.GetArrayLength() + 1) throw Invalid();
            CheckBytes(element.GetProperty("DeviceOwners"), 256);
            foreach (var driver in drivers.EnumerateArray())
            {
                if (Encoding.UTF8.GetByteCount(driver.GetProperty("Cpu").GetRawText()) > ExpansionSchema.CpuBytes)
                    throw Invalid();
                foreach (var field in new[] { "Inbox", "Outbox", "Staging" })
                    CheckBytes(driver.GetProperty("Mailbox").GetProperty(field), ExpansionMailbox.FrameBytes);
            }
            var state = element.Deserialize<ExpansionX5State>(ExpansionSchema.Options) ?? throw Invalid();
            if (!JsonElement.DeepEquals(element, Encode(state))) throw Invalid();
            ValidateShape(state);
            return state;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or
            KeyNotFoundException or NullReferenceException or OverflowException or FormatException)
        {
            throw new HcsException(HcsError.StateInconsistent, "expansion-x5 metadata: " + error.GetType().Name);
        }
    }

    private static void ValidateShape(ExpansionX5State state)
    {
        if (state.Version != 3 || state.Drivers is null || state.Drivers.Length > ExpansionArena.DriverCount ||
            state.Maps is null || state.Maps.Length != state.Drivers.Length + 1 ||
            state.DeviceOwners is not { Length: 256 } || state.DeviceOwners[0] != 0 || state.Cards is null || state.Cards.Length > 255) throw Invalid();
        var previousCard = -1;
        foreach (var card in state.Cards)
        {
            if (card is null || card.Id == 0 || card.Id <= previousCard || card.Id >= 256 || string.IsNullOrWhiteSpace(card.Kind) ||
                card.Kind.Length > 64 || card.Version <= 0 || card.Ports is not { Length: > 0 and <= 32 }) throw Invalid();
            previousCard = card.Id;
            foreach (var port in card.Ports)
                if (port is null || (port.Match & ~port.Mask) != 0 ||
                    port.Access is < Io.PortAccess.Read or > Io.PortAccess.ReadWrite || !Enum.IsDefined(port.Authority)) throw Invalid();
        }
        if (!ExpansionMmu.ValidDetachedState(0, state.Maps[0])) throw Invalid();
        var pages = new HashSet<ushort>();
        for (var index = 0; index < state.Drivers.Length; index++)
        {
            var unit = state.Drivers[index];
            var id = (ushort)(ExpansionArena.DriverFirst + index);
            if (unit is null || unit.Kind != "driver" || unit.Grant.UnitId != id ||
                unit.Grant.ResourceEpoch == 0 || unit.Grant.MapEpoch == 0 || unit.ExecutionEpoch == 0 ||
                !Enum.IsDefined(unit.Lifecycle) || !Enum.IsDefined(unit.SavedLifecycle) ||
                unit.SavedLifecycle is ExpansionLifecycle.Paused or ExpansionLifecycle.Faulted ||
                !Enum.IsDefined(unit.RequestedMailboxEffect) || unit.HandoverHalt ||
                unit.ExchangeOwner == 0 && unit.ExchangePreviousBank != 0 ||
                unit.ExchangeOwner != 0 && unit.ExchangeOwner != id ||
                unit.CpuAnchor < 0 || unit.MasterAnchor < 0 || unit.LastMasterTick < 0 || unit.Remainder != 0 ||
                unit.CpuAnchor > unit.Cpu.TStates ||
                checked(unit.MasterAnchor + checked((unit.Cpu.TStates - unit.CpuAnchor) * 5)) != unit.LastMasterTick ||
                !ExpansionMmu.ValidDetachedState(id, state.Maps[index + 1])) throw Invalid();
            for (var page = 0; page < 4; page++)
                if (unit.Grant.Pages[page] < ExpansionArena.FirstDriverPage || !pages.Add(unit.Grant.Pages[page])) throw Invalid();
            Cpu.ValidateFrontierState(unit.Cpu);
            ExpansionMailbox.ValidateState(unit.Mailbox);
            if (unit.RequestedMailboxEffect != ExpansionMailboxEffect.None &&
                (unit.Cpu.Pending is null || unit.Lifecycle is not (ExpansionLifecycle.Running or ExpansionLifecycle.Paused))) throw Invalid();
            if (unit.Lifecycle is ExpansionLifecycle.ResetStopped or ExpansionLifecycle.WaitingMailbox or ExpansionLifecycle.ServiceIdle && unit.Cpu.Pending is not null) throw Invalid();
            if (unit.Lifecycle == ExpansionLifecycle.ResetStopped && (unit.Cpu.TStates != 0 || unit.Cpu.Halted) ||
                unit.Lifecycle == ExpansionLifecycle.WaitingMailbox && unit.Mailbox.HasInbox ||
                unit.Lifecycle is ExpansionLifecycle.Running or ExpansionLifecycle.Halted && unit.Cpu.Pending is null) throw Invalid();
        }
        foreach (var owner in state.DeviceOwners)
            if (owner != 0 && (owner < ExpansionArena.DriverFirst || owner >= ExpansionArena.DriverFirst + state.Drivers.Length || state.OwnerEpoch == 0)) throw Invalid();
    }

    // Call after the step-1 component has independently passed its own validation. Saved active
    // grants, not their original page numbers, determine ownership; only uncomposed driver slots
    // retain their initial reservations. Scheduler and root checks are separate admission steps.
    internal static void ValidateOwnership(ExpansionSnapshotState compute, ExpansionX5State x5, ushort mainBank)
    {
        try
        {
            ValidateShape(x5);
            if (compute.Units is not { Length: ExpansionArena.ComputeCount }) throw Invalid();
            var owners = new HashSet<ushort>();
            ExpansionUnitState? exchange = null;
            foreach (var unit in compute.Units.Concat(x5.Drivers))
            {
                if (unit.ExchangeOwner != 0)
                {
                    if (exchange is not null || unit.ExchangeOwner != unit.Grant.UnitId ||
                        unit.Lifecycle != ExpansionLifecycle.ServiceIdle || unit.Cpu.Halted ||
                        unit.Cpu.Registers.PC >= 0xc000 || unit.Cpu.Registers.SP > 0xbfff ||
                        unit.Grant.Pages.Page3 != mainBank) throw Invalid();
                    exchange = unit;
                }
                for (var page = 0; page < 4; page++)
                    if (!owners.Add(unit.Grant.Pages[page]) ||
                        unit.Grant.Pages[page] == mainBank && !(page == 3 && unit.ExchangeOwner == unit.Grant.UnitId)) throw Invalid();
            }
            if (x5.Drivers.Length != 0)
                for (var index = x5.Drivers.Length; index < ExpansionArena.DriverCount; index++)
                    for (var page = 0; page < 4; page++)
                    {
                        var reserved = (ushort)(ExpansionArena.FirstDriverPage + index * 4 + page);
                        if (!owners.Add(reserved) || reserved == mainBank) throw Invalid();
                    }
            if (exchange is not null && owners.Contains(exchange.ExchangePreviousBank)) throw Invalid();
            for (var index = 0; index < x5.Drivers.Length; index++)
                if ((x5.Drivers[index].Lifecycle == ExpansionLifecycle.Faulted) != (x5.Maps[index + 1].Fault is not null)) throw Invalid();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NullReferenceException or OverflowException)
        {
            throw new HcsException(HcsError.StateInconsistent, "expansion-x5 ownership: " + error.GetType().Name);
        }
    }

    // The atomic HCS admission for a full-X5 file: cross-component ownership against the step-1
    // compute grants and the saved main latch, plus the driver scheduler timing. The step-1
    // component (validated with the X5 context) separately binds the main event to the main map's
    // recorded fault and the root's pending request. Both sides call this before any mutation.
    internal static void ValidateHcs(ExpansionSnapshotState step1, ExpansionX5State x5, JsonElement manifest)
    {
        try
        {
            var memory = manifest.GetProperty("components").EnumerateArray()
                .Single(c => c.GetProperty("id").GetString() == "memory").GetProperty("state");
            var mainBank = memory.GetProperty("paged").GetProperty("bank").GetUInt16();
            ValidateOwnership(step1, x5, mainBank);
            ValidateDriverTiming(x5, manifest);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or
            KeyNotFoundException or NullReferenceException or OverflowException or FormatException)
        {
            throw new HcsException(HcsError.StateInconsistent, "expansion-x5 admission: " + error.GetType().Name);
        }
    }

    // Driver events are checked separately from the step-1 main/compute events. The caller must
    // also validate those events, ownership and the controller root before admitting an HCS file.
    internal static void ValidateDriverTiming(ExpansionX5State x5, JsonElement manifest)
    {
        try
        {
            ValidateShape(x5);
            var clock = manifest.GetProperty("clock");
            var now = Counter(clock, "now");
            var generations = clock.GetProperty("deviceGenerations").EnumerateArray()
                .Select(g => g.GetProperty("id").GetString()).ToHashSet();
            var expected = new Dictionary<int, long>();
            for (var index = 0; index < x5.Drivers.Length; index++)
            {
                var id = ExpansionFrontierDriver.FirstCpuDeviceId + ExpansionArena.DriverFirst + index;
                var unit = x5.Drivers[index];
                if (!generations.Contains("device-" + id) || unit.LastMasterTick > now) throw Invalid();
                if (unit.Lifecycle is ExpansionLifecycle.Running or ExpansionLifecycle.Halted)
                    expected.Add(id, checked(unit.MasterAnchor + checked((unit.Cpu.Pending!.Value.AtCpuTState - unit.CpuAnchor) * 5)));
            }
            foreach (var e in manifest.GetProperty("events").EnumerateArray())
            {
                var text = e.GetProperty("deviceId").GetString();
                if (text is null || !text.StartsWith("device-", StringComparison.Ordinal) ||
                    !int.TryParse(text.AsSpan("device-".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
                    id < ExpansionFrontierDriver.FirstCpuDeviceId + ExpansionArena.DriverFirst ||
                    id > ExpansionFrontierDriver.FirstCpuDeviceId + ExpansionArena.DriverLast) continue;
                if (!expected.Remove(id, out var tick) || tick <= now || tick != Counter(e, "dueTick") ||
                    e.GetProperty("phase").GetUInt32() != 5 || e.GetProperty("kind").GetString() != "kind-0" ||
                    Counter(e.GetProperty("payload"), "value") != 0) throw Invalid();
            }
            if (expected.Count != 0) throw Invalid();
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or
            KeyNotFoundException or NullReferenceException or OverflowException or FormatException)
        {
            throw new HcsException(HcsError.StateInconsistent, "expansion-x5 timing: " + error.GetType().Name);
        }
    }

    private static long Counter(JsonElement value, string key) =>
        long.Parse(value.GetProperty(key).GetString()!, NumberStyles.None, CultureInfo.InvariantCulture);

    private static void CheckBytes(JsonElement value, int maxBytes)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 4 * ((maxBytes + 2) / 3)) throw Invalid();
    }

    private static ArgumentException Invalid() => new("Invalid expansion X5 state.");
}
