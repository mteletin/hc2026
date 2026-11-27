namespace Hc2000.Z80;

/// <summary>
/// Hooks for a host that runs a frontier-driven core's plain instructions in stretches (the expansion driver's relaxed
/// main quanta, hc2000-fast-emulation): another bus for the stretch, and a cheap checkpoint around one plain Step that
/// can be put back when the host refuses one of the instruction's accesses.
/// </summary>
public sealed partial class Z80
{
    /// <summary>The plain state one <see cref="Step"/> that accepts no interrupt can change.</summary>
    public readonly record struct StepCheckpoint(Registers Registers, long TStates, bool Halted, int EiDelay, bool LdAirPending);

    /// <summary>Points the core at <paramref name="next"/> and returns the bus it had. Only at an instruction boundary
    /// (no frontier instruction under way).</summary>
    public IBus SwapBus(IBus next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (frontierActive) throw new InvalidOperationException("A partial frontier instruction keeps its bus.");
        var previous = bus;
        bus = next;
        m1Bus = next as IM1FetchBus;
        var arrayBus = next as IArrayBus;
        fastMemory = arrayBus?.Memory;
        fastNoContend = arrayBus is { Contends: false } || next is IUncontendedBus;
        return previous;
    }

    /// <summary>The state before a plain <see cref="Step"/>, for <see cref="Rollback"/>.</summary>
    public StepCheckpoint Checkpoint() => new(R, TStates, Halted, eiDelay, lastLdAiOrR);

    /// <summary>Puts back the state <see cref="Checkpoint"/> took. Valid only for a Step that accepted no interrupt
    /// (the host's quantum never runs one with a line raised); the bus's own effects are the host's to undo.</summary>
    public void Rollback(in StepCheckpoint checkpoint)
    {
        R = checkpoint.Registers;
        TStates = checkpoint.TStates;
        Halted = checkpoint.Halted;
        eiDelay = checkpoint.EiDelay;
        lastLdAiOrR = checkpoint.LdAirPending;
    }
}
