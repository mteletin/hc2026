using Hc2000.Machine.Io;

namespace Hc2000.Machine.Network;

/// <summary>
/// Bounded CP/NET 1.2 message card (R7 Option C, hc2000-architecture-expansion-x0-x5 v12):
/// one outstanding request, one TX staging slot and one RX reply slot, each at most 261 bytes.
/// The card performs no host I/O; it submits bounded frames through an <see cref="ICpNetPeer"/>
/// owned by the App session (offline by default, host gateway opt-in). All endpoints are exact
/// 16-bit ports admitted to the main CPU only; wrong or absent origins float 0xFF/no-op.
/// </summary>
public sealed class CpNetCard : IPortDevice
{
    public const ushort DataPort = 0x719d, CommandPort = 0x729d, StatusPort = 0x739d, VersionPort = 0x749d;
    public const byte VersionValue = 0x01;
    public const int MaxFrameBytes = CpNetFrame.MaxFrameBytes;

    public const byte CommandNoOp = 0x00, CommandTxBegin = 0x01, CommandTxCommit = 0x02,
        CommandCancel = 0x03, CommandErrorClear = 0x04;

    public const byte StatusTxReady = 0x01, StatusRxAvailable = 0x02, StatusError = 0x04;
    public const byte ErrorNone = 0x00, ErrorBusy = 0x01, ErrorTooLarge = 0x02, ErrorEmpty = 0x03,
        ErrorRefused = 0x04, ErrorNoTarget = 0x05, ErrorUnknown = 0x06, ErrorAmbiguous = 0x07;

    private enum State { Idle, Staged, RxPending }

    private readonly ICpNetPeer peer;
    private readonly byte[] tx = new byte[MaxFrameBytes];
    private readonly byte[] rx = new byte[MaxFrameBytes];
    private int txCount, rxCount, rxCursor;
    private bool txOverflowed;
    private State state;
    private byte error;

    public CpNetCard(ICpNetPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        this.peer = peer;
    }

    public IReadOnlyList<PortMapping> Mappings() =>
    [
        new(DataPort, 0xffff, this, PortAccess.ReadWrite),
        new(CommandPort, 0xffff, this, PortAccess.Write),
        new(StatusPort, 0xffff, this, PortAccess.Read),
        new(VersionPort, 0xffff, this, PortAccess.Read)
    ];

    /// <summary>Read-only status; it never starts traffic or acknowledges work.</summary>
    public byte Read(ushort port, long masterTick)
    {
        return port switch
        {
            DataPort when state == State.RxPending && rxCursor < rxCount => DrainReplyByte(),
            StatusPort => (byte)((state == State.Idle ? StatusTxReady : 0) |
                (state == State.RxPending && rxCursor < rxCount ? StatusRxAvailable : 0) |
                (error != ErrorNone ? StatusError : 0) | (error << 4)),
            VersionPort => VersionValue,
            _ => 0xff
        };
    }

    public void Write(ushort port, byte value, long masterTick)
    {
        if (port == DataPort)
        {
            if (state == State.Staged)
            {
                if (txCount < MaxFrameBytes) tx[txCount++] = value;
                else
                {
                    // Preserve the bounded prefix, but never submit a truncated request.
                    txOverflowed = true;
                    Refuse(ErrorTooLarge);
                }
            }
            return;
        }
        if (port != CommandPort) return;
        switch (value)
        {
            case CommandNoOp:
                break;
            case CommandTxBegin when state == State.Idle:
                txCount = 0; txOverflowed = false; state = State.Staged;
                break;
            case CommandTxBegin:
                Refuse(ErrorBusy);
                break;
            case CommandTxCommit:
                Commit(masterTick);
                break;
            case CommandCancel when state == State.Staged:
                txCount = 0; txOverflowed = false; state = State.Idle;
                break;
            case CommandErrorClear:
                error = ErrorNone;
                break;
            default:
                Refuse(ErrorUnknown);
                break;
        }
    }

    /// <summary>Validates the staged frame before any external submission; a refusal never
    /// discards the staged request. Only an accepted exchange or an ambiguous outcome leaves
    /// the Staged state, and a peer refusal leaves it so the guest may cancel and retry.</summary>
    private void Commit(long masterTick)
    {
        if (state is not State.Staged)
        {
            Refuse(ErrorBusy);
            return;
        }
        if (txOverflowed)
        {
            Refuse(ErrorTooLarge);
            return;
        }
        if (txCount == 0)
        {
            Refuse(ErrorEmpty);
            return;
        }
        if (!CpNetFrame.TryParse(tx.AsSpan(0, txCount), out _, out var frameError))
        {
            Refuse(frameError == CpNetFrameError.Length && txCount > MaxFrameBytes ? ErrorTooLarge : ErrorRefused);
            return;
        }
        var result = peer.Exchange(tx.AsSpan(0, txCount), masterTick);
        // An accepted request may already have had effects. A malformed accepted reply
        // is therefore ambiguous, not retryable; never trust the peer's factory usage.
        if (result.Ambiguous || (result.Accepted &&
            (result.Response is null || !CpNetFrame.TryParse(result.Response, out _, out _))))
        {
            txCount = 0; rxCount = 0; state = State.Idle;
            Refuse(ErrorAmbiguous);
            return;
        }
        if (!result.Accepted)
        {
            Refuse(result.Code is ErrorNoTarget or ErrorRefused ? result.Code : ErrorRefused);
            return;
        }
        txCount = 0;
        rxCursor = 0;
        rxCount = result.Response!.Length;
        result.Response.CopyTo(rx, 0);
        state = State.RxPending;
        error = ErrorNone;
    }

    /// <summary>Pop one reply byte; draining the last byte returns the card to Idle.</summary>
    private byte DrainReplyByte()
    {
        var value = rx[rxCursor++];
        if (rxCursor >= rxCount) state = State.Idle;
        return value;
    }

    private void Refuse(byte code) => error = code;

    /// <summary>Complete bounded card state; a restore is pure card work and touches no host facts.</summary>
    public CpNetCardSnapshot TakeSnapshot() => new(
        1, (byte)state, tx.AsSpan(0, txCount).ToArray(), txCount,
        rx.AsSpan(rxCursor, rxCount - rxCursor).ToArray(), rxCount - rxCursor, error, txOverflowed);

    public bool Restore(CpNetCardSnapshot snapshot)
    {
        if (snapshot is null || snapshot.Version != 1 || snapshot.State is > (byte)State.RxPending) return false;
        if (snapshot.TxBytes is null || snapshot.TxCount < 0 ||
            snapshot.TxBytes.Length != snapshot.TxCount || snapshot.TxCount > MaxFrameBytes) return false;
        if (snapshot.RxBytes is null || snapshot.RxCount < 0 ||
            snapshot.RxBytes.Length != snapshot.RxCount || snapshot.RxCount > MaxFrameBytes) return false;
        if (snapshot.Error > ErrorAmbiguous) return false;
        if (snapshot.State != (byte)State.Staged && snapshot.TxCount != 0) return false;
        if ((snapshot.State == (byte)State.RxPending) != (snapshot.RxCount > 0)) return false;
        if (snapshot.TxOverflowed && (snapshot.State != (byte)State.Staged || snapshot.TxCount != MaxFrameBytes)) return false;
        snapshot.TxBytes.CopyTo(tx, 0);
        txCount = snapshot.TxCount;
        txOverflowed = snapshot.TxOverflowed;
        snapshot.RxBytes.CopyTo(rx, 0);
        rxCursor = 0;
        rxCount = snapshot.RxCount;
        state = (State)snapshot.State;
        error = snapshot.Error;
        return true;
    }
}

/// <summary>The host boundary of the card. Machine and ordinary tests supply offline peers;
/// the live host gateway is an App-session object, opt-in, default off.</summary>
public interface ICpNetPeer
{
    bool Live { get; }
    CpNetPeerResult Exchange(ReadOnlySpan<byte> request, long masterTick);
}

public readonly record struct CpNetPeerResult(bool Accepted, bool Ambiguous, byte Code, byte[]? Response)
{
    public static CpNetPeerResult Accept(ReadOnlySpan<byte> response) =>
        new(true, false, CpNetCard.ErrorNone, response.ToArray());

    public static CpNetPeerResult Refuse(byte code) => new(false, false, code, null);

    public static CpNetPeerResult AmbiguousOutcome() => new(false, true, CpNetCard.ErrorAmbiguous, null);

    /// <summary>Accept validates the reply as one bounded CP/NET record before adoption.</summary>
    public static CpNetPeerResult TryAccept(ReadOnlySpan<byte> response) =>
        CpNetFrame.TryParse(response, out _, out _) ? Accept(response) : Refuse(CpNetCard.ErrorRefused);
}

/// <summary>Complete bounded card snapshot (R7: offline card snapshots need complete bounded
/// state and no host handles/targets). Version 1.</summary>
public sealed record CpNetCardSnapshot(int Version, byte State, byte[] TxBytes, int TxCount,
    byte[] RxBytes, int RxCount, byte Error, bool TxOverflowed = false);

/// <summary>Default card boundary: no host target configured, so nothing is transmitted.</summary>
public sealed class NoTargetCpNetPeer : ICpNetPeer
{
    public bool Live => false;
    public CpNetPeerResult Exchange(ReadOnlySpan<byte> request, long masterTick) =>
        CpNetPeerResult.Refuse(CpNetCard.ErrorNoTarget);
}
