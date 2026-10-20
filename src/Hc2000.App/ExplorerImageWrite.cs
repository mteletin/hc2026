using System.Collections.Immutable;
using Avalonia.Threading;
using Hc2000.App.Media;
using Hc2000.HostFiles;

namespace Hc2000.App;

internal enum ExplorerWriteRefusal { Mounted, OwnershipUnknown, ProtectedOriginal, NativeAdmission, Failed, Unknown }
internal sealed record ExplorerWriteResult(bool Published, ExplorerWriteRefusal? Refusal, string Message);

public partial class MainWindow
{
    internal const string ExplorerWriteScope = "Protection covers this App host only. External processes may still write mounted images; close them before editing.";

    /// <summary>One synchronous host-only transaction, with no UI reentrancy, owner dispatch or awaits.
    /// The callback must be a pure bounded image edit (e.g. CP/M copy-in), not UI or guest work.
    /// No source bytes reach it until the existing admission and every session have been checked.</summary>
    internal ExplorerWriteResult WriteExplorerImage(string path, Func<ImmutableArray<byte>, ImmutableArray<byte>> edit)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(edit);
        var acquired = NativeImageAdmission.TryBeginPublication(path, Guid.NewGuid(), default);
        if (acquired.Admission is not { } admission)
            return Refused(ExplorerWriteRefusal.NativeAdmission, $"Native image admission: {acquired.Refusal}.");
        using (admission)
        {
            string? temporary = null;
            bool moveStarted = false, moved = false;
            try
            {
                // Metadata only: acquire and validate the actual handle before comparing identities;
                // ownership refusal must not read source contents or stage any output.
                using (var input = admission.OpenPublicationSource(path))
                {
                    var facts = admission.ValidateOpenedSource(input, path);
                    if (CheckExplorerOwnership(facts.FileId) is { } refusal) return refusal;
                    if (AllSessions().Any(session => session.PathIsProtectedOriginal(path)))
                        return Refused(ExplorerWriteRefusal.ProtectedOriginal, "Protected original: use an unmounted private copy.");
                    if (facts.Length <= 0 || facts.Length > HddMediaAdapter.ImageLength)
                        throw new InvalidDataException("Explorer image exceeds the bounded FDD/HDD image size.");
                    var bytes = new byte[checked((int)facts.Length)];
                    input.ReadExactly(bytes);
                    if (input.ReadByte() != -1) throw new InvalidDataException("Image changed size while reading.");
                    admission.ValidateSourceStillCurrent(input, facts);
                    var output = edit(ImmutableArray.CreateRange(bytes));
                    if (output.IsDefault || output.Length != bytes.Length)
                        throw new InvalidDataException("An explorer edit must preserve the image length.");
                    admission.ValidateSourceStillCurrent(input, facts);
                    temporary = facts.CanonicalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using var staging = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    staging.Write(output.AsSpan());
                    staging.Flush(flushToDisk: true);
                }
                // Both file descriptors and all synchronous I/O are drained before publication/terminal.
                admission.ValidatePublicationTargetStillCurrent();
                moveStarted = true;
                File.Move(temporary!, Path.GetFullPath(path), overwrite: true);
                moved = true;
                return new(true, null, "Image published. " + ExplorerWriteScope);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return Refused(moveStarted ? ExplorerWriteRefusal.Unknown : ExplorerWriteRefusal.Failed,
                    moveStarted ? "Publication outcome unknown; do not retry. " + ex.Message : ex.Message);
            }
            finally
            {
                try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
                finally
                {
                    if (moved || !moveStarted)
                        admission.CompleteKnownPublicationAfterDrain(moved ? KnownImagePublication.KnownNew : KnownImagePublication.Unpublished);
                    else admission.RetainUnknownPublication();
                }
            }
        }
    }

    private ExplorerWriteResult? CheckExplorerOwnership(NativeImageFileId target)
    {
        if (closing) return Refused(ExplorerWriteRefusal.OwnershipUnknown, "App host is closing.");
        foreach (var session in AllSessions())
        {
            var host = session.WritebackHost;
            if (session.HeldOperation is not null || session.ReplacingRuntime || !session.BasicReplacementCompletion.IsCompleted
                || session.MediaBridge?.HasPending == true || host?.Frozen == true || host?.ResetGuardClear() == false)
                return Refused(ExplorerWriteRefusal.OwnershipUnknown, "A session has unresolved media or writeback work.");
            foreach (var receipt in host?.CaptureMountObservations() ?? [])
            {
                if (receipt.Invalidated || receipt.BindingState != DiskWritebackHost.MountBindingState.Bound
                    || receipt.CanonicalSourcePath is null || receipt.ReadIdentity is not { } read)
                    return Refused(ExplorerWriteRefusal.OwnershipUnknown, "Mount ownership or writeback identity is unavailable.");
                var current = LinuxFileFacts.Inspect(receipt.CanonicalSourcePath);
                var id = read.FileId;
                var nativeId = $"{id.DeviceMajor:x}:{id.DeviceMinor:x}:{id.Inode:x}";
                if (!current.Known || !current.Regular || current.Link || current.FileId != nativeId
                    || !receipt.PathFactsAtReceipt.Known || receipt.PathFactsAtReceipt.FileId != nativeId)
                    return Refused(ExplorerWriteRefusal.OwnershipUnknown, "Mount source native identity is unavailable or has changed.");
                if (id == target)
                    return Refused(ExplorerWriteRefusal.Mounted, "This image is mounted in an App session; unmount it before editing.");
                // Receipts are not complete occupancy/dirty/busy authority. Do not turn an unrelated
                // receipt, absent receipt, paused worker or clean-looking drive into permission.
                return Refused(ExplorerWriteRefusal.OwnershipUnknown, "A session still has mounted media; stop it before editing.");
            }
            if (session.HasRuntime || session.BasicRuntimeActive || session.StartingConfiguration is not null)
                return Refused(ExplorerWriteRefusal.OwnershipUnknown, "A live or starting session cannot prove empty media ownership.");
        }
        return null;
    }

    private static ExplorerWriteResult Refused(ExplorerWriteRefusal refusal, string message)
        => new(false, refusal, message + " " + ExplorerWriteScope);
}
