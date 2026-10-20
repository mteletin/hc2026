using System.Collections.Immutable;
using Avalonia.Input;
using Hc2000.App.Runtime;
using Hc2000.Machine.Input;

namespace Hc2000.App.Input;

public sealed record FrontendInputRecord(long Order, string Session, long HostTimestamp,
    long HostFrequency, string Stage, string Origin, Key? Key, KeyModifiers Modifiers,
    KeyMappingMode Mode, string Outcome, ImmutableArray<HcKey> Chord, long? QueueSequence);

public sealed record FrontendInputSnapshot(bool Enabled, string? Session, long Dropped,
    string Coverage, ImmutableArray<FrontendInputRecord> Records);

/// <summary>UI records/reads; runtime retirement may clear. Bounded volatile host facts, never guest-consumption evidence.</summary>
public sealed class FrontendInputEvidence
{
    public const string Coverage = "Adapter receipt and queue handoff only; host monotonic ticks, not native event creation or guest master time. No guest consumption/history before enable.";
    private readonly object gate = new();
    private readonly Queue<FrontendInputRecord> records = new();
    private bool enabled;
    private IHostClock? clock;
    private string? session;
    private Func<bool>? sessionIsCurrent;
    private long order;
    private long dropped;
    public int Capacity { get; }
    public bool Enabled { get { lock (gate) return ValidateSession() && enabled; } }

    public FrontendInputEvidence(int capacity = 256)
    {
        if (capacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }

    // The shared composition owner supplies its EXISTING host clock. No new clock or wait.
    public void BeginSession(string id, IHostClock existingClock, Func<bool>? isCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(existingClock);
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
            throw new ArgumentException("A bounded session identity is required.", nameof(id));
        lock (gate) { Disable(); session = id; clock = existingClock; sessionIsCurrent = isCurrent; }
    }
    public bool Enable()
    {
        lock (gate)
        {
            if (!ValidateSession() || clock is null || session is null) return false;
            enabled = true; return true;
        }
    }
    public void Clear() { lock (gate) { records.Clear(); order = 0; dropped = 0; } }
    public void Disable() { lock (gate) { enabled = false; Clear(); } }
    public void EndSession() { lock (gate) { Disable(); session = null; clock = null; sessionIsCurrent = null; } }
    public FrontendInputSnapshot Read()
    {
        lock (gate)
        {
            ValidateSession();
            return new(enabled, session, dropped, Coverage, records.ToImmutableArray());
        }
    }

    // Only a nonblocking atomic lifecycle check; never call UI or the live machine here.
    private bool ValidateSession()
    {
        if (sessionIsCurrent is null) return true;
        try { if (sessionIsCurrent()) return true; }
        catch { /* Diagnostic validation failure cannot affect guest input. */ }
        EndSession(); return false;
    }

    internal void Record(string stage, string origin, Key? key, KeyModifiers modifiers,
        KeyMappingMode mode, string outcome, KeyChord? chord = null, long? queueSequence = null)
    {
        lock (gate)
        {
            if (!enabled || !ValidateSession() || clock is null || session is null) return;
            // A diagnostic clock failure must never break input delivery or release a guest key.
            long now, frequency;
            try { now = clock.Now; frequency = clock.Frequency; }
            catch { Disable(); return; }
            if (frequency <= 0) { Disable(); return; }
            if (records.Count == Capacity) { records.Dequeue(); dropped++; }
            records.Enqueue(new(++order, session, now, frequency, stage, origin, key, modifiers,
                mode, outcome, chord?.Keys ?? ImmutableArray<HcKey>.Empty, queueSequence));
        }
    }
}
