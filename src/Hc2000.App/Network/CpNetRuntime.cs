using Hc2000.Machine.Expansion;
using Hc2000.Machine.Network;
using Hc2000.Machine.Timing;
using LiveMachine = Hc2000.Machine.Machine;

namespace Hc2000.App.Network;

// Owner-thread session port, initially disconnected. Its caller owns the explicit target
// selection and supplies a close operation that completes only when the transport is closed.
internal sealed class CpNetRuntime(DeviceScheduler scheduler, ExpansionServiceDispatcher services, CpNetHostGate? hostGate = null) : ICpNetRetiringRuntime
{
    private const int IngressDeviceId = 32000;
    private readonly int owner = Environment.CurrentManagedThreadId;
    private LiveMachine? machine;
    private CpNetExchangeSession? session;
    private CpNetIngress? ingress;
    private IDisposable? lease;
    private Func<Task>? close;
    private Task? closing;
    private Task hostRetirement = Task.CompletedTask;
    private Func<bool>? canStart;
    private CpNetHostGate.Claim? claim;
    private bool guestAdmission = true;

    public bool HasExternalState { get { VerifyOwner(); return session is not null; } }
    public bool IsClosing { get { VerifyOwner(); return closing is not null; } }
    public bool CloseFailed { get { VerifyOwner(); return closing?.IsFaulted == true || closing?.IsCanceled == true; } }
    public bool GuestAdmissionAvailable { get { VerifyOwner(); return guestAdmission; } }
    public bool CanStart { get { VerifyOwner(); return guestAdmission && session is null && closing is null &&
        canStart?.Invoke() != false && (hostGate is null || hostGate.State == CpNetHostGate.GateState.Available); } }

    public void MarkRestored()
    {
        VerifyOwner();
        if (session is not null || closing is not null) throw new InvalidOperationException("Cannot restore a live CP/NET runtime.");
        guestAdmission = false;
    }

    public void SetStartGuard(Func<bool> guard)
    {
        VerifyOwner();
        canStart = guard ?? throw new ArgumentNullException(nameof(guard));
    }

    public void Bind(LiveMachine value)
    {
        VerifyOwner();
        if (machine is not null) throw new InvalidOperationException("CP/NET runtime already bound.");
        machine = value;
    }

    public bool Start(Func<CpNetFrame, CancellationToken, Task<CpNetFrame>> exchange, Func<Task> closeTransport)
    {
        VerifyOwner();
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(closeTransport);
        if (machine is null) throw new InvalidOperationException("CP/NET runtime is not bound.");
        if (!guestAdmission || session is not null || closing is not null || canStart?.Invoke() == false) return false;
        claim = hostGate?.TryClaim();
        if (hostGate is not null && claim is null) return false;
        IDisposable? acquired = null;
        try
        {
            acquired = machine.AcquireExternalState();
            session = new(exchange);
            close = closeTransport;
            ingress = new(scheduler, services, session, IngressDeviceId, BeginClose);
            lease = acquired;
            return true;
        }
        catch
        {
            session = null; close = null;
            acquired?.Dispose();
            claim?.Abort(); claim = null;
            throw;
        }
    }

    public X0ServiceOutcome Submit(in X0ServiceCall call, long masterTick)
    {
        VerifyOwner();
        guestAdmission = false;
        return ingress is not null && closing is null
            ? ingress.Submit(in call, masterTick)
            : X0ServiceOutcome.Completed(CpNetX0Card.EncodeOutcome(CpNetPeerResult.Refuse(CpNetX0Card.ErrorNoTarget)));
    }

    public void Pump()
    {
        VerifyOwner();
        if (closing is null) ingress?.Pump();
        if (closing is null) return;
        ingress?.Dispose();
        ingress = null;
        // A failed close must not make a live resource snapshot-safe.
        // The gate's async completion can lag the close/quiescence tasks. Do not publish a
        // drained runtime or release its snapshot lease until the host gate has also retired.
        if (!closing.IsCompletedSuccessfully || session?.Quiescence.IsCompleted != true || !hostRetirement.IsCompletedSuccessfully) return;
        lease!.Dispose();
        lease = null; session = null; close = null; closing = null;
    }

    public void Stop()
    {
        VerifyOwner();
        ingress?.Dispose();
        ingress = null;
        Pump();
    }

    private void BeginClose()
    {
        if (closing is not null) return;
        closing = Task.Run(close!);
        claim?.Retire(closing, session!.Quiescence);
        hostRetirement = claim?.Retirement ?? Task.CompletedTask;
        claim = null;
        _ = closing.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void VerifyOwner()
    {
        if (owner != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("CP/NET runtime belongs to the emulation owner.");
    }
}
