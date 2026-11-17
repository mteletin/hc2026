using Hc2000.Machine.Expansion;
using Hc2000.Machine.Network;
using Hc2000.Machine.Timing;

namespace Hc2000.App.Network;

// Owner-thread bridge. Its owner must hold a snapshot lease until the external stream is
// actually closed, and must make onRetired a nonblocking close request, not a host I/O call.
internal sealed class CpNetIngress : IDisposable
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly DeviceScheduler scheduler;
    private readonly ExpansionServiceDispatcher services;
    private readonly CpNetExchangeSession session;
    private readonly Action onRetired;
    private readonly int deviceId;
    private bool waiting, retired, disposed;
    private long scheduledToken;
    private CpNetPeerResult scheduledResult;

    public CpNetIngress(DeviceScheduler scheduler, ExpansionServiceDispatcher services,
        CpNetExchangeSession session, int deviceId, Action onRetired)
    {
        this.scheduler = scheduler;
        this.services = services;
        this.session = session;
        this.deviceId = deviceId;
        this.onRetired = onRetired;
        scheduler.RegisterDevice(deviceId, Deliver);
    }

    public X0ServiceOutcome Submit(in X0ServiceCall call, long masterTick)
    {
        VerifyOwner();
        if (waiting && services.Poll(CpNetX0Card.CardId, masterTick).State != X0ServiceState.Pending)
            Retire();
        if (retired || disposed) return Refuse(CpNetX0Card.ErrorNoTarget);
        if (waiting) return Refuse(CpNetX0Card.ErrorBusy);
        if (masterTick > long.MaxValue - CpNetX0Card.ExchangeDeadlineTicks ||
            !CpNetFrame.TryParse(call.Args, out var request, out _) || request!.Format != 0)
            return Refuse(CpNetX0Card.ErrorRefused);
        if (!session.TryBegin(request)) return Refuse(CpNetX0Card.ErrorBusy);
        waiting = true;
        return X0ServiceOutcome.WorkDue(CpNetX0Card.ExchangeDeadlineTicks);
    }

    // Called at a serialized instruction boundary. Host completion does not advance machine time.
    public void Pump()
    {
        VerifyOwner();
        if (retired || disposed || !waiting) return;
        var status = services.Poll(CpNetX0Card.CardId, scheduler.Now);
        if (status.State != X0ServiceState.Pending) { Retire(); return; }
        if (scheduledToken != 0 || !session.TryTake(out var result)) return;
        if (scheduler.Now == long.MaxValue || scheduler.Now + 1 >= status.Deadline) { Retire(); return; }
        scheduledResult = result;
        scheduledToken = scheduler.Schedule(new(scheduler.Now + 1, EventPhase.ExternalInput,
            deviceId, 0, scheduler.Generation(deviceId)));
    }

    private void Deliver(DeviceEvent item)
    {
        scheduledToken = 0;
        if (retired || disposed || !waiting) return;
        var status = services.Poll(CpNetX0Card.CardId, item.DueTick);
        if (status.State != X0ServiceState.Pending) { Retire(); return; }
        services.Complete(CpNetX0Card.CardId, CpNetX0Card.ExchangeServiceId,
            CpNetX0Card.EncodeOutcome(scheduledResult), item.DueTick);
        waiting = false;
        if (scheduledResult.Ambiguous) Retire();
    }

    private static X0ServiceOutcome Refuse(byte code) =>
        X0ServiceOutcome.Completed(CpNetX0Card.EncodeOutcome(CpNetPeerResult.Refuse(code)));

    private void Retire()
    {
        if (retired) return;
        retired = true;
        if (scheduledToken != 0) { scheduler.Cancel(scheduledToken); scheduledToken = 0; }
        session.Disconnect();
        onRetired();
    }

    public void Dispose()
    {
        VerifyOwner();
        if (disposed) return;
        Retire();
        scheduler.UnregisterDevice(deviceId);
        disposed = true;
    }

    private void VerifyOwner()
    {
        if (owner != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("CP/NET ingress belongs to the emulation owner.");
    }
}
