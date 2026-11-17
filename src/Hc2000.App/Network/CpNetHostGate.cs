namespace Hc2000.App.Network;

// Lives with the tab session, not the worker. A replacement worker cannot reuse the host
// session while a predecessor's close or abandoned operation is still running.
internal sealed class CpNetHostGate
{
    internal enum GateState { Available, Active, Closing, CloseFailed }
    private readonly object sync = new();
    private GateState state;

    public GateState State { get { lock (sync) return state; } }

    public Claim? TryClaim()
    {
        lock (sync)
        {
            if (state != GateState.Available) return null;
            state = GateState.Active;
            return new(this);
        }
    }

    internal sealed class Claim(CpNetHostGate gate)
    {
        private int retired;
        private Task retirement = Task.CompletedTask;
        public Task Retirement => retirement;

        // Only valid when construction failed before a host resource was handed over.
        public void Abort()
        {
            if (Interlocked.Exchange(ref retired, 1) != 0) return;
            lock (gate.sync) gate.state = GateState.Available;
        }

        public void Retire(Task close, Task quiescence)
        {
            ArgumentNullException.ThrowIfNull(close);
            ArgumentNullException.ThrowIfNull(quiescence);
            if (Interlocked.Exchange(ref retired, 1) != 0) return;
            lock (gate.sync) gate.state = GateState.Closing;
            retirement = CompleteAsync(close, quiescence);
        }

        private async Task CompleteAsync(Task close, Task quiescence)
        {
            // A faulted abandoned exchange is still quiescent, but a failed close is not safe.
            try { await quiescence.ConfigureAwait(false); }
            catch { }
            bool closed;
            try { await close.ConfigureAwait(false); closed = true; }
            catch { closed = false; }
            lock (gate.sync) gate.state = closed ? GateState.Available : GateState.CloseFailed;
        }
    }
}
