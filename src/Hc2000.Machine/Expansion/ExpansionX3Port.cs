using System.Buffers.Binary;
using Hc2000.Machine.Io;
using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Expansion;

internal enum X3PortResult : byte { Accepted, Busy, Invalid, Unsupported, SourceDenied, ResourceLimit, InvalidState, TimeLimit }
internal readonly record struct X3LogicalPrincipal(uint UnitId, ulong Epoch, CpuPortRole Role);
internal sealed record X3PortSnapshot(int Version, ushort CardId, X3LogicalPrincipal? Principal, byte[] Staging,
    bool Collecting, bool Overflow, bool Error, byte[]? Reply, int Cursor, bool SharedReceipt);

/// <summary>Bounded X3 command adapter. The decoder and this endpoint both authenticate the
/// origin; synchronous X0 receipts acknowledge commands rather than playback completion.</summary>
internal sealed class ExpansionX3Port : IOriginPortDevice
{
    internal const ushort CommandPort = 0x789d, DataPort = 0x799d;
    internal const byte CommandService = 1;
    private const int HeaderSize = 8, MaximumFrame = 32;
    private readonly DeviceScheduler scheduler;
    private readonly ExpansionX3Streamer streamer;
    private readonly ExpansionServiceDispatcher services;
    private readonly IPortOriginPolicy policy;
    private readonly ushort cardId;
    private readonly byte[] staging = new byte[MaximumFrame];
    private int count, cursor;
    private bool collecting, overflow, error, sharedReceipt;
    private byte[]? reply;
    private CpuPortOrigin? principal;

    internal ExpansionX3Port(DeviceScheduler scheduler, ExpansionX3Streamer streamer,
        ExpansionServiceDispatcher services, IPortOriginPolicy policy, ushort cardId)
    {
        ArgumentNullException.ThrowIfNull(scheduler); ArgumentNullException.ThrowIfNull(streamer);
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(policy);
        this.scheduler = scheduler; this.streamer = streamer; this.services = services; this.policy = policy; this.cardId = cardId;
        if (!services.RegisterCard(cardId) || !services.RegisterService(cardId, CommandService, Admit))
            throw new InvalidOperationException("X3 service registration failed.");
    }

    internal IReadOnlyList<PortMapping> Mappings() =>
        [new(CommandPort, 0xffff, this, PortAccess.ReadWrite), new(DataPort, 0xffff, this, PortAccess.ReadWrite)];

    internal bool IsQuiescent => principal is null && streamer.IsQuiescent;

    public byte Read(ushort port, long masterTick) => 0xff;
    public void Write(ushort port, byte value, long masterTick) { }

    public byte Read(in CpuPortOrigin origin, ushort port, long masterTick)
    {
        if (!Allowed(in origin, port, PortAccess.Read, masterTick)) return 0xff;
        if (port == CommandPort)
        {
            var active = Enumerable.Range(0, ExpansionX3Clock.ChannelCount)
                .Any(channel => streamer.State(channel) is X3PlaybackState.Priming or X3PlaybackState.Playing);
            return (byte)(0x10 | (active ? 1 : 0) | (collecting ? 2 : 0) | (error ? 4 : 0) | (reply is not null ? 8 : 0));
        }
        if (port == DataPort && reply is not null && cursor < reply.Length) return reply[cursor++];
        return 0xff;
    }

    public void Write(in CpuPortOrigin origin, ushort port, byte value, long masterTick)
    {
        if (!Allowed(in origin, port, PortAccess.Write, masterTick)) return;
        if (port == DataPort)
        {
            if (!collecting) return;
            if (count == MaximumFrame) { overflow = true; return; }
            staging[count++] = value;
            return;
        }
        if (port != CommandPort) return;
        switch (value)
        {
            case 1:
                principal = origin; count = cursor = 0; collecting = true; overflow = error = sharedReceipt = false; reply = null;
                Array.Clear(staging);
                break;
            case 2:
                if (!collecting) return;
                collecting = false;
                var frame = staging.AsSpan(0, count).ToArray();
                count = 0;
                if (overflow) { Latch(Refusal(frame, X3PortResult.Invalid)); overflow = false; break; }
                var validation = Decode(frame, out _, out _, out _);
                if (validation != X3PortResult.Accepted) { Latch(Refusal(frame, validation)); break; }
                var receipt = services.Submit(cardId, CommandService, frame, masterTick);
                sharedReceipt = receipt.State == X0ServiceState.Done && receipt.Result is { Length: >= 8 and <= 48 };
                Latch(receipt.State == X0ServiceState.Done && receipt.Result is { Length: >= 8 and <= 48 }
                    ? receipt.Result : Refusal(frame, receipt.State == X0ServiceState.Busy ? X3PortResult.Busy : X3PortResult.InvalidState));
                break;
            case 3:
                principal = null; count = cursor = 0; collecting = overflow = error = sharedReceipt = false; reply = null;
                Array.Clear(staging);
                break;
            case 4:
                if (reply is not null) cursor = 0;
                break;
        }
    }

    private bool Allowed(in CpuPortOrigin origin, ushort port, PortAccess access, long tick) =>
        tick == scheduler.Now && (port == CommandPort || port == DataPort) &&
        policy.Allows(in origin, port, access) && (principal is null ||
            principal.Value.UnitId == origin.UnitId && principal.Value.Epoch == origin.Epoch && principal.Value.Role == origin.Role);

    internal X3PortSnapshot Capture() => new(1, cardId, principal is { } owner ? new(owner.UnitId, owner.Epoch, owner.Role) : null,
        staging.AsSpan(0, count).ToArray(), collecting, overflow, error, reply?.ToArray(), cursor, sharedReceipt);

    // The caller supplies authority from the saved controller roster, not an external bearer token.
    internal bool ValidState(X3PortSnapshot? state, ExpansionX0.X0ServiceCardState? service, long now,
        Func<X3LogicalPrincipal, bool> ownerAllowed)
    {
        if (state is not { Version: 1 } || state.CardId != cardId || now < 0 || service is null || service.CardId != cardId ||
            ownerAllowed is null || state.Staging is null || state.Staging.Length > MaximumFrame || state.Cursor < 0 ||
            state.Principal is { } owner && (owner.Epoch == 0 || !ownerAllowed(owner))) return false;
        if (state.Principal is null)
            return !state.Collecting && !state.Overflow && !state.Error && state.Reply is null &&
                state.Staging.Length == 0 && state.Cursor == 0 && !state.SharedReceipt;
        if (state.Collecting)
            return state.Reply is null && state.Cursor == 0 && !state.Error && !state.SharedReceipt &&
                (!state.Overflow || state.Staging.Length == MaximumFrame);
        if (state.Overflow || state.Staging.Length != 0 || state.Reply is not { Length: 8 or 48 } ||
            state.Cursor > state.Reply.Length || !ValidReply(state.Reply, now) ||
            state.Error != (state.Reply[2] != (byte)X3PortResult.Accepted)) return false;
        if (state.SharedReceipt)
            return service.State == (byte)X0ServiceState.Done && service.ServiceId == CommandService && service.Completed &&
                service.SubmitTick >= 0 && service.SubmitTick <= now &&
                service.Result is not null && service.Result.SequenceEqual(state.Reply);
        return state.Reply[2] is (byte)X3PortResult.Invalid or (byte)X3PortResult.Unsupported or
            (byte)X3PortResult.Busy or (byte)X3PortResult.InvalidState;
    }

    private static bool ValidReply(byte[] response, long now)
    {
        if (response[0] != 1 || !Enum.IsDefined((X3PortResult)response[2]) || response[3] != response.Length - HeaderSize ||
            (response[2] == (byte)X3PortResult.Accepted) != (response.Length == 48)) return false;
        if (response.Length == 8) return true;
        var payload = response.AsSpan(HeaderSize);
        var period = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
        var observed = BinaryPrimitives.ReadUInt64LittleEndian(payload[24..]);
        var next = BinaryPrimitives.ReadUInt64LittleEndian(payload[32..]);
        return response[1] is >= 1 and <= 4 && payload[0] < ExpansionX3Clock.ChannelCount && payload[1] <= 5 &&
            (payload[2] & ~7) == 0 && payload[3] <= 64 && (period == 0 || period >= ExpansionX3Sample.MinimumPeriod) &&
            BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]) <= ExpansionX3Fifo.Capacity &&
            payload.Slice(12, 4).IndexOfAnyExcept((byte)0) < 0 && BinaryPrimitives.ReadUInt64LittleEndian(payload[16..]) != 0 &&
            observed <= (ulong)now && (payload[1] == 2 ? next > observed && next <= long.MaxValue : next == 0);
    }

    internal void Restore(X3PortSnapshot state, ExpansionX0.X0ServiceCardState service, long now,
        Func<X3LogicalPrincipal, bool> ownerAllowed)
    {
        if (!ValidState(state, service, now, ownerAllowed)) throw new ArgumentException("Invalid X3 port state.", nameof(state));
        principal = state.Principal is { } owner ? new(owner.UnitId, owner.Epoch, owner.Role) : null;
        Array.Clear(staging); state.Staging.CopyTo(staging, 0); count = state.Staging.Length;
        collecting = state.Collecting; overflow = state.Overflow; error = state.Error;
        reply = state.Reply?.ToArray(); cursor = state.Cursor; sharedReceipt = state.SharedReceipt;
    }

    private void Latch(byte[] response)
    {
        reply = response.ToArray(); cursor = 0; error = response[2] != (byte)X3PortResult.Accepted;
    }

    private X0ServiceOutcome Admit(in X0ServiceCall call, long tick)
    {
        var result = Decode(call.Args, out var opcode, out var channel, out var sample);
        if (tick != scheduler.Now) result = X3PortResult.InvalidState;
        if (result == X3PortResult.Accepted)
        {
            switch (opcode)
            {
                case 1:
                    result = streamer.CheckStart(channel, sample);
                    if (result == X3PortResult.Accepted && !streamer.Start(channel, sample)) result = X3PortResult.InvalidState;
                    break;
                case 2:
                    if (!streamer.Stop(channel)) result = X3PortResult.ResourceLimit;
                    break;
                case 3:
                    if (!streamer.SetParameters(channel, sample.Period, sample.Volume)) result = X3PortResult.InvalidState;
                    break;
            }
        }
        if (result != X3PortResult.Accepted) return X0ServiceOutcome.Completed(Refusal(call.Args, result));
        var response = Refusal(call.Args, X3PortResult.Accepted, 40);
        var view = streamer.Inspect(channel);
        var payload = response.AsSpan(HeaderSize);
        payload[0] = (byte)channel;
        payload[1] = view.State switch { X3PlaybackState.Empty or X3PlaybackState.Stopped => 0, X3PlaybackState.Priming => 1,
            X3PlaybackState.Playing => 2, X3PlaybackState.Ended => 3, X3PlaybackState.Underrun => 4, _ => 5 };
        payload[2] = (byte)((view.Loop ? 1 : 0) | (view.PendingParameters ? 2 : 0) | (view.Refill ? 4 : 0));
        payload[3] = view.Volume;
        BinaryPrimitives.WriteUInt16LittleEndian(payload[4..], view.Period);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[6..], checked((ushort)view.Buffered));
        BinaryPrimitives.WriteUInt32LittleEndian(payload[8..], view.ReadPosition);
        BinaryPrimitives.WriteUInt64LittleEndian(payload[16..], view.Generation);
        BinaryPrimitives.WriteUInt64LittleEndian(payload[24..], checked((ulong)view.ObservedTick));
        BinaryPrimitives.WriteUInt64LittleEndian(payload[32..], checked((ulong)view.NextTick));
        return X0ServiceOutcome.Completed(response);
    }

    private static byte[] Refusal(ReadOnlySpan<byte> frame, X3PortResult result, byte payloadBytes = 0)
    {
        var response = new byte[HeaderSize + payloadBytes];
        response[0] = 1; response[2] = (byte)result; response[3] = payloadBytes;
        if (frame.Length >= HeaderSize) { response[1] = frame[1]; frame.Slice(4, 4).CopyTo(response.AsSpan(4)); }
        return response;
    }

    private static X3PortResult Decode(ReadOnlySpan<byte> frame, out byte opcode, out int channel, out ExpansionX3Sample sample)
    {
        opcode = 0; channel = 0; sample = default;
        if (frame.Length < HeaderSize || frame.Length > MaximumFrame || frame[0] != 1 || frame[3] != 0 ||
            frame.Length != HeaderSize + frame[2]) return X3PortResult.Invalid;
        opcode = frame[1]; var payload = frame[HeaderSize..];
        var expected = opcode switch { 1 => 24, 2 or 4 => 4, 3 => 8, _ => -1 };
        if (expected < 0) return X3PortResult.Unsupported;
        if (payload.Length != expected || payload[0] >= ExpansionX3Clock.ChannelCount || payload[1] != 0) return X3PortResult.Invalid;
        channel = payload[0];
        if (opcode == 1)
        {
            if (payload[21] != 0 || payload[22] != 0 || payload[23] != 0) return X3PortResult.Invalid;
            sample = new(BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]), BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(payload[6..]), BinaryPrimitives.ReadUInt32LittleEndian(payload[10..]),
                BinaryPrimitives.ReadUInt32LittleEndian(payload[14..]), BinaryPrimitives.ReadUInt16LittleEndian(payload[18..]), payload[20]);
            return sample.IsValid ? X3PortResult.Accepted : X3PortResult.Invalid;
        }
        if (opcode == 3)
        {
            if (payload[5] != 0 || payload[6] != 0 || payload[7] != 0) return X3PortResult.Invalid;
            var period = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
            if (period < ExpansionX3Sample.MinimumPeriod || payload[4] > 64) return X3PortResult.Invalid;
            sample = new(0, 0, 0, 0, 0, period, payload[4]);
        }
        else if (payload[2] != 0 || payload[3] != 0) return X3PortResult.Invalid;
        return X3PortResult.Accepted;
    }
}
