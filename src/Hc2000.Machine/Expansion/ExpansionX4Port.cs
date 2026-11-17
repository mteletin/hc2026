using System.Buffers.Binary;
using Hc2000.Machine.Io;
using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Expansion;

internal sealed record X4PortSnapshot(int Version, ushort CardId, X3LogicalPrincipal? Principal,
    X3LogicalPrincipal? EngineOwner, byte[] Staging, bool Collecting, bool Overflow, bool Error,
    byte[]? Reply, int Cursor, bool SharedReceipt);

internal sealed class ExpansionX4Port : IOriginPortDevice
{
    internal const ushort CommandPort = 0x7a9d, DataPort = 0x7b9d;
    internal const byte CommandService = 1;
    private readonly DeviceScheduler scheduler;
    private readonly ExpansionX4Engine engine;
    private readonly ExpansionServiceDispatcher services;
    private readonly IPortOriginPolicy policy;
    private readonly ushort cardId;
    private readonly byte[] staging = new byte[32];
    private CpuPortOrigin? principal, engineOwner;
    private int count, cursor;
    private bool collecting, overflow, error, sharedReceipt;
    private byte[]? reply;

    internal ExpansionX4Port(DeviceScheduler scheduler, ExpansionX4Engine engine,
        ExpansionServiceDispatcher services, IPortOriginPolicy policy, ushort cardId)
    {
        this.scheduler = scheduler; this.engine = engine; this.services = services; this.policy = policy; this.cardId = cardId;
        if (!services.RegisterCard(cardId) || !services.RegisterService(cardId, CommandService, Admit))
            throw new InvalidOperationException("X4 service registration failed.");
    }

    internal IReadOnlyList<PortMapping> Mappings() =>
        [new(CommandPort, 0xffff, this, PortAccess.ReadWrite), new(DataPort, 0xffff, this, PortAccess.ReadWrite)];
    internal bool IsQuiescent => principal is null && engine.IsQuiescent;
    public byte Read(ushort port, long masterTick) => 0xff;
    public void Write(ushort port, byte value, long masterTick) { }

    public byte Read(in CpuPortOrigin origin, ushort port, long masterTick)
    {
        if (!Allowed(in origin, port, PortAccess.Read, masterTick)) return 0xff;
        if (port == CommandPort)
            return (byte)(0x10 | (engine.State == X4EngineState.Busy ? 1 : 0) | (collecting ? 2 : 0) |
                (error ? 4 : 0) | (reply is not null && cursor < reply.Length ? 8 : 0));
        return reply is not null && cursor < reply.Length ? reply[cursor++] : (byte)0xff;
    }

    public void Write(in CpuPortOrigin origin, ushort port, byte value, long masterTick)
    {
        if (!Allowed(in origin, port, PortAccess.Write, masterTick)) return;
        if (port == DataPort)
        {
            if (!collecting) { error = true; return; }
            if (count == staging.Length) { overflow = error = true; return; }
            staging[count++] = value;
            return;
        }
        switch (value)
        {
            case 1:
                principal = origin; count = cursor = 0; collecting = true; overflow = error = sharedReceipt = false; reply = null;
                Array.Clear(staging);
                break;
            case 2:
                if (!collecting) { error = true; break; }
                collecting = false;
                var frame = staging.AsSpan(0, count).ToArray();
                count = 0;
                if (frame.Length < 8) { error = true; reply = null; break; }
                if (overflow || frame.Length != 8 + frame[2] || frame[3] != 0)
                {
                    Latch(Refusal(frame, X4Admission.Invalid));
                    overflow = false;
                    break;
                }
                var receipt = services.Submit(cardId, CommandService, frame, masterTick);
                sharedReceipt = receipt.State == X0ServiceState.Done && receipt.Result is { Length: >= 8 and <= 64 };
                Latch(sharedReceipt
                    ? receipt.Result! : Refusal(frame, receipt.State == X0ServiceState.Busy ? X4Admission.Busy : X4Admission.ResourceLimit));
                if (sharedReceipt && reply![2] == (byte)X4Admission.Ok)
                {
                    if (frame[1] == 1) engineOwner = origin;
                    if (frame[1] == 4) engineOwner = null;
                }
                break;
            case 3:
                principal = null; count = cursor = 0; collecting = overflow = error = sharedReceipt = false; reply = null;
                Array.Clear(staging);
                break;
            case 4:
                if (reply is not null) cursor = 0;
                break;
            default:
                error = true;
                break;
        }
    }

    private bool Allowed(in CpuPortOrigin origin, ushort port, PortAccess access, long tick) =>
        tick == scheduler.Now && (port == CommandPort || port == DataPort) &&
        policy.Allows(in origin, port, access) && (principal is null ||
            principal.Value.UnitId == origin.UnitId && principal.Value.Epoch == origin.Epoch && principal.Value.Role == origin.Role);

    internal X4PortSnapshot Capture() => new(1, cardId, Logical(principal), Logical(engineOwner),
        staging.AsSpan(0, count).ToArray(), collecting, overflow, error, reply?.ToArray(), cursor, sharedReceipt);

    internal bool ValidState(X4PortSnapshot? state, X4EngineSnapshot engineState, ExpansionX0.X0ServiceCardState service,
        long now, Func<X3LogicalPrincipal, bool> ownerAllowed)
    {
        if (state is not { Version: 1, Staging: not null } || state.CardId != cardId || state.Staging.Length > 32 ||
            state.Cursor < 0 || service.CardId != cardId || now < 0 || ownerAllowed is null ||
            state.Principal is { } principalValue && (principalValue.Epoch == 0 || !ownerAllowed(principalValue)) ||
            state.EngineOwner is { } engineValue && (engineValue.Epoch == 0 || !ownerAllowed(engineValue)) ||
            (engineState.State == X4EngineState.Idle) != (state.EngineOwner is null)) return false;
        if (state.Collecting)
            return state.Principal is not null && state.Reply is null && state.Cursor == 0 && !state.SharedReceipt &&
                (!state.Overflow || state.Staging.Length == 32 && state.Error);
        if (state.Staging.Length != 0 || state.Overflow) return false;
        if (state.Reply is null) return state.Cursor == 0 && !state.SharedReceipt;
        if (state.Principal is null || state.Cursor > state.Reply.Length || !ValidReply(state.Reply, now)) return false;
        if (state.Reply[2] is (byte)X4Admission.Invalid or (byte)X4Admission.Unsupported && !state.Error) return false;
        if (state.SharedReceipt)
            return service.State == (byte)X0ServiceState.Done && service.ServiceId == CommandService && service.Completed &&
                service.SubmitTick >= 0 && service.SubmitTick <= now && service.Result is not null && service.Result.SequenceEqual(state.Reply);
        return state.Reply[2] is (byte)X4Admission.Invalid or (byte)X4Admission.Busy or (byte)X4Admission.ResourceLimit;
    }

    internal void Restore(X4PortSnapshot state, X4EngineSnapshot engineState, ExpansionX0.X0ServiceCardState service,
        long now, Func<X3LogicalPrincipal, bool> ownerAllowed)
    {
        if (!ValidState(state, engineState, service, now, ownerAllowed)) throw new ArgumentException("Invalid X4 port state.", nameof(state));
        principal = Physical(state.Principal); engineOwner = Physical(state.EngineOwner);
        Array.Clear(staging); state.Staging.CopyTo(staging, 0); count = state.Staging.Length;
        collecting = state.Collecting; overflow = state.Overflow; error = state.Error;
        reply = state.Reply?.ToArray(); cursor = state.Cursor; sharedReceipt = state.SharedReceipt;
    }

    internal void ResetTransport()
    {
        principal = engineOwner = null; count = cursor = 0; collecting = overflow = error = sharedReceipt = false;
        reply = null; Array.Clear(staging);
    }

    private static X3LogicalPrincipal? Logical(CpuPortOrigin? origin) => origin is { } value ? new(value.UnitId, value.Epoch, value.Role) : null;
    private static CpuPortOrigin? Physical(X3LogicalPrincipal? origin) => origin is { } value ? new(value.UnitId, value.Epoch, value.Role) : null;

    private static bool ValidReply(byte[] response, long now)
    {
        if (response.Length < 8 || response[0] != 1 || !Enum.IsDefined((X4Admission)response[2]) ||
            response[3] != response.Length - 8) return false;
        if (response[2] != (byte)X4Admission.Ok) return response.Length == 8;
        if (response[1] == 0) return response.Length == 24 && response.AsSpan(8).SequenceEqual(
            new byte[] { 1, 1, 1, 0, 0x1f, 0, 0, 0, 0xfe, 0, 0, 0, 32, 64, 0, 0 });
        if (response[1] is < 1 or > 4 || response.Length != 64) return false;
        var view = response.AsSpan(8);
        return view[0] <= (byte)X4EngineState.Cancelled && view[4] <= (byte)X4NumericStatus.InvalidNumericEncoding &&
            view[5] is 0 or 4 or 6 or 8 or 16 && (BinaryPrimitives.ReadUInt16LittleEndian(view[6..]) & ~7) == 0 &&
            BinaryPrimitives.ReadUInt16LittleEndian(view[12..]) == 1 && BinaryPrimitives.ReadUInt16LittleEndian(view[14..]) == 1 &&
            BinaryPrimitives.ReadUInt64LittleEndian(view[16..]) != 0 && BinaryPrimitives.ReadUInt64LittleEndian(view[24..]) <= (ulong)now &&
            BinaryPrimitives.ReadUInt64LittleEndian(view[32..]) <= long.MaxValue;
    }

    private void Latch(byte[] response)
    {
        reply = response.ToArray(); cursor = 0;
        if (response[2] is (byte)X4Admission.Invalid or (byte)X4Admission.Unsupported) error = true;
    }

    private X0ServiceOutcome Admit(in X0ServiceCall call, long tick)
    {
        var response = tick == scheduler.Now ? engine.Handle(call.Args) : Refusal(call.Args, X4Admission.ResourceLimit);
        return X0ServiceOutcome.Completed(response);
    }

    private static byte[] Refusal(ReadOnlySpan<byte> frame, X4Admission result)
    {
        var response = new byte[8];
        response[0] = 1; response[1] = frame[1]; response[2] = (byte)result;
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4), BinaryPrimitives.ReadUInt32LittleEndian(frame[4..8]));
        return response;
    }
}
