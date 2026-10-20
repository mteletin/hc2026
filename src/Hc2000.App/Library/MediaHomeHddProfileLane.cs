using System.Collections.Immutable;
using Hc2000.App.Configuration;
using Hc2000.App.Media;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Library;

/// <summary>
/// The media-home HDD profile lane (hc2000-hdd-context-mount-profile-lane): a right-click
/// "Select as C:–F:" on a media list HDD row records a named container volume in the machine
/// draft, effective cold through the existing HddColdVolume/HddColdAdmission path at the next
/// cold start. The target is an HDD container, never a raw 8 MiB volume row: an hdd-raw row is
/// listed and browsable but is not a Select-as target until an existing container publication
/// puts it in a container, which this contract does not add. No third mount mechanism, no hot
/// swap. A refusal at any step leaves the draft untouched and states the reason.
/// </summary>
public static class MediaHomeHddProfileLane
{
    /// <summary>The same cold admission bounds the existing HddColdVolume path admits with.</summary>
    private static readonly HddLimits Limits = new(uint.MaxValue, 511, 12_276, 114_640);

    /// <summary>Selects a scanned media list row as a cold profile entry for one of the HDD slots
    /// C:–F:. <paramref name="kind"/> is the row's scanned kind ("hdd-container" or "hdd-raw");
    /// <paramref name="volumeName"/> selects one container volume, or when null exactly one named
    /// volume must be published in the container. <paramref name="draft"/> is the machine draft; a
    /// refusal returns it unchanged with the stated reason, a selection returns the recorded draft
    /// plus the admitted volume identity.</summary>
    public static MediaHomeHddProfileResult Select(string kind, string path, string? volumeName, string slot, MachineConfiguration draft)
    {
        if (slot is not ("C" or "D" or "E" or "F"))
            return Refused(draft, $"Select-as targets are the HDD slots C:–F:, not '{slot}:'");
        if (kind == "hdd-raw")
            return Refused(draft, "an hdd-raw row is listed and browsable but it is not a Select-as-C: target until an existing container publication puts it in a container; this contract does not add that publication");
        if (kind != "hdd-container")
            return Refused(draft, $"only an hdd-container row can be selected cold, not a {kind} row");
        if (string.IsNullOrWhiteSpace(path))
            return Refused(draft, "the selected row has no local file path");
        try
        {
            var name = volumeName ?? SingleVolume(path);
            using var volume = HddColdVolume.OpenMapped(path, [new HddColdVolumeRequest((byte)(slot[0] - 'A'), name)]);
            var identity = volume.Identities[0];
            // Profile change, effective cold: the slot is replaced (not duplicated) in the machine
            // draft; the existing cold start path admits it at the next cold start.
            var recorded = draft with { Hdd = [.. draft.Hdd.Where(h => h.Slot != slot), new HddConfiguration(slot, path, true, identity.Name)] };
            recorded.Validate();
            return new(MediaHomeHddProfileOutcome.Selected, null, recorded, identity);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            return Refused(draft, ex.GetBaseException().Message);
        }
    }

    /// <summary>Clears the recorded slot from the machine draft; a slot that is not recorded
    /// returns the draft unchanged.</summary>
    public static MachineConfiguration Clear(MachineConfiguration draft, string slot)
        => draft.Hdd.Any(h => h.Slot == slot)
            ? draft with { Hdd = [.. draft.Hdd.Where(h => h.Slot != slot)] }
            : draft;

    /// <summary>Names the single volume published in a container, refusing a container that
    /// publishes none or more than one until the caller picks one. Bounded footer and index reads
    /// only, through the existing detached codec -- no admission or payload copy here.</summary>
    private static string SingleVolume(string path)
    {
        string[] names;
        using (var file = File.OpenRead(path))
        {
            var length = file.Length;
            if (length < 76)
                throw new InvalidDataException("HDD container length exceeds cold admission.");
            var footer = new byte[64];
            file.Seek(-64L, SeekOrigin.End);
            file.ReadExactly(footer);
            var inspected = HddCatalogCodec.InspectHddFooter(footer, (ulong)length, Limits);
            if (!inspected.Success)
                throw new InvalidDataException(inspected.Failure!.Detail);
            var view = inspected.Value;
            var index = new byte[checked((int)view.IndexBytes)];
            file.Seek(checked((long)view.IndexStart), SeekOrigin.Begin);
            file.ReadExactly(index);
            var decoded = HddCatalogCodec.DecodeHdd(footer, index, (ulong)length, Limits);
            if (!decoded.Success)
                throw new InvalidDataException(decoded.Failure!.Detail);
            names = decoded.Value.Entries.Select(entry => entry.Name).ToArray();
        }
        return names.Length switch
        {
            1 => names[0],
            0 => throw new InvalidDataException("The container holds no named volumes."),
            _ => throw new InvalidDataException("Select a volume from this multi-volume container: " + string.Join(", ", names) + ".")
        };
    }

    private static MediaHomeHddProfileResult Refused(MachineConfiguration draft, string reason)
        => new(MediaHomeHddProfileOutcome.Refused, reason, draft, null);
}

public enum MediaHomeHddProfileOutcome
{
    Selected,
    Refused,
}

/// <summary>The lane's closed outcome: a refusal carries the stated reason and the untouched
/// draft; a selection carries the recorded draft and the admitted volume identity.</summary>
public sealed record MediaHomeHddProfileResult(MediaHomeHddProfileOutcome Outcome, string? Message, MachineConfiguration Draft, HddVolumeIdentity? Identity);
