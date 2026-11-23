using System.Buffers.Binary;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Expansion;

/// <summary>
/// X0 shared physical DMA (hc2000-shared-physical-dma-v1): one owner-thread engine, one channel
/// per registered card (at most 16), one outstanding transfer per channel, global nonpreemptive
/// FIFO by owner acceptance sequence, 16-KiB single-page segments, device-time paced completion
/// with an atomic segment commit.
/// </summary>

public enum X0DmaState { Idle = 0, Programming = 1, Queued = 2, Done = 3, Error = 4, Cancelled = 5, TooLate = 6 }

public enum X0DmaTrigger { Begin = 1, Start = 2, Cancel = 3, Acknowledge = 4 }

public static class X0DmaDirection
{
    public const byte RamToCard = 1;
    public const byte CardToRam = 2;
}

public enum X0DmaRefusal
{
    None = 0, InvalidDescriptor = 1, Busy = 2, InvalidState = 3, StaleEndpoint = 4,
    OutOfRange = 5, RangeOverlap = 6, ReservationFailed = 7, InvalidTime = 8, UnknownCard = 9,
    PageNotOwned = 10,
}

public sealed record X0DmaTimingPolicy(int SetupTicks, int RateNumerator, int RateDenominator)
{
    /// <summary>
    /// v1 policy: one positive rational tick per byte plus one setup tick. It is a versioned
    /// determinism policy, not a bandwidth claim.
    /// </summary>
    public static readonly X0DmaTimingPolicy V1 = new(1, 1, 1);

    public void Validate()
    {
        if (SetupTicks < 0 || SetupTicks > int.MaxValue / 2 || RateNumerator <= 0 || RateNumerator > int.MaxValue / 2 ||
            RateDenominator <= 0 || RateDenominator > int.MaxValue / 2)
            throw new ArgumentOutOfRangeException("policy", "DMA timing policy must be finite and positive.");
    }
}

/// <summary>Stable host-owned card identity. The epoch is host state, never guest authority.</summary>
public sealed record X0DmaCardIdentity(ushort CardId, ulong Epoch);

/// <summary>
/// A bounded, pure owner-thread reserve/capture/commit/cancel endpoint. Implementations carry
/// stable card/buffer identity and an epoch; they never perform side-effecting port reads, host
/// I/O or wall-clock work at admission or commit.
/// </summary>
public interface IX0DmaEndpoint
{
    X0DmaCardIdentity Identity { get; }

    /// <summary>False while the card holds live external state that cannot be snapshotted offline.</summary>
    bool AllowsOfflineSnapshot { get; }

    /// <summary>Reserve a bounded buffer for the admitted length. Must be pure and bounded.</summary>
    bool TryAdmit(uint length);

    /// <summary>Whether this endpoint supports the transfer direction before any reservation.</summary>
    bool SupportsDirection(byte direction) => true;

    /// <summary>Card-to-RAM only: hand over the immutable source bytes at admission.</summary>
    byte[] CaptureSource(uint length);

    /// <summary>RAM-to-card only: receive the committed data at the ordered completion event.</summary>
    void Commit(byte[] data);

    /// <summary>Release a provisional reservation without mutating transfer data.</summary>
    void CancelReservation();

    /// <summary>Release a successfully consumed card-to-RAM source reservation.</summary>
    void CompleteSource() => CancelReservation();

    byte[] CaptureSnapshotState();
    /// <summary>Purely validate a detached snapshot. False means this endpoint cannot safely
    /// restore it. When true, RestoreSnapshotState must accept the same bytes without failure.</summary>
    bool CanRestoreSnapshotState(byte[] state) => false;
    /// <summary>Validate the endpoint state together with its channel's detached transfer.</summary>
    bool CanRestoreTransfer(byte[] state, byte direction, uint count, bool pending) => CanRestoreSnapshotState(state);
    void RestoreSnapshotState(byte[] state);
}

public sealed record X0DmaStatus(X0DmaState State, X0DmaRefusal Refusal, uint Tag, ulong EndpointEpoch, long DueTick,
    bool Active = false);

public sealed partial class ExpansionDma
{
    public const int DescriptorBytes = 16;
    public const int MaxCards = 16;
    public const int PageBytes = 0x4000;
    public const int PageCount = 0x10000;
    public const int FirstDeviceId = 2000;
    private const int EventKind = 70;

    private sealed class Channel
    {
        public IX0DmaEndpoint Endpoint = null!;
        public int DeviceId;
        public ushort CardId;
        public ulong EndpointEpoch;
        public ulong PageEpoch;
        public X0DmaState State = X0DmaState.Idle;
        public byte[] Staging = new byte[DescriptorBytes];
        public int StagingCount;
        public bool HasRecord;
        public ushort Page, Offset;
        public uint Count, Tag;
        public byte Direction;
        public byte[]? Data;
        public long Token, DueTick, LastTick;
        public X0DmaRefusal Refusal = X0DmaRefusal.None;
        public byte[] EndpointState = [];
        public bool Settled;
        public int Sequence;
    }

    private readonly HcMemory memory;
    private readonly DeviceScheduler scheduler;
    private readonly X0DmaTimingPolicy policy;
    private readonly List<Channel> channels = new();

    public ExpansionDma(HcMemory memory, DeviceScheduler scheduler)
        : this(memory, scheduler, X0DmaTimingPolicy.V1)
    {
    }

    public ExpansionDma(HcMemory memory, DeviceScheduler scheduler, X0DmaTimingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(memory); ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        this.memory = memory; this.scheduler = scheduler; this.policy = policy;
    }

    public X0DmaTimingPolicy Policy => policy;

    /// <summary>Lent-page settlement (design contract 1): the arena's loan ends with the transfer -
    /// done, failed, cancelled, reset - so the lending unit can write the page again. The composition
    /// wires the arena's EndLentPage here; the engine only reports (cardId, page) and never decides.
    /// The arena callback ends the loan only if it matches, so reporting a page nobody lent is a no-op.</summary>
    public Action<ushort, ushort>? OnLentPageSettled { get; set; }

    /// <summary>Row interrupt (design contract 2): a finished transfer raises the dma-complete source
    /// with the channel's registration sequence (1-16); a failed or cancelled transfer is visible by
    /// the card's status, as today.</summary>
    public Action<int>? OnTransferDone { get; set; }

    internal bool CanRegisterCard(ushort cardId, int otherDeviceId, Hc2000.Machine.Memory.HcMemory expectedMemory,
        DeviceScheduler expectedScheduler)
    {
        if (!ReferenceEquals(memory, expectedMemory) || !ReferenceEquals(scheduler, expectedScheduler) || cardId == 0 ||
            channels.Count >= MaxCards || channels.Any(c => c.CardId == cardId) || otherDeviceId < 0) return false;
        var dmaDevice = FirstDeviceId + channels.Count;
        return otherDeviceId != dmaDevice && !scheduler.DeviceGenerations.Any(d => d.DeviceId == dmaDevice || d.DeviceId == otherDeviceId);
    }

    /// <summary>Register one capable card. The card id must be new; at most 16 cards.</summary>
    public int RegisterCard(IX0DmaEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Identity.CardId == 0 || endpoint.Identity.Epoch == 0)
            throw new ArgumentOutOfRangeException(nameof(endpoint), "DMA cards need a non-zero identity and epoch.");
        if (channels.Count >= MaxCards || channels.Any(c => c.CardId == endpoint.Identity.CardId))
            throw new InvalidOperationException("DMA card roster is full or the card id is taken.");
        var channel = new Channel
        {
            Endpoint = endpoint, CardId = endpoint.Identity.CardId, EndpointEpoch = endpoint.Identity.Epoch,
            DeviceId = FirstDeviceId + channels.Count, Sequence = channels.Count + 1,
        };
        scheduler.RegisterDevice(channel.DeviceId, e => Completion(channel, e));
        channels.Add(channel);
        return channel.DeviceId;
    }

    public bool HasCard(ushort cardId) => channels.Any(c => c.CardId == cardId);

    /// <summary>Whether any channel holds an admitted (queued) transfer from the page: the row
    /// controller refuses to lend or un-lend a page while a transfer on it is outstanding.</summary>
    public bool HasChannelOnPage(ushort page) =>
        channels.Any(c => c.State == X0DmaState.Queued && c.Page == page);

    public IReadOnlyList<int> DeviceIds => channels.Select(c => c.DeviceId).ToArray();

    public X0DmaStatus Status(ushort cardId)
    {
        var c = Find(cardId);
        return c is null ? new(X0DmaState.Idle, X0DmaRefusal.UnknownCard, 0, 0, 0)
            : new(c.State, c.Refusal, c.Tag, c.EndpointEpoch, c.DueTick, c.State == X0DmaState.Queued && c.Token != 0);
    }

    /// <summary>
    /// Stage one descriptor byte at the bounded cursor. Programming state only; writes from any
    /// other state change nothing.
    /// </summary>
    public X0DmaStatus WriteDescriptor(ushort cardId, byte value)
    {
        var c = Find(cardId);
        if (c is null) return new(X0DmaState.Idle, X0DmaRefusal.UnknownCard, 0, 0, 0);
        if (c.State != X0DmaState.Programming || c.StagingCount == DescriptorBytes)
        {
            c.Refusal = X0DmaRefusal.InvalidState;
        }
        else
        {
            c.Staging[c.StagingCount++] = value; c.Refusal = X0DmaRefusal.None;
        }
        return Status(cardId);
    }

    public X0DmaStatus Trigger(ushort cardId, X0DmaTrigger action, long masterTick)
    {
        var c = Find(cardId);
        if (c is null) return new(X0DmaState.Idle, X0DmaRefusal.UnknownCard, 0, 0, 0);
        switch (action)
        {
            case X0DmaTrigger.Begin:
                if (c.State is not X0DmaState.Idle)
                {
                    c.Refusal = X0DmaRefusal.InvalidState;
                }
                else
                {
                    Array.Clear(c.Staging); c.StagingCount = 0; c.Refusal = X0DmaRefusal.None;
                    c.State = X0DmaState.Programming;
                }
                break;
            case X0DmaTrigger.Start:
                Start(c, masterTick);
                break;
            case X0DmaTrigger.Cancel:
                if (masterTick < scheduler.Now) c.Refusal = X0DmaRefusal.InvalidTime;
                else Cancel(c, masterTick);
                break;
            case X0DmaTrigger.Acknowledge:
                if (c.State is X0DmaState.Done or X0DmaState.Error or X0DmaState.Cancelled or X0DmaState.TooLate)
                {
                    c.State = X0DmaState.Idle; c.Data = null; c.Refusal = X0DmaRefusal.None;
                    c.StagingCount = 0; c.HasRecord = false; c.Token = 0;
                }
                else c.Refusal = X0DmaRefusal.InvalidState;
                break;
            default:
                c.Refusal = X0DmaRefusal.InvalidState;
                break;
        }
        return Status(cardId);
    }

    private void Start(Channel c, long masterTick)
    {
        if (c.State != X0DmaState.Programming || c.StagingCount != DescriptorBytes)
        {
            c.Refusal = X0DmaState.Programming == c.State ? X0DmaRefusal.InvalidDescriptor : X0DmaRefusal.InvalidState;
            return;
        }
        var s = c.Staging;
        if (s[0] != 1 || s[2] != 0 || s[3] != 0 ||
            (s[1] != X0DmaDirection.RamToCard && s[1] != X0DmaDirection.CardToRam))
        {
            Fail(c, X0DmaRefusal.InvalidDescriptor, BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(12)));
            return;
        }
        var page = BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(4));
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(6));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(8));
        var tag = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(12));
        if (offset >= PageBytes || count == 0 || count > PageBytes - offset)
        {
            Fail(c, X0DmaRefusal.OutOfRange, tag);
            return;
        }
        if (!memory.TryDmaPageEpoch(c.CardId, s[1], page, offset, count, out var pageEpoch))
        {
            Fail(c, X0DmaRefusal.PageNotOwned, tag);
            return;
        }
        // Overlap with every outstanding admitted range, both directions, all cards.
        foreach (var other in channels.Where(o => o != c && o.State == X0DmaState.Queued))
            if (RangesOverlap(page, offset, count, other.Page, other.Offset, other.Count))
            {
                Fail(c, X0DmaRefusal.RangeOverlap, tag);
                return;
            }
        if (!MatchesIdentity(c))
        {
            Fail(c, X0DmaRefusal.StaleEndpoint, tag);
            return;
        }
        if (!c.Endpoint.SupportsDirection(s[1]))
        {
            Fail(c, X0DmaRefusal.ReservationFailed, tag);
            return;
        }
        if (masterTick < c.LastTick || masterTick < scheduler.Now)
        {
            Fail(c, X0DmaRefusal.InvalidTime, tag);
            return;
        }
        var delay = Delay(count);
        var tail = channels.Where(other => other.State == X0DmaState.Queued)
            .Select(other => other.DueTick).DefaultIfEmpty(masterTick).Max();
        var start = Math.Max(masterTick, tail);
        if (start > long.MaxValue - delay)
        {
            Fail(c, X0DmaRefusal.InvalidTime, tag);
            return;
        }
        var due = start + delay;
        if (due <= scheduler.Now)
        {
            Fail(c, X0DmaRefusal.InvalidTime, tag);
            return;
        }
        // Admission: capture the immutable source and reserve the endpoint before queueing.
        byte[] data;
        var reserved = false;
        try
        {
            if (!c.Endpoint.TryAdmit(count))
            {
                Fail(c, X0DmaRefusal.ReservationFailed, tag);
                return;
            }
            reserved = true;
            data = s[1] == X0DmaDirection.RamToCard
                ? memory.ExpansionArenaSpan(page, offset, checked((int)count)).ToArray()
                : c.Endpoint.CaptureSource(count);
            if (data?.Length != (int)count)
            {
                c.Endpoint.CancelReservation();
                Fail(c, X0DmaRefusal.ReservationFailed, tag);
                return;
            }
        }
        catch (ArgumentException)
        {
            if (reserved) c.Endpoint.CancelReservation();
            Fail(c, X0DmaRefusal.ReservationFailed, tag);
            return;
        }
        c.HasRecord = true; c.Page = page; c.PageEpoch = pageEpoch; c.Offset = offset; c.Count = count; c.Tag = tag;
        c.Direction = s[1]; c.Data = data; c.DueTick = due; c.LastTick = masterTick; c.Settled = false;
        c.EndpointState = c.Endpoint.CaptureSnapshotState();
        c.Token = 0;
        c.State = X0DmaState.Queued;
        c.Refusal = X0DmaRefusal.None;
        if (!channels.Any(other => other != c && other.State == X0DmaState.Queued && other.Token != 0))
            ScheduleActive(c, due);
    }

    private long Delay(uint count)
    {
        var scaled = checked((long)count * policy.RateNumerator);
        return Math.Max(1, checked(policy.SetupTicks + checked((scaled + policy.RateDenominator - 1) / policy.RateDenominator)));
    }

    private void ScheduleActive(Channel c, long due)
    {
        c.DueTick = due;
        c.Token = scheduler.Schedule(new(due, EventPhase.Controller, c.DeviceId, EventKind,
            scheduler.Generation(c.DeviceId)));
    }

    private void PromoteNext(long now)
    {
        var next = channels.Where(c => c.State == X0DmaState.Queued && c.Token == 0)
            .OrderBy(c => c.DueTick).FirstOrDefault();
        if (next is not null) ScheduleActive(next, checked(now + Delay(next.Count)));
    }

    private void Cancel(Channel c, long masterTick)
    {
        switch (c.State)
        {
            case X0DmaState.Queued:
                var wasActive = c.Token != 0;
                if (wasActive && !scheduler.Cancel(c.Token))
                {
                    c.State = X0DmaState.TooLate;
                    c.Refusal = X0DmaRefusal.None;
                    SettleLentPage(c);
                    return;
                }
                if (MatchesIdentity(c))
                {
                    c.Endpoint.CancelReservation();
                    c.State = X0DmaState.Cancelled;
                    c.Refusal = X0DmaRefusal.None;
                }
                else Fail(c, X0DmaRefusal.StaleEndpoint, c.Tag);
                c.Data = null; c.HasRecord = false; c.Token = 0;
                SettleLentPage(c);
                if (wasActive) PromoteNext(Math.Max(masterTick, scheduler.Now));
                break;
            case X0DmaState.Done or X0DmaState.TooLate:
                // The ordered completion already committed; cancellation is too late, no rollback.
                c.State = X0DmaState.TooLate;
                break;
            default:
                c.Refusal = X0DmaRefusal.InvalidState;
                break;
        }
    }

    private void Completion(Channel c, DeviceEvent e)
    {
        if (e.InsertionSequence != c.Token || c.State != X0DmaState.Queued || c.Data is null) return;
        if (!MatchesIdentity(c))
        {
            c.Data = null; c.Token = 0;
            Fail(c, X0DmaRefusal.StaleEndpoint, c.Tag);
            SettleLentPage(c);
            PromoteNext(scheduler.Now);
            return;
        }
        if (!memory.TryDmaPageEpoch(c.CardId, c.Direction, c.Page, c.Offset, c.Count, out var pageEpoch) || pageEpoch != c.PageEpoch)
        {
            c.Endpoint.CancelReservation();
            c.Data = null; c.Token = 0;
            Fail(c, X0DmaRefusal.PageNotOwned, c.Tag);
            SettleLentPage(c);
            PromoteNext(scheduler.Now);
            return;
        }
        // One atomic segment commit at the ordered completion event.
        if (c.Direction == X0DmaDirection.RamToCard) c.Endpoint.Commit(c.Data);
        else
        {
            memory.ExpansionArenaCopy(c.Page, c.Offset, c.Data);
            c.Endpoint.CompleteSource();
        }
        c.Data = null; c.Token = 0; c.State = X0DmaState.Done;
        c.Refusal = X0DmaRefusal.None;
        SettleLentPage(c);
        OnTransferDone?.Invoke(c.Sequence);
        PromoteNext(scheduler.Now);
    }

    /// <summary>Report this channel's admitted page as settled if the transfer has not reported it
    /// already: the loan (if any) ends with the transfer, and the match-based arena callback makes
    /// every non-lent page a no-op.</summary>
    private void SettleLentPage(Channel c)
    {
        if (c.Settled || OnLentPageSettled is null) return;
        c.Settled = true;
        OnLentPageSettled(c.CardId, c.Page);
    }

    private static bool MatchesIdentity(Channel c) =>
        c.Endpoint.Identity.CardId == c.CardId && c.Endpoint.Identity.Epoch == c.EndpointEpoch;

    private void Fail(Channel c, X0DmaRefusal refusal, uint tag)
    {
        c.State = X0DmaState.Error;
        c.Refusal = refusal;
        c.Tag = tag;
    }

    private Channel? Find(ushort cardId) => channels.FirstOrDefault(c => c.CardId == cardId);

    internal int DescriptorBytesStaged(ushort cardId) => Find(cardId)?.StagingCount ?? 0;

    private static bool RangesOverlap(ushort pageA, ushort offsetA, uint countA, ushort pageB, ushort offsetB, uint countB)
    {
        if (pageA != pageB) return false;
        var aStart = (long)offsetA; var aEnd = checked(aStart + countA);
        var bStart = (long)offsetB; var bEnd = checked(bStart + countB);
        return aStart < bEnd && bStart < aEnd;
    }

    internal sealed record ChannelInfo(ushort CardId, ulong EndpointEpoch, X0DmaState State, byte[] Staging,
        int StagingCount, bool HasRecord, ushort Page, ushort Offset, uint Count, uint Tag, byte Direction,
        long DueTick, long Token, X0DmaRefusal Refusal, byte[]? Data, byte[] EndpointState, bool Offline);

    internal ChannelInfo[] CaptureChannels()
    {
        if (channels.Any(c => !MatchesIdentity(c)))
            throw new InvalidOperationException("DMA card identity changed.");
        if (channels.Any(c => c.State == X0DmaState.Queued &&
            (!memory.TryDmaPageEpoch(c.CardId, c.Direction, c.Page, c.Offset, c.Count, out var epoch) || epoch != c.PageEpoch)))
            throw new InvalidOperationException("DMA page ownership changed.");
        return channels.Select(c => new ChannelInfo(c.CardId, c.EndpointEpoch, c.State,
            (byte[])c.Staging.Clone(), c.StagingCount, c.HasRecord, c.Page, c.Offset, c.Count, c.Tag,
            c.Direction, c.DueTick, c.Token, c.Refusal, c.Data, c.Endpoint.CaptureSnapshotState(),
            c.Endpoint.AllowsOfflineSnapshot)).ToArray();
    }

    internal void ValidateChannels(ExpansionX0.X0DmaChannelState[] states, long now, IReadOnlyList<DeviceEvent> events,
        Func<ExpansionX0.X0DmaChannelState, bool>? transferAllowed = null)
    {
        if (states.Length != channels.Count) throw new ArgumentException("DMA card roster changed.");
        for (var index = 0; index < channels.Count; index++)
        {
            var s = states[index]; var c = channels[index];
            if (s.CardId != c.CardId || s.EndpointEpoch != c.EndpointEpoch || !MatchesIdentity(c))
                throw new ArgumentException("DMA card identity.");
            if (!Enum.IsDefined((X0DmaState)s.State) || s.Staging is not { Length: DescriptorBytes } ||
                s.StagingCount < 0 || s.StagingCount > DescriptorBytes ||
                !Enum.IsDefined((X0DmaRefusal)s.Refusal)) throw new ArgumentException("DMA channel state.");
            var state = (X0DmaState)s.State;
            switch (state)
            {
                case X0DmaState.Idle:
                    if (s.StagingCount != 0 || s.HasRecord || s.Data is not null || s.Token != 0 ||
                        s.Refusal != (byte)X0DmaRefusal.None) throw new ArgumentException("DMA idle state.");
                    break;
                case X0DmaState.Programming:
                    if (s.HasRecord || s.Data is not null || s.Token != 0) throw new ArgumentException("DMA programming state.");
                    break;
                case X0DmaState.Queued:
                    if (!s.HasRecord || s.Data is not { Length: > 0 and <= PageBytes } ||
                        s.Offset >= PageBytes || s.Count == 0 || s.Count != (uint)s.Data.Length ||
                        s.Offset + s.Count > PageBytes ||
                        (s.Direction != X0DmaDirection.RamToCard && s.Direction != X0DmaDirection.CardToRam) ||
                        s.DueTick <= now || s.Token < 0) throw new ArgumentException("DMA queued state.");
                    if (!(transferAllowed?.Invoke(s) ?? memory.TryDmaPageEpoch(s.CardId, s.Direction, s.Page, s.Offset, s.Count, out _)))
                        throw new ArgumentException("DMA page ownership.");
                    break;
                case X0DmaState.Done or X0DmaState.Error or X0DmaState.Cancelled or X0DmaState.TooLate:
                    if (s.Data is not null || s.Token != 0) throw new ArgumentException("DMA terminal state.");
                    break;
                default:
                    throw new ArgumentException("DMA channel state.");
            }
            if (s.EndpointState is null ||
                state == X0DmaState.Queued && !c.Endpoint.SupportsDirection(s.Direction) ||
                !c.Endpoint.CanRestoreTransfer(s.EndpointState, s.Direction, s.Count, state == X0DmaState.Queued))
                throw new ArgumentException("DMA endpoint state.");
            var channelEvents = events.Where(e => e.DeviceId == c.DeviceId).ToArray();
            if (state == X0DmaState.Queued && s.Token != 0)
            {
                if (channelEvents.Length != 1 || channelEvents[0].Kind != EventKind ||
                    channelEvents[0].Phase != EventPhase.Controller || channelEvents[0].InsertionSequence != s.Token ||
                    channelEvents[0].DueTick != s.DueTick) throw new ArgumentException("DMA active event roster.");
            }
            else if (channelEvents.Length != 0) throw new ArgumentException("DMA inactive event roster.");
        }
        var queued = states.Where(s => (X0DmaState)s.State == X0DmaState.Queued).OrderBy(s => s.DueTick).ToArray();
        if (queued.Length != 0 && (queued[0].Token == 0 || queued.Count(s => s.Token != 0) != 1))
            throw new ArgumentException("DMA active transfer roster.");
        for (var i = 1; i < queued.Length; i++)
            if (queued[i].DueTick <= queued[i - 1].DueTick ||
                queued[i].DueTick - queued[i - 1].DueTick < Delay(queued[i].Count))
                throw new ArgumentException("DMA FIFO timing.");
        for (var i = 0; i < queued.Length; i++)
            for (var j = i + 1; j < queued.Length; j++)
                if (RangesOverlap(queued[i].Page, queued[i].Offset, queued[i].Count,
                    queued[j].Page, queued[j].Offset, queued[j].Count))
                    throw new ArgumentException("DMA overlapping transfers.");
    }

    internal void AdoptChannels(ExpansionX0.X0DmaChannelState[] states, long now, IReadOnlyList<DeviceEvent> events)
    {
        ValidateChannels(states, now, events);
        for (var index = 0; index < channels.Count; index++)
        {
            var s = states[index]; var c = channels[index];
            var state = (X0DmaState)s.State;
            c.State = state; c.Staging = (byte[])s.Staging.Clone(); c.StagingCount = s.StagingCount;
            c.HasRecord = s.HasRecord; c.Page = s.Page; c.Offset = s.Offset; c.Count = s.Count;
            c.Tag = s.Tag; c.Direction = s.Direction; c.DueTick = s.DueTick; c.Token = s.Token;
            c.Refusal = (X0DmaRefusal)s.Refusal; c.Data = s.Data;
            if (state == X0DmaState.Queued &&
                !memory.TryDmaPageEpoch(s.CardId, s.Direction, s.Page, s.Offset, s.Count, out c.PageEpoch))
                throw new InvalidOperationException("Validated DMA page ownership changed.");
            c.Endpoint.RestoreSnapshotState(s.EndpointState);
        }
    }

    /// <summary>
    /// Invalidate this engine's pending generations, drop uncommitted requests, preserve committed
    /// data. Committed bytes already live in RAM or the card endpoint.
    /// </summary>
    public void Reset()
    {
        foreach (var c in channels)
        {
            if (c.State == X0DmaState.Queued)
            {
                scheduler.Cancel(c.Token);
                if (MatchesIdentity(c)) c.Endpoint.CancelReservation();
                SettleLentPage(c);
            }
            scheduler.InvalidateDevice(c.DeviceId);
            c.State = X0DmaState.Idle; c.StagingCount = 0; c.Data = null; c.HasRecord = false;
            c.Refusal = X0DmaRefusal.None; c.Token = 0; c.DueTick = 0;
        }
    }
}
