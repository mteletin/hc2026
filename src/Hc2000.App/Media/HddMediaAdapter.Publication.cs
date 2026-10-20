using System.Collections.Immutable;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Storage;
using Publication = Hc2000.HostFiles.HddContainerPublication;

namespace Hc2000.App.Media;

public sealed partial class HddMediaAdapter
{
    private sealed class BoundSave(HddSaveCapture capture, HddSlotState[] states, HddSwitchTarget?[] bindings)
    {
        internal readonly HddSaveCapture Capture = capture;
        internal readonly HddSlotState[] States = states;
        internal readonly HddSwitchTarget?[] Bindings = bindings;
        internal readonly (byte Unit, HddSlotState Expected)[] Acknowledgements = [(capture.Drive, states[capture.Drive - 2])];
        internal HddSwitchTarget?[]? Next;
        internal Publication.OfflineAttempt? Attempt;
        internal bool Started;
    }
    private BoundSave? boundSave;
    private readonly Guid publicationEpoch = Guid.NewGuid();
    // Test-only fault seam, never a queued caller input or source of replacement bytes.
    internal Action<Publication.OfflineIoEvent>? PublicationFault { get; set; }
    internal Publication.PublicationLimits PublicationLimits { get; set; } = new(
        uint.MaxValue, 511, 12_276, 114_640, 33_554_432, 33_554_432,
        10UL * ImageLength, 100_728_832, HddColdVolume.DefaultMaxTotalLiveBytes, 2UL * uint.MaxValue, 8UL * uint.MaxValue);

    // Only the existing writeback ledger calls this, AFTER pausing the FIFO owner.
    // CaptureSave itself remains an unconditional byte export, not publication authority.
    internal void FreezeSave(HddSaveCapture capture)
    {
        VerifyOwner();
        if (boundSave is not null || pendingSwitch is not null || controller.GuestSwitchHoldsCpu)
            throw new InvalidOperationException("An HDD save or guest switch is already held.");
        if (!saves.TryGetValue(capture.Id, out var known) || !ReferenceEquals(known, capture))
            throw new InvalidOperationException("The owner capture is stale.");
        if (PublicationLimits.MaxResidentBytes < 4UL * ImageLength || PublicationLimits.MaxSnapshotBytes < 10UL * ImageLength
            || PublicationLimits.MaxTotalLiveBytes < 17UL * ImageLength + 65536)
            throw new InvalidOperationException("HDD owner capture budget exceeded; raw export is still available.");
        var states = Enumerable.Range(2, 4).Select(drive => controller.CaptureHdd((byte)drive)).ToArray();
        var selected = states[Index(capture.Drive)];
        if (!selected.Present || selected.Generation != capture.Generation || !selected.Bytes.AsSpan().SequenceEqual(capture.Bytes.AsSpan()))
            throw new InvalidOperationException("The native capture changed before its fence.");
        boundSave = new(capture, states, bindings.ToArray());
    }

    internal bool AbortPublication(long saveId)
    {
        VerifyOwner();
        if (boundSave is not { } save || save.Capture.Id != saveId) return false;
        if (save.Started && (save.Attempt is null || save.Attempt.Receipt.Outcome is Publication.OfflineOutcome.Unknown or Publication.OfflineOutcome.KnownNew))
            return false; // Lost/unknown Begin or published-but-unacknowledged is not an abort.
        saves.Remove(saveId);
        save.Attempt?.Dispose();
        boundSave = null;
        return true;
    }

    internal HddPublicationView PublishSave(long saveId, bool reconcile)
    {
        VerifyOwner();
        if (boundSave is not { } save || save.Capture.Id != saveId)
            return new(saveId, false, boundSave is not null, Error: "Unknown HDD owner capture.");
        try
        {
            if (!save.Started)
            {
                ValidateNative(save);
                var target = save.Bindings[Index(save.Capture.Drive)] ?? throw new InvalidOperationException("This HDD slot has no container binding; export a raw copy instead.");
                if (!target.Writable || save.States[Index(save.Capture.Drive)].WriteProtected)
                    throw new InvalidOperationException("The bound volume is read-only.");
                var mounted = ImmutableArray.CreateBuilder<Publication.VolumeIdentity>();
                foreach (var binding in save.Bindings.OfType<HddSwitchTarget>())
                {
                    bool sameId = binding.Identity.Store.ContainerId.AsSpan().SequenceEqual(target.Identity.Store.ContainerId.AsSpan());
                    bool samePath = StringComparer.Ordinal.Equals(binding.Path, target.Path);
                    if (sameId != samePath) throw new InvalidOperationException("Mounted HDD store aliases disagree.");
                    if (samePath) mounted.Add(HostIdentity(binding.Identity));
                }
                // E includes four retained native expectations, up to four raw captures,
                // and two scratch/copy images; R is all four native slots, Q is 32 KiB.
                var request = new Publication.OfflineReplaceRequest(target.Path, HostIdentity(target.Identity), publicationEpoch,
                    Guid.NewGuid(), new(true, true, false, true), PublicationLimits, new(1, 4UL * ImageLength, 10UL * ImageLength, 0));
                save.Started = true;
                save.Attempt = Publication.ReplaceCaptured(request, save.Capture.Bytes.AsSpan(), mounted.ToImmutable(), intended =>
                {
                    ValidateNative(save);
                    var store = new HddStoreToken(intended.Store.ContainerId, intended.Store.Revision, intended.Store.MetadataSha256);
                    var next = save.Bindings.ToArray();
                    for (int i = 0; i < next.Length; i++)
                        if (next[i] is { } binding && StringComparer.Ordinal.Equals(binding.Path, target.Path))
                            next[i] = binding with { Identity = new(store, binding.Identity.Name,
                                i == Index(save.Capture.Drive) ? intended.PayloadSha256 : binding.Identity.PayloadSha256) };
                    save.Next = next;
                }, fault: PublicationFault);
            }
            else if (reconcile) save.Attempt?.Reconcile();
            var receipt = save.Attempt?.Receipt;
            if (receipt is null) return new(saveId, false, true, Error: "HDD publication Begin outcome is unresolved.");
            if (!receipt.WriterDrained || receipt.Outcome == Publication.OfflineOutcome.Unknown)
                return new(saveId, false, true, receipt, "HDD publication is unknown; retain the owner fence and reconcile.");
            if (receipt.Outcome == Publication.OfflineOutcome.KnownNew)
            {
                ValidateNative(save); // ALL bindings, before the atomic native batch preflight.
                if (save.Next is null || !controller.TryAcknowledgeHddSaved(save.Acknowledgements))
                    return new(saveId, false, true, receipt, "Published HDD bytes could not be acknowledged; the owner fence remains held.");
                // All allocation and validation preceded publication/ack; only nonthrowing sideband assignments remain.
                for (int i = 0; i < bindings.Length; i++) bindings[i] = save.Next[i];
                saves.Remove(saveId);
                boundSave = null;
                save.Attempt?.Dispose();
                return new(saveId, true, false, receipt);
            }
            saves.Remove(saveId);
            boundSave = null;
            save.Attempt?.Dispose();
            return new(saveId, false, false, receipt, $"HDD publication refused: {receipt.Error}; native edits remain unsaved.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException)
        {
            // Before Begin there is certainly no writer; afterwards an exception is not evidence of rollback.
            bool held = save.Started;
            if (!held) { saves.Remove(saveId); boundSave = null; }
            return new(saveId, false, held, save.Attempt?.Receipt, ex.Message);
        }
    }

    private void ValidateNative(BoundSave save)
    {
        for (byte drive = 2; drive <= 5; drive++)
        {
            int i = Index(drive);
            var expected = save.States[i];
            var current = controller.CaptureHdd(drive);
            if (!ReferenceEquals(bindings[i], save.Bindings[i]) || current.Present != expected.Present
                || current.Generation != expected.Generation || current.WriteProtected != expected.WriteProtected
                || current.Dirty != expected.Dirty || !current.Bytes.AsSpan().SequenceEqual(expected.Bytes)
                || (save.Bindings[i] is { Writable: false } && !current.WriteProtected))
                throw new InvalidOperationException("A mounted HDD binding or native state changed; no saved acknowledgement is permitted.");
        }
    }
    private static Publication.VolumeIdentity HostIdentity(HddVolumeIdentity identity) => new(
        new(identity.Store.ContainerId, identity.Store.Revision, identity.Store.MetadataSha256), identity.Name, identity.PayloadSha256);
}
