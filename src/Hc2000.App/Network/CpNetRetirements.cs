using LiveMachine = Hc2000.Machine.Machine;

namespace Hc2000.App.Network;

internal interface ICpNetRetiringRuntime
{
    bool HasExternalState { get; }
    void Stop();
    void Pump();
}

// Keeps a retiring transport's external-state guard across machine replacement. A failed
// close remains guarded; it never makes the replacement machine safe merely by resetting.
internal sealed class CpNetRetirements
{
    private sealed class Entry(ICpNetRetiringRuntime runtime)
    {
        public ICpNetRetiringRuntime Runtime { get; } = runtime;
        public IDisposable? Guard { get; set; }
    }

    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly List<Entry> entries = [];
    // Retired machines whose RAM a closing runtime may still read: each goes back to the pool once no runtime is closing
    // (hc2000-ram-one-buffer).
    private readonly List<LiveMachine> waiting = [];

    /// <summary>Disposes a retired machine, so its RAM goes back to the pool: at once when no retiring runtime is still
    /// closing, else as soon as <see cref="Pump"/> sees them all closed.</summary>
    public void DisposeWhenClosed(LiveMachine machine)
    {
        VerifyOwner();
        if (entries.Count == 0) machine.Dispose();
        else waiting.Add(machine);
    }

    /// <summary>At stop no next machine pumps the line: pump it here until every retiring runtime has closed or
    /// <paramref name="timeout"/> passes. Returns the machines still waiting, whose RAM a runtime may still read.</summary>
    public IReadOnlyList<LiveMachine> Drain(TimeSpan timeout)
    {
        VerifyOwner();
        var until = DateTime.UtcNow + timeout;
        Pump();
        while (waiting.Count > 0 && DateTime.UtcNow < until)
        {
            Thread.Sleep(10);
            Pump();
        }
        LiveMachine[] left = [.. waiting];
        waiting.Clear();
        return left;
    }

    public void Retire(ICpNetRetiringRuntime? runtime)
    {
        VerifyOwner();
        if (runtime is null) return;
        runtime.Stop();
        if (runtime.HasExternalState) entries.Add(new(runtime));
    }

    public void Bind(LiveMachine machine, CpNetRuntime? runtime, CpNetInboundRuntime? inbound = null)
    {
        VerifyOwner();
        var guards = new List<IDisposable>();
        try
        {
            foreach (var entry in entries) guards.Add(machine.AcquireExternalState());
        }
        catch
        {
            foreach (var guard in guards) guard.Dispose();
            throw;
        }
        for (var index = 0; index < entries.Count; index++)
        {
            entries[index].Guard?.Dispose();
            entries[index].Guard = guards[index];
        }
        runtime?.SetStartGuard(() => !entries.Any(entry => entry.Runtime is CpNetRuntime));
        inbound?.SetStartGuard(() => !entries.Any(entry => entry.Runtime is CpNetInboundRuntime));
    }

    public void Pump()
    {
        VerifyOwner();
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            entry.Runtime.Pump();
            if (entry.Runtime.HasExternalState) continue;
            entry.Guard?.Dispose();
            entries.RemoveAt(index);
        }
        if (entries.Count > 0 || waiting.Count == 0) return;
        foreach (var machine in waiting) machine.Dispose();
        waiting.Clear();
    }

    private void VerifyOwner()
    {
        if (owner != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("CP/NET retirement belongs to the emulation owner.");
    }
}
