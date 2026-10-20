using Hc2000.App.Runtime;
using Hc2000.HostFiles;
using LiveMachine = Hc2000.Machine.Machine;

namespace Hc2000.App.Media;

// A native read holder only: selected-volume parsing and bounded copying stay with the existing caller.
internal sealed class HddColdAdmission : IDisposable
{
    private readonly object sync = new();
    private readonly NativeImageAdmission admission;
    private readonly AdmittedImageFacts facts;
    private readonly Guid candidate = Guid.NewGuid();
    private PreparedBasicMachine? prepared;
    private LiveMachine? ownerMachine;
    private int ownerThread;
    private enum State { Reading, Prepared, Enqueued, Adopting, Terminal }
    private State state;
    internal FileStream Source { get; }

    private HddColdAdmission(NativeImageAdmission admission, FileStream source, AdmittedImageFacts facts)
    { this.admission = admission; Source = source; this.facts = facts; }

    internal static HddColdAdmission Open(string path, CancellationToken cancellation = default)
    {
        var result = NativeImageAdmission.TryAcquireColdRead(cancellation);
        var native = result.Admission ?? throw new AdmissionException(result.Refusal!.Value);
        FileStream? source = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            source = native.OpenColdSource(path);
            var facts = native.ValidateOpenedSource(source, path);
            return new(native, source, facts);
        }
        catch { source?.Dispose(); native.Dispose(); throw; }
    }
    internal void BindPrepared(PreparedBasicMachine value)
    {
        lock (sync)
        {
            if (state != State.Reading) throw new InvalidOperationException("Read admission is already bound.");
            RequireReadOnlyPayload(value);
            admission.ValidateSourceStillCurrent(Source, facts);
            admission.MarkColdOwnerPending(candidate);
            prepared = value; state = State.Prepared;
        }
    }
    private static void RequireReadOnlyPayload(PreparedBasicMachine value)
    {
        if (value.InitialHdd.Length != 1 || !value.InitialHdd[0].WriteProtected ||
            value.InitialHdd[0].Bytes.IsDefault || value.InitialHdd[0].Bytes.Length != HddMediaAdapter.ImageLength)
            throw new InvalidOperationException("Admitted cold input must be one exact read-only 8 MiB image.");
    }
    internal void MarkEnqueued(PreparedBasicMachine value)
    {
        lock (sync)
        {
            if (state != State.Prepared || !ReferenceEquals(prepared, value))
                throw new InvalidOperationException("Cold candidate was reused or is not prepared.");
            state = State.Enqueued;
        }
    }
    internal void EnterOwner()
    {
        lock (sync)
        {
            if (state == State.Terminal) return; // Later Reset/HCS factories never repeat the initial adoption.
            if (state != State.Enqueued || ownerThread != 0)
                throw new InvalidOperationException("Cold owner was reused or not enqueued.");
            ownerThread = Environment.CurrentManagedThreadId;
        }
    }
    internal void RejectBeforeOwnerStarted()
    {
        lock (sync)
        {
            if (state == State.Terminal) return;
            if (state != State.Enqueued || ownerThread != 0)
                throw new InvalidOperationException("A started owner must acknowledge its own drain.");
            ReleasePending();
        }
    }
    internal void VerifyFreshOwnerCandidate(PreparedBasicMachine value)
    {
        lock (sync)
        {
            if (state is not (State.Prepared or State.Enqueued) || !ReferenceEquals(prepared, value) ||
                (ownerThread != 0 && ownerThread != Environment.CurrentManagedThreadId))
                throw new InvalidOperationException("Cold candidate is stale or belongs to another owner.");
            RequireReadOnlyPayload(value);
        }
    }
    internal void ValidateBeforeAdoption(PreparedBasicMachine value, LiveMachine machine)
    {
        lock (sync)
        {
            if (state is not (State.Prepared or State.Enqueued) || !ReferenceEquals(prepared, value) || machine.MasterTick != 0)
                throw new InvalidOperationException("Cold adoption requires the same detached never-executed candidate.");
            if (ownerThread != 0 && ownerThread != Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("Cold adoption must run on its actual owner.");
            RequireReadOnlyPayload(value);
            admission.ValidateColdOwner(candidate, Source, facts);
            ownerThread = Environment.CurrentManagedThreadId;
            ownerMachine = machine; state = State.Adopting;
        }
    }
    internal void AcknowledgeInstalled(PreparedBasicMachine value, LiveMachine machine, HddMediaAdapter? adapter)
    {
        lock (sync)
        {
            if (state == State.Terminal) return;
            if (state != State.Adopting || !ReferenceEquals(value, prepared) || !ReferenceEquals(ownerMachine, machine) ||
                machine.MasterTick != 0 || adapter is null || ownerThread != Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("Cold owner has not installed this candidate and media adapter.");
            admission.ValidateColdOwner(candidate, Source, facts);
            ReleasePending();
        }
    }
    internal void RejectAfterOwnerDrain()
    {
        lock (sync)
        {
            if (state == State.Terminal) return;
            if ((state is State.Enqueued or State.Adopting) && ownerThread != Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("Only the actual cold owner may acknowledge enqueued work.");
            if (state == State.Reading) { Source.Dispose(); admission.Dispose(); state = State.Terminal; }
            else ReleasePending();
        }
    }
    internal void CancelBeforeEnqueue()
    {
        lock (sync)
        {
            if (state is State.Enqueued or State.Adopting)
                throw new InvalidOperationException("Enqueued cold work requires acknowledgement from its actual owner.");
            RejectAfterOwnerDrain();
        }
    }
    private void ReleasePending()
    {
        // The owner adapters call only after synchronous work/callbacks are finished, never from cancelled await.
        Source.Dispose(); admission.CompleteColdOwnerAfterDrain(candidate); state = State.Terminal;
    }
    public void Dispose()
    {
        lock (sync)
        {
            // Prepared/enqueued/adopting candidates retain both source and native fence until explicit drain.
            if (state == State.Reading) CancelBeforeEnqueue();
        }
    }
}
