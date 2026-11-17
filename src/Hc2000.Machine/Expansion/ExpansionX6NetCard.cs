using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Expansion;

/// <summary>The X6 network card (hc2000-ssh-terminal): an X5 owned card whose ports belong to the driver unit
/// that owns it, so CP/M reaches the network only through that driver's delegated API. The card carries request
/// frames to the host and replies back; the host side (the App's network runtime, behind the owner's outbound
/// gateway) owns the sockets and the encryption. Without a host handler every request is answered "no host".
/// <para>Transport, like X2 and X4: writing 1 to the command port opens a frame, data-port writes fill it (at most
/// 64 bytes), 2 submits it, 3 abandons it and 4 rewinds the reply. Reading the command port: 0x10 plus bit 1 a
/// frame is open, bit 2 the last frame was refused by the transport, bit 3 reply bytes remain; reading the data
/// port takes the next reply byte. A request frame is [1, op, payload length, session] + payload; a reply is
/// [1, op, result, payload length] + payload, the result codes being <see cref="X6NetResult"/>. Every request
/// completes at once: slow work (a connect, a handshake) runs on the host while the guest polls a status op.</para>
/// <para>Reading the event/length port: bits 0-6 the reply bytes still to read, bit 7 a session has news (bytes
/// waiting or a state change not yet seen by a status op), so a driver can idle without a request. All three ports
/// are Exclusive to the owning driver unit: MAIN may only read passive idle values before ownership.</para>
/// </summary>
public sealed class ExpansionX6NetCard : IOriginPortDevice
{
    public const ushort CardId = 6;
    public const ushort CommandPort = 0x7c9d, DataPort = 0x7d9d, EventPort = 0x7e9d;
    public const string Kind = "net";
    public const byte Version = 1;
    public const byte CommandService = 1;
    public const int MaximumFrame = 64;
    private readonly DeviceScheduler scheduler;
    private readonly ExpansionServiceDispatcher services;
    private readonly IPortOriginPolicy policy;
    private readonly byte[] staging = new byte[MaximumFrame];
    private CpuPortOrigin? principal;
    private int count, cursor;
    private bool collecting, overflow, error, submitting;
    private byte[]? reply;

    private ExpansionX6NetCard(DeviceScheduler scheduler, ExpansionServiceDispatcher services, IPortOriginPolicy policy)
    {
        this.scheduler = scheduler; this.services = services; this.policy = policy;
        if (!services.RegisterCard(CardId) || !services.RegisterService(CardId, CommandService, Admit))
            throw new InvalidOperationException("X6 service registration failed.");
    }

    /// <summary>The host side: takes one request frame (already length-checked) and returns its reply frame.
    /// Called on the emulation thread; it must not block.</summary>
    public Func<byte[], byte[]>? Host { get; set; }

    /// <summary>Whether a session has news for the guest (the event port's bit 7); called on the emulation thread.</summary>
    public Func<bool>? EventPending { get; set; }

    internal bool IsQuiescent => principal is null;

    internal IReadOnlyList<PortMapping> Mappings() =>
        [new(CommandPort, 0xffff, this, PortAccess.ReadWrite), new(DataPort, 0xffff, this, PortAccess.ReadWrite),
            new(EventPort, 0xffff, this, PortAccess.Read)];

    public byte Read(ushort port, long masterTick) => 0xff;
    public void Write(ushort port, byte value, long masterTick) { }

    public byte Read(in CpuPortOrigin origin, ushort port, long masterTick)
    {
        if (origin.Role == CpuPortRole.Main)
        {
            if (masterTick != scheduler.Now || !policy.Allows(in origin, port, PortAccess.Read)) return 0xff;
            return port switch { CommandPort => 0x10, EventPort => 0, _ => 0xff };
        }
        if (origin.Role != CpuPortRole.Driver) return 0xff;
        if (!Allowed(in origin, port, PortAccess.Read, masterTick)) return 0xff;
        if (port == CommandPort)
            return (byte)(0x10 | (collecting ? 2 : 0) | (error ? 4 : 0) | (reply is not null && cursor < reply.Length ? 8 : 0));
        if (port == EventPort)
            return (byte)((reply is null ? 0 : reply.Length - cursor) | (EventPending?.Invoke() == true ? 0x80 : 0));
        return reply is not null && cursor < reply.Length ? reply[cursor++] : (byte)0xff;
    }

    public void Write(in CpuPortOrigin origin, ushort port, byte value, long masterTick)
    {
        if (origin.Role != CpuPortRole.Driver) return;
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
                principal = origin; count = cursor = 0; collecting = true; overflow = error = false; reply = null;
                Array.Clear(staging);
                break;
            case 2:
                if (!collecting) { error = true; break; }
                collecting = false;
                var frame = staging.AsSpan(0, count).ToArray();
                count = 0;
                if (overflow || frame.Length < 4 || frame[0] != 1 || frame.Length != 4 + frame[2])
                {
                    reply = Reply(frame.Length > 1 ? frame[1] : (byte)0, X6NetResult.Invalid, []);
                    cursor = 0; error = true; overflow = false;
                    principal = null;
                    break;
                }
                X0ServiceStatus receipt;
                submitting = true;
                try { receipt = services.Submit(CardId, CommandService, frame, masterTick); }
                finally { submitting = false; }
                reply = receipt.State == X0ServiceState.Done && receipt.Result is { Length: >= 4 and <= MaximumFrame }
                    ? receipt.Result : Reply(frame[1], X6NetResult.Busy, []);
                cursor = 0;
                principal = null;
                break;
            case 3:
                ResetTransport();
                break;
            case 4:
                if (reply is not null) cursor = 0;
                break;
            default:
                error = true;
                break;
        }
    }

    internal void ResetTransport()
    {
        principal = null; count = cursor = 0; collecting = overflow = error = submitting = false; reply = null;
        Array.Clear(staging);
    }

    /// <summary>A reply frame: [1, op, result, length] + payload.</summary>
    public static byte[] Reply(byte op, X6NetResult result, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumFrame - 4) throw new ArgumentOutOfRangeException(nameof(payload));
        var frame = new byte[4 + payload.Length];
        frame[0] = 1; frame[1] = op; frame[2] = (byte)result; frame[3] = (byte)payload.Length;
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    private bool Allowed(in CpuPortOrigin origin, ushort port, PortAccess access, long tick) =>
        tick == scheduler.Now && (port == CommandPort || port == DataPort || port == EventPort) &&
        policy.Allows(in origin, port, access) && (principal is null ||
            principal.Value.UnitId == origin.UnitId && principal.Value.Epoch == origin.Epoch && principal.Value.Role == origin.Role);

    private X0ServiceOutcome Admit(in X0ServiceCall call, long tick)
    {
        var request = call.Args;
        var authorised = submitting && principal is { Role: CpuPortRole.Driver } owner &&
            tick == scheduler.Now && policy.Allows(in owner, CommandPort, PortAccess.Write);
        submitting = false;
        if (!authorised) return X0ServiceOutcome.Completed(Reply(request.Length > 1 ? request[1] : (byte)0, X6NetResult.Invalid, []));
        if (Host is not { } host) return X0ServiceOutcome.Completed(Reply(request[1], X6NetResult.NoHost, []));
        var response = host(request);
        if (response is not { Length: >= 4 and <= MaximumFrame } || response[0] != 1 || response.Length != 4 + response[3])
            response = Reply(request[1], X6NetResult.Invalid, []);
        return X0ServiceOutcome.Completed(response);
    }

    /// <summary>Composes the card on the X5 profile: card id 6, kind net, its three ports, all Exclusive to the
    /// owning driver.</summary>
    public static bool TryCompose(HcMemory memory, DeviceScheduler scheduler, ExpansionController controller,
        ExpansionX0 x0, IReadOnlyList<PortMapping> otherMappings, out ExpansionX6NetCard? card)
    {
        ArgumentNullException.ThrowIfNull(memory); ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(controller); ArgumentNullException.ThrowIfNull(x0); ArgumentNullException.ThrowIfNull(otherMappings);
        card = null;
        if (!controller.SharesArena(memory.ExpansionArena) || !x0.Services.CanRegisterCard(CardId)) return false;
        var placeholder = new PortDevice();
        PortMapping[] proposed = [new(CommandPort, 0xffff, placeholder, PortAccess.ReadWrite),
            new(DataPort, 0xffff, placeholder, PortAccess.ReadWrite), new(EventPort, 0xffff, placeholder, PortAccess.Read)];
        try
        {
            controller.ValidateOwnedCard(CardId, Kind, Version, proposed);
            _ = new PortDecoder(otherMappings.Concat(proposed));
        }
        catch (ArgumentException) { return false; }
        var candidate = new ExpansionX6NetCard(scheduler, x0.Services, controller);
        controller.RegisterOwnedCard(CardId, Kind, Version, candidate.Mappings(), () => candidate.IsQuiescent);
        card = candidate;
        return true;
    }
}

/// <summary>The result byte of an X6 reply.</summary>
public enum X6NetResult : byte
{
    Ok = 0,
    /// <summary>The frame or its fields are malformed.</summary>
    Invalid = 1,
    /// <summary>The card could not take the request now; try again.</summary>
    Busy = 2,
    /// <summary>No host side is attached (a machine without the network runtime).</summary>
    NoHost = 3,
    /// <summary>The owner's outbound gateway is off for this machine.</summary>
    GatewayOff = 4,
    /// <summary>The session number is not an open session.</summary>
    NoSession = 5,
    /// <summary>No free session slot.</summary>
    NoSlot = 6,
    /// <summary>The op is not known to this card version.</summary>
    Unsupported = 7
}
