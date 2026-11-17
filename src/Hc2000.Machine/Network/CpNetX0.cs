using System.Buffers.Binary;
using Hc2000.Machine.Expansion;
using Hc2000.Machine.Io;

namespace Hc2000.Machine.Network;

/// <summary>
/// The CP/NET card on the ONE X0 platform (v2 transport model, decision
/// hc2000-cpnet-card-increment-v1): the guest frame moves guest-RAM-to-card over the ONE
/// shared physical-DMA channel, the exchange goes through the ONE main-CPU service dispatcher
/// (one outstanding service, Busy backpressure, explicit deadline/cancel/too-late, an
/// ambiguous outcome never replayed), and the reply is drained byte at a time from the data
/// port. Poll-only; the read-only status port never starts traffic or acknowledges work. The
/// version register (0x02) identifies this model; the v1 port-data card (CpNetCard) remains
/// the offline/test seam.
/// </summary>
public sealed class CpNetX0Card : IPortDevice
{
    public const ushort CardId = 0xC001;
    public const ushort StagingPage = 864;
    public const ushort StagingCapacity = CpNetFrame.MaxFrameBytes;
    public const byte ExchangeServiceId = 0x01;
    public const byte Version = 0x02;

    /// <summary>Finite exchange deadline policy in master ticks (about 3.1 s at the 3.25 MHz
    /// master). A versioned determinism policy, not a latency claim.</summary>
    public const long ExchangeDeadlineTicks = 10_000_000;

    public const ushort DataPort = CpNetCard.DataPort, CommandPort = CpNetCard.CommandPort,
        StatusPort = CpNetCard.StatusPort, VersionPort = CpNetCard.VersionPort;

    public const byte CommandTxCommit = CpNetCard.CommandTxCommit,
        CommandCancel = CpNetCard.CommandCancel,
        CommandErrorClear = CpNetCard.CommandErrorClear,
        CommandDmaBegin = 0x10, CommandDmaStart = 0x11, CommandDmaCancel = 0x12;

    public const byte ErrorNone = 0x00, ErrorBusy = 0x01, ErrorTooLarge = 0x02, ErrorEmpty = 0x03,
        ErrorRefused = 0x04, ErrorNoTarget = 0x05, ErrorUnknown = 0x06, ErrorAmbiguous = 0x07,
        ErrorTooLate = 0x08;

    /// <summary>High-nibble status flags: 0x10 TX frame staged, 0x20 reply drainable,
    /// 0x40 exchange pending, 0x80 an error is latched. The low nibble carries the error
    /// code. The version register selects the model; a v1 driver reads the v1 card.</summary>
    public const byte StatusTxReady = 0x10, StatusRxAvailable = 0x20,
        StatusExchanging = 0x40, StatusError = 0x80;

    private enum Phase { Idle, Programming, TxReady, Exchanging, RxPending }

    private readonly ExpansionX0 x0;
    private readonly ExpansionDma dma;
    private readonly ExpansionServiceDispatcher services;
    private readonly CpNetX0Endpoint endpoint = new();
    private readonly byte[] descriptor = new byte[ExpansionDma.DescriptorBytes];
    private int descriptorCount;
    private readonly byte[] rx = new byte[CpNetFrame.MaxFrameBytes];
    private int rxCount, rxCursor;
    private Phase phase;
    private byte error;

    /// <summary>Compose the card onto the shared X0 platform: one DMA channel and one
    /// dispatcher card entry. The exchange service handler is registered by the composition
    /// (it owns the host-session gateway boundary), not by the card.</summary>
    public CpNetX0Card(ExpansionX0 x0)
    {
        ArgumentNullException.ThrowIfNull(x0);
        this.x0 = x0;
        dma = x0.Dma;
        services = x0.Services;
        if (!services.RegisterCard(CardId))
            throw new ArgumentException("The CP/NET card id is already composed.");
        dma.RegisterCard(endpoint);
    }

    public CpNetX0Endpoint Endpoint => endpoint;
    public ExpansionX0 Platform => x0;

    public IReadOnlyList<PortMapping> Mappings() =>
    [
        new(DataPort, 0xffff, this, PortAccess.ReadWrite),
        new(CommandPort, 0xffff, this, PortAccess.Write),
        new(StatusPort, 0xffff, this, PortAccess.Read),
        new(VersionPort, 0xffff, this, PortAccess.Read)
    ];

    /// <summary>Read-only status: polling the dispatcher at the guest master time is how a
    /// deadline becomes a too-late receipt; nothing here starts traffic or acknowledges
    /// work. In an in-flight DMA window a completion the engine already committed flips the
    /// card to TxReady (card state only; the engine is acknowledged at the next commit or
    /// begin command).</summary>
    public byte Read(ushort port, long masterTick)
    {
        if (port == VersionPort) return Version;
        if (port == StatusPort) return Status(masterTick);
        if (port == DataPort && phase == Phase.RxPending && rxCursor < rxCount)
            return DrainReplyByte();
        return 0xff;
    }

    public void Write(ushort port, byte value, long masterTick)
    {
        if (port == DataPort)
        {
            if (phase == Phase.Programming)
            {
                if (descriptorCount < descriptor.Length)
                {
                    descriptor[descriptorCount++] = value;
                    dma.WriteDescriptor(CardId, value);
                }
                else Refuse(ErrorTooLarge); // an over-cap descriptor byte latches and changes nothing
            }
            return;
        }
        if (port != CommandPort) return;
        switch (value)
        {
            case CommandDmaBegin:
                BeginDma(masterTick);
                break;
            case CommandDmaStart:
                if (phase == Phase.Programming)
                {
                    dma.Trigger(CardId, X0DmaTrigger.Start, masterTick);
                    MapDmaRefusal();
                }
                else Refuse(ErrorBusy);
                break;
            case CommandDmaCancel:
                if (phase == Phase.Programming)
                    dma.Trigger(CardId, X0DmaTrigger.Cancel, masterTick);
                else Refuse(ErrorBusy);
                break;
            case CommandTxCommit:
                if (phase == Phase.TxReady) Commit(masterTick);
                else Refuse(ErrorBusy);
                break;
            case CommandCancel:
                if (phase == Phase.Exchanging) Cancel(masterTick);
                else Refuse(ErrorBusy);
                break;
            case CommandErrorClear:
                error = ErrorNone;
                break;
            default:
                break; // unprogrammed command bytes are guest-visible no-ops, like v1
        }
    }

    private byte Status(long masterTick)
    {
        if (phase is Phase.Programming or Phase.TxReady)
        {
            var status = dma.Status(CardId);
            if (status.State == X0DmaState.Done && endpoint.Count > 0 && phase == Phase.Programming)
                phase = Phase.TxReady; // engine committed at its ordered tick; acknowledge at the next command
        }
        if (phase == Phase.Exchanging)
        {
            var receipt = services.Poll(CardId, masterTick);
            ApplyService(receipt);
        }
        byte flags = 0;
        if (phase == Phase.TxReady) flags |= 1;
        if (phase == Phase.RxPending) flags |= 2;
        if (phase == Phase.Exchanging) flags |= 4;
        if (error != ErrorNone) flags |= 8;
        return (byte)(error | (flags << 4));
    }

    private void BeginDma(long masterTick)
    {
        if (phase == Phase.Exchanging)
        {
            Refuse(ErrorBusy);
            return;
        }
        if (phase == Phase.RxPending)
        {
            Refuse(ErrorBusy); // a partially drained reply is not discarded by a command
            return;
        }
        var status = dma.Status(CardId);
        if (status.State == X0DmaState.Programming)
        {
            Refuse(ErrorBusy); // a window is already open; close it with start or cancel
            return;
        }
        if (status.State == X0DmaState.Done || status.State == X0DmaState.Error ||
            status.State == X0DmaState.Cancelled || status.State == X0DmaState.TooLate)
            dma.Trigger(CardId, X0DmaTrigger.Acknowledge, masterTick);
        if (phase == Phase.TxReady)
        {
            // Re-staging replaces the staged frame before any commit.
            endpoint.Discard();
        }
        descriptorCount = 0;
        var begin = dma.Trigger(CardId, X0DmaTrigger.Begin, masterTick);
        if (begin.State != X0DmaState.Programming)
        {
            phase = Phase.Idle;
            MapDmaRefusal();
            return;
        }
        phase = Phase.Programming;
    }

    private void Commit(long masterTick)
    {
        var status = dma.Status(CardId);
        if (status.State == X0DmaState.Done)
            dma.Trigger(CardId, X0DmaTrigger.Acknowledge, masterTick);
        var frame = endpoint.Staged;
        if (!CpNetFrame.TryParse(frame, out _, out var frameError))
        {
            Refuse(frameError == CpNetFrameError.Length ? ErrorTooLarge : ErrorRefused);
            return; // the staged frame stays for the guest to re-stage; nothing is submitted
        }
        var receipt = services.Submit(CardId, ExchangeServiceId, frame, masterTick);
        switch (receipt.State)
        {
            case X0ServiceState.Pending:
                phase = Phase.Exchanging;
                error = ErrorNone;
                break;
            case X0ServiceState.Done:
                ApplyService(receipt);
                break;
            case X0ServiceState.Busy:
                Refuse(ErrorBusy);
                break;
            default:
                Refuse(ErrorUnknown);
                break;
        }
    }

    private void Cancel(long masterTick)
    {
        var receipt = services.Cancel(CardId, masterTick);
        switch (receipt.State)
        {
            case X0ServiceState.Cancelled:
                phase = Phase.Idle;
                error = ErrorNone;
                break;
            case X0ServiceState.TooLate:
                phase = Phase.Idle;
                Refuse(ErrorTooLate);
                break;
            default:
                Refuse(ErrorBusy);
                break;
        }
    }

    /// <summary>Apply a dispatcher receipt: Done with an encoded outcome stages the reply or
    /// latches the mapped error; TooLate is terminal and never replayed; Pending keeps the
    /// exchange pending.</summary>
    private void ApplyService(X0ServiceStatus receipt)
    {
        if (receipt.State == X0ServiceState.Pending) return;
        if (receipt.State == X0ServiceState.TooLate)
        {
            phase = Phase.Idle;
            Refuse(ErrorTooLate);
            return;
        }
        if (receipt.State != X0ServiceState.Done || receipt.Result is null)
        {
            phase = Phase.Idle;
            Refuse(ErrorUnknown);
            return;
        }
        if (!TryDecodeOutcome(receipt.Result, out var kind, out var code, out var reply))
        {
            phase = Phase.Idle;
            Refuse(ErrorUnknown);
            return;
        }
        switch (kind)
        {
            case 1 when code == ErrorNone:
                if (!CpNetFrame.TryParse(reply, out _, out _))
                {
                    // An accepted reply that is not one bounded record is ambiguous, not retryable.
                    phase = Phase.Idle;
                    Refuse(ErrorAmbiguous);
                    return;
                }
                reply.CopyTo(rx, 0);
                rxCount = reply.Length;
                rxCursor = 0;
                phase = Phase.RxPending;
                error = ErrorNone;
                break;
            case 2:
                phase = Phase.Idle;
                Refuse(code is ErrorBusy or ErrorTooLarge or ErrorNoTarget ? code : ErrorRefused);
                break;
            case 3:
                phase = Phase.Idle;
                Refuse(ErrorAmbiguous);
                break;
            default:
                phase = Phase.Idle;
                Refuse(ErrorUnknown);
                break;
        }
    }

    private void MapDmaRefusal()
    {
        var status = dma.Status(CardId);
        if (status.State is X0DmaState.Queued or X0DmaState.Programming or X0DmaState.Idle)
        {
            error = ErrorNone;
            return;
        }
        error = status.Refusal is X0DmaRefusal.OutOfRange or X0DmaRefusal.ReservationFailed
            ? ErrorTooLarge
            : ErrorRefused;
    }

    private byte DrainReplyByte()
    {
        var value = rx[rxCursor++];
        if (rxCursor >= rxCount)
        {
            phase = Phase.Idle;
            rxCount = 0;
            rxCursor = 0;
        }
        return value;
    }

    private void Refuse(byte code) => error = code;

    /// <summary>Complete bounded card state for the v2 model. The DMA channel state and the
    /// dispatcher receipt travel with the ExpansionX0 component; the v1 CpNetCardSnapshot
    /// stays valid for the v1 card, and a v2 save is never reinterpreted as v1.</summary>
    public CpNetX0CardSnapshot TakeSnapshot() => new(
        Version, (byte)phase, rxCursor, rx.AsSpan(0, rxCount).ToArray(), rxCount, error);

    public bool Restore(CpNetX0CardSnapshot snapshot)
    {
        if (snapshot is null || snapshot.Model != Version || snapshot.Phase is > (byte)Phase.RxPending)
            return false;
        if (snapshot.RxBytes is null || snapshot.RxCount < 0 ||
            snapshot.RxBytes.Length != snapshot.RxCount || snapshot.RxCount > CpNetFrame.MaxFrameBytes)
            return false;
        if (snapshot.Cursor < 0 || (snapshot.RxCount > 0 && snapshot.Cursor >= snapshot.RxCount)) return false;
        if (snapshot.Error > ErrorTooLate) return false;
        if (snapshot.Phase == (byte)Phase.RxPending ? snapshot.RxCount == 0 : snapshot.RxCount != 0 || snapshot.Cursor != 0) return false;
        Array.Clear(rx, 0, rx.Length);
        snapshot.RxBytes.CopyTo(rx, 0);
        rxCount = snapshot.RxCount;
        rxCursor = snapshot.Cursor;
        phase = (Phase)snapshot.Phase;
        error = snapshot.Error;
        descriptorCount = phase == Phase.Programming ? dma.DescriptorBytesStaged(CardId) : 0;
        return true;
    }

    // ---- Outcome encoding (owner-thread, bounded) ----

    private const byte OutcomeAccepted = 1, OutcomeRefused = 2, OutcomeAmbiguous = 3;

    /// <summary>Encode a peer result for the dispatcher receipt: kind, code, then the reply.</summary>
    public static byte[] EncodeOutcome(CpNetPeerResult result)
    {
        if (result.Ambiguous)
        {
            return [OutcomeAmbiguous, ErrorAmbiguous];
        }
        if (!result.Accepted || result.Response is null)
        {
            return [OutcomeRefused, result.Code];
        }
        var outcome = new byte[2 + result.Response.Length];
        outcome[0] = OutcomeAccepted;
        outcome[1] = ErrorNone;
        result.Response.CopyTo(outcome, 2);
        return outcome;
    }

    /// <summary>Decode a dispatcher receipt written by EncodeOutcome (or the live gateway's
    /// Deliver). Unknown kinds are refused, never guessed.</summary>
    public static bool TryDecodeOutcome(ReadOnlySpan<byte> receipt, out byte kind,
        out byte code, out byte[] reply)
    {
        kind = 0; code = ErrorNone; reply = [];
        if (receipt.Length < 2) return false;
        kind = receipt[0];
        code = receipt[1];
        if (kind is > OutcomeAmbiguous) return false;
        if (kind == OutcomeAccepted)
        {
            reply = receipt.Slice(2).ToArray();
            return reply.Length is > 0 and <= CpNetFrame.MaxFrameBytes;
        }
        if (kind == OutcomeRefused && code is ErrorBusy or ErrorTooLarge or ErrorEmpty or ErrorRefused
            or ErrorNoTarget or ErrorUnknown) return true;
        if (kind == OutcomeAmbiguous) return true;
        return false;
    }

    /// <summary>Live gateway seam (host-session owned, off by default): the composition's
    /// exchange handler calls Begin at submission with the frame copy, and the gateway
    /// delivers the encoded outcome later through the dispatcher's Complete. The profile
    /// never connects by itself; the card performs no host I/O.</summary>
    public interface ICpNetGateway
    {
        void Begin(ReadOnlySpan<byte> frame, long deadlineTicks);
        void Deliver(ReadOnlySpan<byte> encodedOutcome);
    }
}

/// <summary>Complete bounded card state for the v2 model (version 2 = CpNetX0Card.Version).</summary>
public sealed record CpNetX0CardSnapshot(byte Model, byte Phase, int Cursor, byte[] RxBytes, int RxCount, byte Error);

/// <summary>
/// The card's DMA endpoint (IX0DmaEndpoint): one bounded 261-byte frame buffer. The card only
/// receives frames by DMA (guest RAM to card); it never sources a DMA transfer, so a
/// card-to-RAM admission is refused by the engine as a failed reservation. The staging bytes
/// travel with the channel snapshot through ExpansionX0.
/// </summary>
public sealed class CpNetX0Endpoint : IX0DmaEndpoint
{
    private const int MaxFrame = CpNetFrame.MaxFrameBytes;
    private readonly byte[] staging = new byte[CpNetFrame.MaxFrameBytes];
    private int count;

    public X0DmaCardIdentity Identity { get; } = new(CpNetX0Card.CardId, 1);

    /// <summary>The staging bytes are bounded card state; the live external state is the
    /// dispatcher's pending service, which ExpansionX0.CaptureState already refuses.</summary>
    public bool AllowsOfflineSnapshot => true;

    public bool TryAdmit(uint length) => length is > 0 and <= (uint)MaxFrame;

    public byte[] CaptureSource(uint length) => []; // this card never sources a DMA transfer

    public void Commit(byte[] data)
    {
        data.CopyTo(staging, 0);
        count = data.Length;
    }

    public void CancelReservation() { }

    public byte[] CaptureSnapshotState()
    {
        var state = new byte[4 + count];
        BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(), (uint)count);
        staging.AsSpan(0, count).CopyTo(state.AsSpan(4));
        return state;
    }

    /// <summary>Same bytes RestoreSnapshotState accepts. X5 refuses an adopt unless this is true;
    /// the default would reject every CP/NET channel, including an idle one.</summary>
    public bool CanRestoreSnapshotState(byte[] state) =>
        state is { Length: >= 4 } &&
        BinaryPrimitives.ReadUInt32LittleEndian(state) is var saved &&
        saved <= (uint)MaxFrame && state.Length >= 4 + saved;

    public void RestoreSnapshotState(byte[] state)
    {
        if (!CanRestoreSnapshotState(state))
            throw new ArgumentException("CP/NET endpoint snapshot is out of range.");
        count = (int)BinaryPrimitives.ReadUInt32LittleEndian(state);
        state.AsSpan(4, count).CopyTo(staging.AsSpan(0, count));
    }

    internal int Count => count;
    internal ReadOnlySpan<byte> Staged => staging.AsSpan(0, count);

    internal void Discard() => count = 0;
}
