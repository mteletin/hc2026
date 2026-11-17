using System.Buffers.Binary;
using System.Text.Json;
using Hc2000.Machine.Io;

namespace Hc2000.Machine.Expansion;

/// <summary>Single owner-side request/response admission. Guest selection is never authority.</summary>
public sealed partial class ExpansionController : IOriginPortDevice, IPortOriginPolicy
{
    public const int HeaderBytes = 40, MaxRequestPayload = 65544, MaxObservePayload = 69636;
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly ExpansionArena arena;
    private readonly ExpansionComputeUnit[] units;
    /// <summary>Root + composed units: 16 compute + the driver count this composition enabled (0..8).</summary>
    public int UnitCount => units.Length;
    // Full-X5 root state (host-composed only): the managed map and the static v1 device owner table.
    public ExpansionMmu? Mmu { get; set; }
    internal ExpansionServiceDispatcher? Services { get; set; }
    private readonly byte[] deviceOwners = new byte[256];
    private ulong ownerEpoch;
    private ulong portIncarnation;
    internal ulong OwnershipEpoch => ownerEpoch;
    internal ulong PortIncarnation => portIncarnation;
    private readonly byte[] staging = new byte[HeaderBytes + MaxRequestPayload];
    private readonly IReadOnlyList<PortMapping> mappings;
    private readonly JsonSerializerOptions jsonOptions = new() { IncludeFields = true };
    private byte[]? response;
    private int stagingCount, responseCursor;
    private bool collecting, overflow;
    private ushort selected, latched, pendingId;
    private byte pendingOperation;
    private ExpansionResult lastResult;

    public ExpansionController(ExpansionArena arena, int driverCount = 0)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (driverCount is < 0 or > ExpansionArena.DriverCount) throw new ArgumentOutOfRangeException(nameof(driverCount),
            "A composition enables at most the arena's driver roster.");
        this.arena = arena;
        if (driverCount != 0) arena.EnableDriverRoster();
        units = new ExpansionComputeUnit[ExpansionArena.ComputeCount + driverCount];
        for (ushort id = 1; id <= units.Length; id++)
            units[id - 1] = new(id, arena.Acquire(id), id <= ExpansionArena.ComputeCount ? "compute" : "driver");
        mappings = Array.AsReadOnly(new PortMapping[]
        {
            new(0x7bfd, 0xffff, this, PortAccess.ReadWrite), new(0x7afd, 0xffff, this, PortAccess.ReadWrite),
            new(0x79fd, 0xffff, this, PortAccess.Write), new(0x78fd, 0xffff, this, PortAccess.ReadWrite),
            new(0x77fd, 0xffff, this, PortAccess.Read), new(0x76fd, 0xffff, this, PortAccess.Read),
            new(0x75fd, 0xffff, this, PortAccess.ReadWrite), new(0x74fd, 0xffff, this, PortAccess.ReadWrite)
        });
    }
    public CpuPortOrigin MainOrigin => new(0, 1, CpuPortRole.Main);
    public ExpansionComputeUnit GetUnit(ushort id)
    {
        VerifyOwner();
        if (id == 0 || id > units.Length) throw new ArgumentOutOfRangeException(nameof(id));
        return units[id - 1];
    }
    public IReadOnlyList<PortMapping> Mappings()
    {
        VerifyOwner();
        cardRosterSealed = true;
        return Array.AsReadOnly(mappings.Concat(ownedCards.OrderBy(card => card.Key)
            .SelectMany(card => card.Value.Mappings)).ToArray());
    }
    public bool Allows(in CpuPortOrigin origin, ushort port, PortAccess access)
    {
        VerifyOwner();
        if (access is not (PortAccess.Read or PortAccess.Write)) return false;
        var card = CardForPort(port, access);
        if (origin == MainOrigin) return port is not (0x74fd or 0x75fd) &&
            (card is null || card.Value.Authority == ExpansionPortAuthority.MainOnly || deviceOwners[card.Value.Card.Descriptor.Id] == 0);
        var validUnit = origin.Role is CpuPortRole.Compute or CpuPortRole.Driver &&
            origin.UnitId > 0 && origin.UnitId <= units.Length &&
            (origin.Role == CpuPortRole.Compute ? origin.UnitId <= ExpansionArena.ComputeCount : origin.UnitId >= ExpansionArena.DriverFirst) &&
            units[origin.UnitId - 1].ExecutionEpoch == origin.Epoch;
        if (!validUnit) return false;
        if (port is 0x74fd or 0x75fd) return true;
        return origin.Role == CpuPortRole.Driver && origin.UnitId >= ExpansionArena.DriverFirst &&
            origin.OwnershipEpoch == ownerEpoch && origin.Incarnation == portIncarnation &&
            card is { } owned && owned.Authority == ExpansionPortAuthority.Exclusive &&
            deviceOwners[owned.Card.Descriptor.Id] == origin.UnitId;
    }
    // Never infer an origin from a direct host call.
    public byte Read(ushort port, long masterTick) => 0xff;
    public void Write(ushort port, byte value, long masterTick) { }
    public byte Read(in CpuPortOrigin origin, ushort port, long masterTick)
    {
        if (!Allows(in origin, port, PortAccess.Read)) return 0xff;
        if (origin.Role is CpuPortRole.Compute or CpuPortRole.Driver) return units[origin.UnitId - 1].ReadLocal(port);
        return port switch
        {
            0x7bfd => (byte)selected, 0x7afd => (byte)(selected >> 8), 0x76fd => 1,
            0x78fd => response is not null && responseCursor < response.Length ? response[responseCursor++] : (byte)0xff,
            0x77fd => (byte)((collecting ? 1 : 0) | (response is null ? 0 : 2) | (pendingId == 0 ? 0 : 4) |
                ((selected > 0 && selected <= units.Length && units[selected - 1].Lifecycle == ExpansionLifecycle.Faulted ? 8 : 0)) |
                ((byte)lastResult << 4)),
            _ => 0xff
        };
    }
    public void Write(in CpuPortOrigin origin, ushort port, byte value, long masterTick)
    {
        if (!Allows(in origin, port, PortAccess.Write)) return;
        if (origin.Role is CpuPortRole.Compute or CpuPortRole.Driver) { units[origin.UnitId - 1].WriteLocal(port, value); return; }
        switch (port)
        {
            case 0x7bfd: selected = (ushort)((selected & 0xff00) | value); break;
            case 0x7afd: selected = (ushort)((selected & 0xff) | (value << 8)); break;
            case 0x78fd:
                if (!collecting) { lastResult = ExpansionResult.InvalidFrame; break; }
                if (stagingCount == staging.Length) { overflow = true; lastResult = ExpansionResult.InvalidFrame; break; }
                staging[stagingCount++] = value;
                break;
            case 0x79fd: Control(value, masterTick); break;
        }
    }
    private void Control(byte verb, long tick)
    {
        switch (verb)
        {
            case 1:
                if (response is not null || pendingId != 0 || collecting) { lastResult = ExpansionResult.Busy; break; }
                Array.Clear(staging); stagingCount = 0; overflow = false; collecting = true; latched = selected;
                lastResult = ExpansionResult.Accepted;
                break;
            case 2:
                if (response is not null || pendingId != 0) { lastResult = ExpansionResult.Busy; break; }
                if (!collecting) { lastResult = ExpansionResult.InvalidFrame; break; }
                collecting = false; Submit(tick);
                break;
            case 3:
                if (collecting) { Array.Clear(staging); collecting = overflow = false; stagingCount = 0; }
                break;
            case 4: responseCursor = 0; break;
            case 5: response = null; responseCursor = 0; break;
            default: lastResult = ExpansionResult.InvalidFrame; break;
        }
    }
    private void Submit(long tick)
    {
        if (overflow || stagingCount < HeaderBytes || staging[0] != 1 || staging[2] != 0 || staging[3] != 0 ||
            U32(4) > MaxRequestPayload || U32(4) != stagingCount - HeaderBytes || U64(8) == 0)
        { Emit(ExpansionResult.InvalidFrame); return; }
        if (latched > units.Length) { Emit(ExpansionResult.NotFound); return; }
        var op = staging[1];
        var allZero = U64(16) == 0 && U64(24) == 0 && U64(32) == 0;
        if ((!allZero || op != 1) && (U64(16) == 0 || U64(24) == 0 || U64(32) == 0))
        { Emit(ExpansionResult.InvalidFrame); return; }
        var grant = latched == 0 ? default : arena.GetGrant(latched);
        var unit = latched == 0 ? null : units[latched - 1];
        if (!allZero && (U64(16) != (latched == 0 ? 1ul : grant.ResourceEpoch) ||
            U64(24) != (unit?.ExecutionEpoch ?? 1) || U64(32) != (latched == 0 ? 1ul : grant.MapEpoch)))
        { Emit(ExpansionResult.StaleEpoch); return; }
        if (latched == 0 && op is not (1 or 14 or 16)) { Emit(ExpansionResult.DeniedOrigin); return; }
        var data = staging.AsSpan(HeaderBytes, stagingCount - HeaderBytes);
        if (!ValidPayload(op, data)) { Emit(ExpansionResult.InvalidFrame); return; }
        if (op == 1) { Emit(ExpansionResult.Accepted, Query()); return; }
        if (op is 14 or 16) { MapOp(op, data); return; }
        if (op == 15) { Handoff(data); return; }
        if (unit is null) throw new InvalidOperationException("Missing admitted compute.");
        if (tick < unit.LastMasterTick) { Emit(ExpansionResult.InvalidState); return; }
        if (op is 2 or 3 or 4 or 10 && unit.ExecutionEpoch == ulong.MaxValue)
        { Emit(ExpansionResult.ResourceLimit); return; }
        if (op is 2 or 10 && !OwnedCardsQuiescent(latched))
        { Emit(ExpansionResult.Busy); return; }
        switch (op)
        {
            case 2: // RESET: never wait for instruction retirement.
            case 6: // PAUSE acknowledges one cycle-safe frontier, including a partial instruction.
                if (op == 6 && arena.OwnsExchange(latched))
                { Emit(ExpansionResult.InvalidState); return; }
                if (unit.Runnable)
                {
                    pendingId = latched; pendingOperation = op;
                    lastResult = ExpansionResult.Accepted; // No response until owner acknowledgment.
                    return;
                }
                if (op == 2) { arena.RevokeExchange(latched); unit.Reset(tick); Mmu?.ClearFault(latched); }
                else if (unit.Lifecycle is ExpansionLifecycle.Paused or ExpansionLifecycle.Faulted)
                { Emit(ExpansionResult.InvalidState); return; }
                else unit.Pause(tick);
                break;
            case 3:
                if (unit.Lifecycle != ExpansionLifecycle.ResetStopped) { Emit(ExpansionResult.InvalidState); return; }
                arena.RevokeExchange(latched);
                unit.Fill(data[0]); break;
            case 4:
                if (!unit.MutableImage) { Emit(ExpansionResult.InvalidState); return; }
                arena.RevokeExchange(latched);
                unit.ReplaceImage(BinaryPrimitives.ReadUInt16LittleEndian(data), data[8..]); break;
            case 5:
                if (!unit.Stable || !unit.Retired && unit.Lifecycle != ExpansionLifecycle.Paused)
                { Emit(ExpansionResult.InvalidState); return; }
                Emit(ExpansionResult.Accepted, unit.Copy(BinaryPrimitives.ReadUInt16LittleEndian(data),
                    checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[4..])))); return;
            case 7:
                if (unit.Lifecycle != ExpansionLifecycle.ResetStopped) { Emit(ExpansionResult.InvalidState); return; }
                unit.Start(tick); break;
            case 8:
                if (unit.Lifecycle is not (ExpansionLifecycle.Paused or ExpansionLifecycle.ServiceIdle))
                { Emit(ExpansionResult.InvalidState); return; }
                arena.RevokeExchange(latched);
                unit.Resume(tick); break;
            case 9:
                if (!unit.Stable || !unit.Retired && unit.Lifecycle != ExpansionLifecycle.Paused)
                { Emit(ExpansionResult.InvalidState); return; }
                var json = JsonSerializer.SerializeToUtf8Bytes(unit.CaptureFrontier(), jsonOptions);
                if (json.Length > 4096) { Emit(ExpansionResult.ResourceLimit); return; }
                var observed = new byte[4 + json.Length + 65536];
                BinaryPrimitives.WriteUInt32LittleEndian(observed, (uint)json.Length); json.CopyTo(observed, 4);
                for (var i = 0; i < 65536; i++) observed[4 + json.Length + i] = unit.ReadMemory((ushort)i);
                Emit(ExpansionResult.Accepted, observed); return;
            case 10:
                if (!unit.MutableImage || unit.HasWork) { Emit(ExpansionResult.InvalidState); return; }
                if (grant.ResourceEpoch == ulong.MaxValue || grant.MapEpoch == ulong.MaxValue)
                { Emit(ExpansionResult.ResourceLimit); return; }
                var pages = new ExpansionPageMap(BinaryPrimitives.ReadUInt16LittleEndian(data),
                    BinaryPrimitives.ReadUInt16LittleEndian(data[2..]), BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(data[6..]));
                // Allocate the replacement lease before publication; no allocation can fail after the zero/map commit.
                var replacement = new ExpansionMemoryLease(arena,
                    new(latched, checked(grant.ResourceEpoch + 1), checked(grant.MapEpoch + 1), pages));
                if (!arena.TryRepurpose(grant, pages)) { Emit(ExpansionResult.InvalidState); return; }
                unit.Reset(tick, replacement); break;
            case 11:
                var sent = unit.Mailbox.SendInbox(data);
                if (sent == ExpansionResult.Accepted) unit.Wake(tick);
                Emit(sent); return;
            case 12:
                var read = unit.Mailbox.ReadOutbox(out var frame);
                if (read == ExpansionResult.Accepted && unit.Lifecycle == ExpansionLifecycle.ServiceIdle &&
                    latched <= ExpansionArena.ComputeCount) // the exchange window is the compute job ABI; drivers never latch
                {
                    if (arena.ExchangeLive && !arena.OwnsExchange(latched)) { Emit(ExpansionResult.Busy); return; }
                    var retained = unit.CaptureFrontier();
                    if (retained.Halted || retained.Registers.PC >= 0xc000 || retained.Registers.SP > 0xbfff)
                    { Emit(ExpansionResult.InvalidState); return; }
                    if (!arena.TryInstallExchange(latched)) { Emit(ExpansionResult.Busy); return; }
                }
                Emit(read, frame); return;
            case 13:
                Emit(unit.Mailbox.AcknowledgeOutbox(BinaryPrimitives.ReadUInt64LittleEndian(data))); return;
            default: Emit(ExpansionResult.Unsupported); return;
        }
        Emit(ExpansionResult.Accepted);
    }
    public void AcknowledgeFrontier(ushort id, long masterTick)
    {
        VerifyOwner();
        if (pendingId != id || pendingId == 0) return;
        var unit = GetUnit(id);
        if (unit.LastMasterTick != masterTick) throw new InvalidOperationException("Control acknowledgment needs the committed frontier.");
        var op = pendingOperation;
        pendingId = 0; pendingOperation = 0;
        if (op == 2 && !OwnedCardsQuiescent(id)) { Emit(ExpansionResult.Busy); return; }
        if (op == 2) { arena.RevokeExchange(id); unit.Reset(masterTick); Mmu?.ClearFault(id); } else unit.Pause(masterTick);
        Emit(ExpansionResult.Accepted);
    }
    // Full-X5 root map ops (host-composed Mmu only; refused as Unsupported without it - the
    // compatibility profile gate). One outstanding request; no effect beyond an explicit error.
    private void MapOp(byte op, ReadOnlySpan<byte> data)
    {
        if (Mmu is null) { Emit(ExpansionResult.Unsupported); return; }
        var holder = latched;
        if (holder >= 1 && holder <= ExpansionArena.ComputeCount)
        { Emit(ExpansionResult.Unsupported); return; } // compute units never own a map
        if (op == 14)
        {
            if (holder == 0 && Services?.MainMapBusy == true) { Emit(ExpansionResult.Busy); return; }
            var slots = new ExpansionMmuSlot[ExpansionMmu.SlotCount];
            for (var i = 0; i < ExpansionMmu.SlotCount; i++)
            {
                var b = 16 + i * 16;
                slots[i] = new((ExpansionSlotKind)data[b], (ExpansionSlotCaps)data[b + 1],
                    BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(b + 2, 4)));
            }
            var expectedUnit = BinaryPrimitives.ReadUInt64LittleEndian(data);
            if (holder == 0 ? expectedUnit != 1 : expectedUnit != units[holder - 1].ExecutionEpoch)
            { Emit(ExpansionResult.StaleEpoch); return; }
            var expectedMap = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(8, 8));
            Emit(Mmu.TryMap((ushort)holder, expectedMap, slots));
            return;
        }
        // op 16 MAP_OBSERVE: bounded observation, no receipt overwrite while a response is unread.
        var state = Mmu.Capture((ushort)holder);
        var payload = new byte[4 * 16 + 8 + 1 + 2 + 1 + 8];
        var observed = new[] { state.Slot0, state.Slot1, state.Slot2, state.Slot3 };
        for (var i = 0; i < ExpansionMmu.SlotCount; i++)
        {
            payload[i * 16] = (byte)observed[i].Kind;
            payload[i * 16 + 1] = (byte)observed[i].Caps;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(i * 16 + 2, 4), observed[i].Target);
        }
        var cursor = 4 * 16;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(cursor, 8), state.MapEpoch); cursor += 8;
        payload[cursor++] = state.Fault is null ? (byte)0 : (byte)1;
        if (state.Fault is not null)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(cursor, 2), state.Fault.Value.Address); cursor += 2;
            payload[cursor++] = (byte)state.Fault.Value.Access;
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(cursor, 8), state.Fault.Value.MapEpoch);
        }
        Emit(ExpansionResult.Accepted, payload);
    }

    // v1 static owner table: the initial table is empty; HANDOFF transfers one device slot between
    // two quiescent driver units and publishes atomically (single cell + epoch, machine-owner thread).
    private void Handoff(ReadOnlySpan<byte> data)
    {
        if (Mmu is null) { Emit(ExpansionResult.Unsupported); return; }
        if (pendingId != 0) { Emit(ExpansionResult.Busy); return; }
        var source = latched;
        var target = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2, 2));
        var device = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2));
        if (BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0, 2)) != source ||
            source is < ExpansionArena.DriverFirst or > ExpansionArena.DriverLast ||
            target is < ExpansionArena.DriverFirst or > ExpansionArena.DriverLast ||
            source == target || device > deviceOwners.Length - 1)
        { Emit(ExpansionResult.InvalidFrame); return; }
        if (target > units.Length) { Emit(ExpansionResult.NotFound); return; }
        CardBinding? card = null;
        if (device != 0 && !ownedCards.TryGetValue(device, out card)) { Emit(ExpansionResult.NotFound); return; }
        var s = units[source - 1]; var t = units[target - 1];
        if (s.Lifecycle is not (ExpansionLifecycle.ServiceIdle or ExpansionLifecycle.ResetStopped) ||
            t.Lifecycle is not (ExpansionLifecycle.ServiceIdle or ExpansionLifecycle.ResetStopped) ||
            s.HasWork || t.HasWork || Mmu.Faulted(source) || Mmu.Faulted(target) ||
            !OwnedCardsQuiescent(source) || !OwnedCardsQuiescent(target) || card is not null && !card.IsQuiescent())
        { Emit(ExpansionResult.Busy); return; }
        if (device != 0)
        {
            var current = deviceOwners[device];
            if (current != 0 && current != source) { Emit(ExpansionResult.InvalidState); return; }
            if (ownerEpoch == ulong.MaxValue) { Emit(ExpansionResult.ResourceLimit); return; }
            deviceOwners[device] = (byte)target;
            ownerEpoch++;
        }
        var receipt = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(receipt, target);
        BinaryPrimitives.WriteUInt16LittleEndian(receipt.AsSpan(2), device);
        Emit(ExpansionResult.Accepted, receipt);
    }

    private static bool ValidPayload(byte op, ReadOnlySpan<byte> data)
    {
        switch (op)
        {
            case 1: case 2: case 6: case 7: case 8: case 9: case 12: case 16: return data.IsEmpty;
            case 3: return data.Length == 1;
            case 4: case 5:
                if (data.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) != 0) return false;
                var count = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
                var offset = BinaryPrimitives.ReadUInt16LittleEndian(data);
                return count is > 0 and <= 65536 && count <= 65536u - offset &&
                    (op != 4 || offset + count <= 0xc000 || offset >= 0xf000) &&
                    data.Length == (op == 4 ? 8 + (int)count : 8);
            case 10: return data.Length == 9 && data[8] == 0;
            case 11: return data.Length == ExpansionMailbox.FrameBytes;
            case 13: return data.Length == 8;
            case 14:
                // 16-byte header (expected unit epoch + expected map epoch) + four 16-byte slot records
                // (kind, caps, target u32, 10 reserved zero bytes).
                if (data.Length != 16 + 4 * 16) return false;
                for (var i = 0; i < 4; i++)
                    if (data.Slice(22 + i * 16, 10).ContainsAnyExcept((byte)0)) return false;
                return true;
            case 15: return data.Length == 6;
            default: return true; // Unknown operation gets Unsupported, not a guessed payload interpretation.
        }
    }
    private byte[] Query()
    {
        var data = new byte[32];
        BinaryPrimitives.WriteUInt16LittleEndian(data, latched);
        data[2] = (byte)(latched == 0 ? CpuPortRole.Main : latched <= ExpansionArena.ComputeCount ? CpuPortRole.Compute : CpuPortRole.Driver);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), ExpansionArena.ComputeCount);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), ExpansionMailbox.PayloadBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), ExpansionArena.PageBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), MaxRequestPayload);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), 5);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(24), 1);
        return data;
    }
    private void Emit(ExpansionResult result, byte[]? payload = null)
    {
        if (response is not null) throw new InvalidOperationException("An unread receipt cannot be overwritten.");
        payload ??= [];
        if (payload.Length > MaxObservePayload) throw new InvalidOperationException("Expansion response exceeds admission.");
        var next = new byte[HeaderBytes + payload.Length]; next[0] = 1; next[1] = (byte)result;
        BinaryPrimitives.WriteUInt32LittleEndian(next.AsSpan(4), (uint)payload.Length);
        if (stagingCount >= 16) BinaryPrimitives.WriteUInt64LittleEndian(next.AsSpan(8), U64(8));
        if (latched == 0)
        {
            next[2] = (byte)ExpansionLifecycle.Running;
            for (var offset = 16; offset <= 32; offset += 8) BinaryPrimitives.WriteUInt64LittleEndian(next.AsSpan(offset), 1);
        }
        else if (latched <= units.Length)
        {
            var unit = units[latched - 1]; var grant = arena.GetGrant(latched); next[2] = (byte)unit.Lifecycle;
            BinaryPrimitives.WriteUInt64LittleEndian(next.AsSpan(16), grant.ResourceEpoch);
            BinaryPrimitives.WriteUInt64LittleEndian(next.AsSpan(24), unit.ExecutionEpoch);
            BinaryPrimitives.WriteUInt64LittleEndian(next.AsSpan(32), grant.MapEpoch);
        }
        payload.CopyTo(next, HeaderBytes);
        response = next; responseCursor = 0; lastResult = result;
    }
    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(staging.AsSpan(offset));
    private ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(staging.AsSpan(offset));
    private void VerifyOwner()
    {
        if (Environment.CurrentManagedThreadId != owner)
            throw new InvalidOperationException("Expansion control belongs to the emulation owner.");
    }
}
