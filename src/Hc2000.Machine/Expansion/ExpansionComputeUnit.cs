using Hc2000.Z80;
using Hc2000.Z80.Frontier;

namespace Hc2000.Machine.Expansion;

public enum ExpansionLifecycle : byte { ResetStopped, Running, Paused, Halted, Faulted, ServiceIdle, WaitingMailbox }

/// <summary>A compute owns only its CPU, checked memory lease, value clocks and local mailbox.
/// In particular it has no owner/controller/scheduler, peer, card or Machine reference.</summary>
public sealed partial class ExpansionComputeUnit
{
    private readonly Z80.Z80 core = new(new NoBus());
    private ExpansionMemoryLease memory;
    internal readonly ExpansionMailbox Mailbox = new();
    private ExpansionMailboxEffect requestedMailboxEffect;
    private ExpansionLifecycle savedLifecycle;
    private long cpuAnchor, masterAnchor;
    public ushort Id { get; }
    public ExpansionLifecycle Lifecycle { get; private set; }
    public ulong ExecutionEpoch { get; private set; } = 1;
    public bool Runnable => Lifecycle is ExpansionLifecycle.Running or ExpansionLifecycle.Halted;
    public long LastMasterTick { get; private set; }
    public long CpuAnchor => cpuAnchor;
    public long MasterAnchor => masterAnchor;
    // The only admitted rational clock is 5/1; the denominator-one remainder is always zero.
    public long ClockRemainder => 0;
    public long NextMasterTick => checked(masterAnchor + checked((PeekNextFrontier().AtCpuTState - cpuAnchor) * 5));
    internal bool Retired => core.AtInstructionBoundary;
    internal bool Stable => Lifecycle is ExpansionLifecycle.ResetStopped or ExpansionLifecycle.Paused or
        ExpansionLifecycle.ServiceIdle or ExpansionLifecycle.WaitingMailbox;
    internal bool MutableImage => Retired && Lifecycle is ExpansionLifecycle.ResetStopped or ExpansionLifecycle.ServiceIdle;

    internal ExpansionComputeUnit(ushort id, ExpansionMemoryLease memory) { Id = id; this.memory = memory; }
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
        var result = core.CommitFrontier(value);
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
        cpuAnchor = core.TStates; masterAnchor = tick; LastMasterTick = tick;
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
        core.Reset(); Mailbox.Clear(); requestedMailboxEffect = ExpansionMailboxEffect.None;
        ExecutionEpoch = checked(ExecutionEpoch + 1);
        Lifecycle = savedLifecycle = ExpansionLifecycle.ResetStopped;
        cpuAnchor = 0; masterAnchor = LastMasterTick = tick;
    }
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

    private sealed class NoBus : IBus
    {
        public byte Read(ushort addr) => throw Forbidden();
        public void Write(ushort addr, byte value) => throw Forbidden();
        public byte In(ushort port) => throw Forbidden();
        public void Out(ushort port, byte value) => throw Forbidden();
        public void Contend(ushort addr, int tstates) => throw Forbidden();
        public byte IntVector() => throw Forbidden();
        private static InvalidOperationException Forbidden() => new("Compute effects belong to the machine owner.");
    }
}
