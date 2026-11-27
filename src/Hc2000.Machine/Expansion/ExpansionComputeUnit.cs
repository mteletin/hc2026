using Hc2000.Z80;
using Hc2000.Z80.Frontier;

namespace Hc2000.Machine.Expansion;

public enum ExpansionLifecycle : byte { ResetStopped, Running, Paused, Halted, Faulted, ServiceIdle, WaitingMailbox }

/// <summary>A compute owns only its CPU, checked memory lease, value clocks and local mailbox.
/// In particular it has no owner/controller/scheduler, peer, card or Machine reference.</summary>
public sealed partial class ExpansionComputeUnit
{
    private readonly UnitBus bus = new();
    private readonly Z80.Z80 core;
    private ExpansionMemoryLease memory;
    internal readonly ExpansionMailbox Mailbox = new();
    private ExpansionMailboxEffect requestedMailboxEffect;
    private ExpansionLifecycle savedLifecycle;
    private long cpuAnchor, masterAnchor;
    public ushort Id { get; }
    /// <summary>"compute" (fixed 64-KiB units 1-16) or "driver" (map-backed units 17-24).</summary>
    public string Kind { get; }
    public ExpansionLifecycle Lifecycle { get; private set; }
    public ulong ExecutionEpoch { get; private set; } = 1;
    public bool Runnable => Lifecycle is ExpansionLifecycle.Running or ExpansionLifecycle.Halted;
    public long LastMasterTick { get; private set; }
    public long CpuAnchor => cpuAnchor;
    public long MasterAnchor => masterAnchor;
    // Per-unit clock target (design contract 4): the faithful 3.5 MHz class by default, exactly 5/1
    // master ticks per T-state, so faithful units stay byte-identical to today's mapping. An explicit
    // target is the exact reduced rational rate with a persisted remainder - never a per-T-state
    // rounding; achieved throughput is metered in CommittedTStates and reported, not promised.
    private MainCpuSpeed clockTarget = MainCpuSpeed.Faithful;
    private long clockNumerator = 5, clockDenominator = 1, clockRemainder;
    public MainCpuSpeed ClockTarget => clockTarget;
    public long ClockNumerator => clockNumerator;
    public long ClockDenominator => clockDenominator;
    public long ClockRemainder => clockRemainder;
    public long CommittedTStates { get; private set; }
    public long NextMasterTick
    {
        get
        {
            // The committed T-states' exact time from the epoch anchor, the same ceil-boundary the
            // main CPU's mapper uses: two effects inside one master tick share the tick, and the
            // anchor (not a running sum) keeps the rate exact - no per-commit drift.
            var delta = checked(PeekNextFrontier().AtCpuTState - cpuAnchor);
            return checked(masterAnchor + (checked(delta * clockNumerator) + clockDenominator - 1) / clockDenominator);
        }
    }
    internal bool Retired => core.AtInstructionBoundary;
    internal bool Stable => Lifecycle is ExpansionLifecycle.ResetStopped or ExpansionLifecycle.Paused or
        ExpansionLifecycle.ServiceIdle or ExpansionLifecycle.WaitingMailbox;
    internal bool MutableImage => Retired && Lifecycle is ExpansionLifecycle.ResetStopped or ExpansionLifecycle.ServiceIdle;

    internal ExpansionComputeUnit(ushort id, ExpansionMemoryLease memory, string kind = "compute")
    { Id = id; this.memory = memory; Kind = kind; core = new(bus); }
    public FrontierState CaptureFrontier() => core.CaptureFrontier();
    public FrontierEffect PeekNextFrontier()
    {
        if (!Runnable) throw new InvalidOperationException("A stopped compute has no next scheduled effect.");
        return core.PeekNextFrontier();
    }
    public byte ReadMemory(ushort address) => memory.Read(address);
    public void WriteMemory(ushort address, byte value) => memory.Write(address, value);
    public FrontierCommit CommitFrontier(byte value, long masterTick)
    {
        if (masterTick != NextMasterTick) throw new InvalidOperationException("Compute clock/frontier mismatch.");
        var delta = checked(PeekNextFrontier().AtCpuTState - core.TStates);
        var result = core.CommitFrontier(value);
        clockRemainder = checked((checked(core.TStates - cpuAnchor) * clockNumerator)) % clockDenominator;
        CommittedTStates = checked(CommittedTStates + delta);
        LastMasterTick = masterTick;
        Lifecycle = core.Halted ? ExpansionLifecycle.Halted : ExpansionLifecycle.Running;
        if (result.Retired)
        {
            if (requestedMailboxEffect == ExpansionMailboxEffect.Idle) Lifecycle = ExpansionLifecycle.ServiceIdle;
            else if (requestedMailboxEffect == ExpansionMailboxEffect.Wait && !Mailbox.HasInbox)
                Lifecycle = ExpansionLifecycle.WaitingMailbox;
            requestedMailboxEffect = ExpansionMailboxEffect.None;
        }
        return result;
    }
    internal byte ReadLocal(ushort port) => port == 0x75fd ? Mailbox.ReadLocalByte() :
        Mailbox.ReadStatus(Lifecycle == ExpansionLifecycle.WaitingMailbox, Lifecycle == ExpansionLifecycle.ServiceIdle);
    internal void WriteLocal(ushort port, byte value)
    {
        if (port == 0x75fd) Mailbox.WriteLocalByte(value);
        else
        {
            var effect = Mailbox.WriteLocalControl(value);
            if (effect != ExpansionMailboxEffect.None) requestedMailboxEffect = effect;
        }
    }
    internal void Rebase(long tick)
    {
        if (tick < LastMasterTick) throw new ArgumentOutOfRangeException(nameof(tick));
        // A machine clock switch this unit could not take while it ran takes effect at its next fresh epoch.
        if (pendingFollow is { } follow) { pendingFollow = null; if (clockTarget == follow.From) ApplyClockTarget((byte)follow.To); }
        // The remainder belongs to the previous anchors; each rebase starts a fresh clock epoch.
        cpuAnchor = core.TStates; masterAnchor = tick; LastMasterTick = tick; clockRemainder = 0;
    }
    internal void Start(long tick) { Rebase(tick); Lifecycle = ExpansionLifecycle.Running; }
    internal void Pause(long tick)
    {
        savedLifecycle = Lifecycle; Rebase(tick); Lifecycle = ExpansionLifecycle.Paused;
    }
    internal void Resume(long tick)
    {
        Rebase(tick);
        Lifecycle = Lifecycle == ExpansionLifecycle.ServiceIdle ? ExpansionLifecycle.Running : savedLifecycle;
        if (Lifecycle == ExpansionLifecycle.WaitingMailbox && Mailbox.HasInbox) Lifecycle = ExpansionLifecycle.Running;
    }
    internal void Wake(long tick)
    {
        if (Lifecycle != ExpansionLifecycle.WaitingMailbox) return;
        Rebase(tick); Lifecycle = ExpansionLifecycle.Running;
    }
    internal void Reset(long tick, ExpansionMemoryLease? replacement = null)
    {
        if (replacement is not null) memory = replacement;
        openBurst = null;
        core.Reset(); Mailbox.Clear(); requestedMailboxEffect = ExpansionMailboxEffect.None;
        ExecutionEpoch = checked(ExecutionEpoch + 1);
        Lifecycle = savedLifecycle = ExpansionLifecycle.ResetStopped;
        cpuAnchor = 0; masterAnchor = LastMasterTick = tick;
        clockRemainder = 0; CommittedTStates = 0;
    }

    /// <summary>Set the unit's clock target (op 19): stable units only - a running unit keeps its
    /// epoch's ratio until its next rebase. The remainder starts fresh for the new denominator.</summary>
    internal void SetClockTarget(byte value)
    {
        pendingFollow = null; // the program's (or the composition's) own choice wins over a machine switch not yet taken
        ApplyClockTarget(value);
    }

    private void ApplyClockTarget(byte value)
    {
        var (numerator, denominator) = ((MainCpuSpeed)value).MasterTicksPerTState();
        clockTarget = (MainCpuSpeed)value; clockNumerator = numerator; clockDenominator = denominator;
        clockRemainder = 0;
    }

    // A machine clock switch (hc2000-speed-140-turbo-280) waiting for this unit's next fresh epoch. Host state: a
    // snapshot does not carry it.
    private (MainCpuSpeed From, MainCpuSpeed To)? pendingFollow;

    /// <summary>The machine's clock moved from <paramref name="from"/> to <paramref name="to"/> (the side Z80s follow the
    /// main by default): a unit on <paramref name="from"/> takes <paramref name="to"/> now when it is stable, else at its
    /// next fresh epoch (start, resume, wake, pause), unless a program sets its own clock first. A unit on another clock
    /// (one a program chose) keeps it.</summary>
    internal void FollowClock(MainCpuSpeed from, MainCpuSpeed to)
    {
        pendingFollow = null;
        if (clockTarget != from || from == to) return;
        if (Stable) ApplyClockTarget((byte)to);
        else pendingFollow = (from, to);
    }

    /// <summary>Managed-map denial (full-X5): the holder's access was refused; it stops at the
    /// uncommitted effect until a root reset clears the fault and restarts it.</summary>
    internal void Fault() => Lifecycle = ExpansionLifecycle.Faulted;
    internal void ReplaceImage(ushort offset, ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++) memory.Write(checked((ushort)(offset + i)), data[i]);
        ExecutionEpoch = checked(ExecutionEpoch + 1); Mailbox.Clear(); requestedMailboxEffect = ExpansionMailboxEffect.None;
    }
    internal void Fill(byte value)
    {
        for (var i = 0; i < 65536; i++) memory.Write((ushort)i, value);
        ExecutionEpoch = checked(ExecutionEpoch + 1); Mailbox.Clear(); requestedMailboxEffect = ExpansionMailboxEffect.None;
    }
    internal byte[] Copy(ushort offset, int count)
    {
        var copy = new byte[count];
        for (var i = 0; i < count; i++) copy[i] = memory.Read(checked((ushort)(offset + i)));
        return copy;
    }
    internal bool HasWork => Mailbox.HasWork || requestedMailboxEffect != ExpansionMailboxEffect.None;

}
