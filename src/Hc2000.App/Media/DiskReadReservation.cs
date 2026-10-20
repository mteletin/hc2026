using System.Collections.Immutable;
using Hc2000.HostFiles;

namespace Hc2000.App.Media;

/// <summary>Host-held disk read admission, one bounded host operation at a time (contract
/// host-admission-configured-pair-632523). <see cref="Operation"/> is the parent: it claims the single
/// native cold-read fence BEFORE any file I/O and freezes the configured A/B membership (1-2 slots, a
/// prestart configured-start subset or a single panel insert). <see cref="Reservation"/> is the
/// per-member borrower: it owns the current-bytes read and the actual owner outcome, and never
/// reacquires or disposes the fence - only the parent does, exactly once. On guarded Linux the fence is
/// held from the claim through every member's real receipts, accounting and bind/no-bind outcomes, or a
/// proven owner drain (the HddColdAdmission hold model, not a read-only window: a cooperating external
/// publisher cannot acquire the freed gate between a read and the real owner outcome). The dispatch and a
/// confirmed outcome revalidate the file against the read facts with a fresh native same-file check (a
/// swapped or re-linked file throws InvalidDomain and cannot authorize an old mount); a stale outcome
/// refuses and returns no identity. A member that becomes terminal without a reply keeps the parent fence
/// until the host's named drain. Legacy hosts keep the plain bounded read and report no native identity.</summary>
internal static class DiskReadReservation
{
    internal sealed record HostFileIdentity(string CanonicalPath, NativeImageFileId FileId, long Length);

    internal enum Phase { Reading, Dispatched, Terminal }

    /// <summary>Per-member borrower: owns the current-bytes read and the actual owner outcome of one
    /// frozen disk of its parent operation. It carries no native admission and never disposes one: the
    /// parent <see cref="Operation"/> is the sole owner and sole disposer of the single fence, and a
    /// member terminal (confirmed or stale owner outcome, explicit refusal, host drain, or a pre-enqueue
    /// cancellation or Dispose of a still-Reading member) does not release it.</summary>
    internal sealed class Reservation
    {
        private readonly object sync = new();
        private readonly DiskSlot slot;
        private readonly string canonical;
        private readonly bool guarded;
        private AdmittedImageFacts? facts;
        private Phase phase = Phase.Reading;
        private ImmutableArray<byte>? bytes;

        internal Reservation(DiskSlot slot, string canonical, bool guarded)
        {
            this.slot = slot;
            this.canonical = canonical;
            this.guarded = guarded;
        }

        internal DiskSlot Slot => slot;
        internal string CanonicalPath => canonical;
        internal bool Guarded => guarded;
        internal Phase CurrentPhase { get { lock (sync) return phase; } }

        internal ImmutableArray<byte> Bytes
        {
            get
            {
                lock (sync)
                {
                    if (bytes is null) throw new InvalidOperationException("Reservation read failed or has not completed.");
                    return bytes.Value;
                }
            }
        }

        internal HostFileIdentity? Identity
        {
            get
            {
                if (!guarded) return null;
                return new HostFileIdentity(canonical, facts!.FileId, facts!.Length);
            }
        }

        // Records the validated source facts of the bounded read. Called once by the parent operation's
        // member read, before the read completes; the parent keeps the native fence, so the facts are the
        // stale-identity guard for the dispatch and outcome revalidation.
        internal void RecordFacts(AdmittedImageFacts facts)
        {
            lock (sync)
            {
                if (phase != Phase.Reading) throw new InvalidOperationException("Reservation read already completed or terminal.");
                if (this.facts is not null) throw new InvalidOperationException("Read facts already recorded.");
                this.facts = facts;
            }
        }

        internal void ReadCurrent(ImmutableArray<byte> bytes)
        {
            lock (sync)
            {
                if (phase != Phase.Reading) throw new InvalidOperationException("Reservation read already completed or terminal.");
                this.bytes = bytes;
            }
        }

        /// <summary>Revalidates the file against the read facts BEFORE the owner enqueue with a fresh native
        /// same-file check (a swapped or re-linked file throws InvalidDomain now, not as an old mount), then
        /// marks the reservation dispatched so only the owner outcome or a named drain can complete it. The
        /// parent operation keeps holding the native fence across the dispatch and the actual owner outcome;
        /// this member terminal releases nothing.</summary>
        internal void MarkDispatched()
        {
            lock (sync)
            {
                if (phase != Phase.Reading)
                    throw new InvalidOperationException("Reservation is already dispatched or terminal.");
                if (guarded)
                    CheckCurrent(facts!);
                phase = Phase.Dispatched;
            }
        }

        /// <summary>Acknowledge the actual owner outcome. A confirmed reply requires the file to still match the
        /// cold-read identity (a fresh same-file check; stale throws and cannot authorize current state); a
        /// refused or absent drive returns no identity. This member terminal releases no native fence: the
        /// parent operation retains the single fence through the bind/no-bind outcome until its own release -
        /// after every expected member is accounted, or by a named host drain. This is the only
        /// acknowledgement of a dispatched member besides an explicit refusal or a named drain.</summary>
        internal HostFileIdentity? CompleteAfterOwnerOutcome(MediaReply reply)
        {
            lock (sync)
            {
                if (phase != Phase.Dispatched)
                    throw new InvalidOperationException("Owner outcome requires a dispatched reservation.");
                var view = slot == DiskSlot.A ? reply.A : reply.B;
                var confirmed = reply.Error is null && view.Present;
                if (guarded)
                {
                    try
                    {
                        CheckCurrent(facts!);
                    }
                    catch (AdmissionException)
                    {
                        // The read facts no longer match the canonical path: the stale outcome cannot
                        // authorize current state. The fence stays held by the parent operation, which
                        // releases it once its expected members are accounted or by a named host drain.
                        phase = Phase.Terminal;
                        throw;
                    }
                }
                phase = Phase.Terminal;
                return confirmed ? Identity : null;
            }
        }

        // Fresh native same-file check against the read facts, no gate of its own: the parent operation
        // holds the single fence, and this check is the stale-identity guard for dispatch and outcome
        // revalidation. A swapped or re-linked file is a stale identity, never an old mount.
        private void CheckCurrent(AdmittedImageFacts expected)
        {
            var current = FileAt(canonical);
            if (current.Id != expected.FileId || current.Length != (ulong)expected.Length)
                throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
        }

        /// <summary>Fresh no-gate facts of the canonical path. Production hygiene first; when the read was
        /// admitted in the private test domain (a sticky-root ancestor such as /tmp, which production
        /// hygiene rejects), re-check under that hygiene instead. The identity compare is the stale
        /// protection in either case.</summary>
        private static LinuxImageAdmissionNative.Facts FileAt(string path)
        {
            try { return LinuxImageAdmissionNative.FileAt(path, false); }
            catch (AdmissionException ex) when (ex.Refusal == ImageAdmissionRefusal.InvalidDomain)
            { return LinuxImageAdmissionNative.FileAt(path, true); }
        }

        /// <summary>Explicit no-reply acknowledgement: the owner refused or the reply was lost, and the host
        /// completes the member by name instead of by cancellation. A member terminal releases no native
        /// fence; the parent operation retains the single fence until its own release. The bind/no-bind
        /// outcome the parent was holding the fence for is complete once the host has accounted it.</summary>
        internal void CompleteAfterRefusal()
        {
            lock (sync)
            {
                if (phase == Phase.Terminal) return;
                phase = Phase.Terminal;
            }
        }

        /// <summary>Host-lifecycle drain (window closed, worker completed, machine reset): complete the
        /// member with a named reason at the boundary, whether the read or the owner outcome was
        /// outstanding. The named drain is where the parent operation's fence release is proven.</summary>
        internal void DrainAtStop()
        {
            lock (sync)
            {
                if (phase == Phase.Terminal) return;
                phase = Phase.Terminal;
            }
        }

        /// <summary>Pre-enqueue cancellation only. Post-enqueue work must be acknowledged by its owner outcome
        /// or a named drain; cancellation of an await alone never releases pending work.</summary>
        internal void CancelBeforeEnqueue()
        {
            lock (sync)
            {
                if (phase == Phase.Dispatched)
                    throw new InvalidOperationException("A dispatched reservation must be acknowledged by its owner outcome or host drain, not by cancellation.");
                phase = Phase.Terminal;
            }
        }

        /// <summary>A still-Reading member is completed; a Dispatched one is untouched, so a disposed member
        /// can never silently complete pending work. No native fence is released here: the parent
        /// operation is its sole owner and sole disposer.</summary>
        public void Dispose()
        {
            lock (sync)
            {
                if (phase != Phase.Reading) return;
                phase = Phase.Terminal;
            }
        }
    }

    // Reads the already-opened, already-validated source so the dispatched bytes are the validated inode's bytes.
    private static async Task<ImmutableArray<byte>> ReadBoundedAsync(FileStream source)
    {
        if (source.Length > MediaFiles.MaxDiskBytes) throw new InvalidDataException("Media file exceeds the size limit.");
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int length;
        while ((length = await source.ReadAsync(buffer)) != 0)
        {
            if (output.Length + length > MediaFiles.MaxDiskBytes) throw new InvalidDataException("Media file exceeds the size limit.");
            output.Write(buffer, 0, length);
        }
        return ImmutableArray.CreateRange(output.ToArray());
    }

    /// <summary>One bounded host read operation (a prestart configured-start subset of A/B, or a single
    /// panel insert). The parent claims the single native cold-read fence before any file I/O and freezes
    /// the configured A/B membership. Per-disk member reads borrow the live fence without reacquiring or
    /// disposing it: open/validate/bounded-read, then close only the source file descriptor. The parent
    /// retains the fence through every member's real receipts, accounting and bind/no-bind outcomes, or a
    /// proven owner drain, and is released exactly once - after every expected member is accounted, by a
    /// pre-enqueue abort whose started I/O the member read already drained, or by a named host drain at a
    /// lifecycle boundary. No member terminal, cancellation or dispose disposes the fence.</summary>
    internal sealed class Operation
    {
        private readonly object sync = new();
        private readonly NativeImageAdmission? admission;
        private readonly bool guarded;
        private readonly DiskSlot[] slots;
        private readonly Reservation?[] members;
        private readonly Task?[] inFlight;
        private bool released;

        private Operation(DiskSlot[] slots, NativeImageAdmission? admission)
        {
            this.slots = slots;
            members = new Reservation?[slots.Length];
            inFlight = new Task?[slots.Length];
            guarded = admission is not null;
            this.admission = admission;
        }

        /// <summary>Claim the bounded host read operation: freeze the configured A/B membership (1-2 distinct
        /// slots) and acquire the single native cold-read fence before any file I/O (guarded hosts). A
        /// refused gate leaves no held work; a second claim while the fence is held is a Busy refusal
        /// before any read.</summary>
        internal static Operation ClaimStart(IReadOnlyList<DiskSlot> configured, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (configured is null || configured.Count is < 1 or > 2)
                throw new ArgumentException("A bounded host read operation owns one or two configured disks.", nameof(configured));
            var slots = configured.ToArray();
            for (var i = 0; i < slots.Length; i++)
                for (var j = i + 1; j < slots.Length; j++)
                    if (slots[i] == slots[j])
                        throw new ArgumentException("The configured membership names a disk twice.", nameof(configured));
            var guarded = NativeImageAdmission.PlatformMode == ImageAdmissionPlatformMode.GuardedLinux;
            NativeImageAdmission? admission = null;
            if (guarded)
            {
                var result = NativeImageAdmission.TryAcquireColdRead(cancellationToken);
                admission = result.Admission ?? throw new AdmissionException(result.Refusal!.Value);
            }
            return new Operation(slots, admission);
        }

        internal DiskSlot[] Slots => slots;
        internal bool Guarded => guarded;
        internal int ExpectedMemberCount => slots.Length;
        internal bool Released { get { lock (sync) return released; } }
        internal bool IsMember(DiskSlot slot) => slots.Contains(slot);

        internal Reservation? Member(DiskSlot slot)
        {
            var index = IndexOf(slot);
            if (index < 0) return null;
            lock (sync) return members[index];
        }

        internal IEnumerable<Reservation> Members
        {
            get
            {
                lock (sync) return members.Where(m => m is not null).Cast<Reservation>().ToArray();
            }
        }

        /// <summary>True once every expected member has a reservation that is terminal: the point at which
        /// the host may release the operation after its accounting.</summary>
        internal bool AllMembersTerminal
        {
            get
            {
                lock (sync) return members.All(m => m is not null && m.CurrentPhase == Phase.Terminal);
            }
        }

        /// <summary>Read one frozen member under the live fence (no reacquire, no dispose): open and validate
        /// the source (guarded hosts), bound-read the bytes, and close only the source file descriptor. The
        /// returned reservation borrows the parent fence and never disposes it. A failed member is terminal
        /// (pre-enqueue), the fence stays held by the operation, and the host aborts the whole configured
        /// start before any enqueue instead of dispatching a partial set.</summary>
        internal async Task<Reservation> ReadMemberAsync(DiskSlot slot, string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = IndexOf(slot);
            if (index < 0)
                throw new InvalidOperationException($"Disk {slot} is not a frozen member of this operation.");
            if (Released)
                throw new InvalidOperationException("The operation is already released.");
            string canonical;
            try { canonical = Path.GetFullPath(path); }
            catch { canonical = path; }

            var reservation = new Reservation(slot, canonical, guarded);
            Task<Reservation> read;
            lock (sync)
            {
                if (members[index] is not null)
                    throw new InvalidOperationException($"Disk {slot} already has a member read in this operation.");
                members[index] = reservation;
                // Register the started read under the same lock the release quiescence check takes, so a
                // concurrent named drain (machine reset, window close, worker completion) awaits this
                // read before the fence goes away: the source descriptor and its async I/O are always
                // drained before the fence is released.
                read = ReadMemberCoreAsync(reservation, canonical, cancellationToken);
                inFlight[index] = read;
            }
            try
            {
                return await read;
            }
            finally
            {
                lock (sync)
                {
                    if (ReferenceEquals(inFlight[index], read))
                        inFlight[index] = null;
                }
            }
        }

        private async Task<Reservation> ReadMemberCoreAsync(Reservation reservation, string canonical, CancellationToken cancellationToken)
        {
            FileStream? source = null;
            try
            {
                if (guarded)
                {
                    try
                    {
                        source = admission!.OpenColdSource(canonical);
                        reservation.RecordFacts(admission!.ValidateOpenedSource(source, canonical));
                    }
                    catch
                    {
                        // The member read cannot be admitted: the member is terminal (pre-enqueue), the
                        // started open (if any) is drained, and the fence stays held by the operation for
                        // the host's abort or named drain.
                        source?.Dispose();
                        source = null;
                        reservation.CancelBeforeEnqueue();
                        throw;
                    }
                }
                try
                {
                    var bytes = guarded
                        ? await ReadBoundedAsync(source!)
                        : await MediaFiles.ReadAsync(canonical, MediaFiles.MaxDiskBytes);
                    reservation.ReadCurrent(bytes);
                }
                catch
                {
                    reservation.CancelBeforeEnqueue();
                    throw;
                }
            }
            finally
            {
                source?.Dispose();
            }
            return reservation;
        }

        /// <summary>Release the operation exactly once: await any still-in-flight member read (a proven I/O
        /// quiescence), then complete any still-outstanding member by drain and dispose the single native
        /// fence. Called after every expected member is accounted (receipts, accounting and bind/no-bind
        /// outcomes), after a pre-enqueue abort whose started I/O the member read already drained, or by a
        /// named host drain at a lifecycle boundary (machine reset, window close, worker completion) — which
        /// is exactly where a started member read must be awaited before the fence goes away. The exact-once
        /// decision is synchronous under the lock; the fence is released only once the quiescence holds. A
        /// second release is a no-op.</summary>
        internal Task ReleaseOnceAsync(string reason)
        {
            Task[] pending;
            lock (sync)
            {
                if (released) return Task.CompletedTask;
                released = true;
                pending = inFlight.Where(t => t is not null).Cast<Task>().ToArray();
            }
            if (pending.Length == 0)
            {
                DrainAndDispose();
                return Task.CompletedTask;
            }
            return DrainAfterInFlightAsync(pending);
        }

        private async Task DrainAfterInFlightAsync(Task[] pending)
        {
            try
            {
                await Task.WhenAll(pending);
            }
            catch
            {
                // A failed member read already terminated its member pre-enqueue and surfaces to its own
                // caller; the named drain must still reach the fence.
            }
            DrainAndDispose();
        }

        private void DrainAndDispose()
        {
            lock (sync)
            {
                foreach (var member in members)
                    member?.DrainAtStop();
                if (guarded)
                    admission!.Dispose();
            }
        }

        /// <summary>End the operation before any member was enqueued (a read or parse failure in the
        /// configured start): the started I/O was already drained by the member read, so the fence is
        /// released here, exactly once. A member that was already enqueued cannot be aborted: it is
        /// acknowledged by its owner outcome or a named host drain.</summary>
        internal void AbortBeforeEnqueue(string reason)
        {
            lock (sync)
            {
                if (released) return;
                if (members.Any(m => m is { CurrentPhase: Phase.Dispatched }))
                    throw new InvalidOperationException("A member is already enqueued; the operation is acknowledged by owner outcomes or a named host drain, not by abort.");
            }
            // The failed read has already completed and drained its started I/O, so nothing is in flight:
            // the release reaches the fence synchronously.
            _ = ReleaseOnceAsync(reason);
        }

        private int IndexOf(DiskSlot slot)
        {
            for (var i = 0; i < slots.Length; i++)
                if (slots[i] == slot) return i;
            return -1;
        }
    }
}
