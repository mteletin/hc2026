namespace Hc2000.App.Configuration;

/// <summary>Apply-running-configuration policy: the running machine may receive a live configuration
/// change and reset ONLY when the draft keeps the mounted A/B media and writeback choices byte-for-byte
/// identical (same machine identity, valid, and effective for this app). Any media difference needs a
/// new machine draft. No runtime access, no persistence: persistence remains the explicit prestart save
/// and Settings… only, and snapshots/RAM are never written here.</summary>
public static class ReconfigureApply
{
    public static bool TryPlan(MachineConfiguration current, MachineConfiguration draft, out string? error)
    {
        error = null;
        if (current.Id != draft.Id)
        {
            error = "The draft belongs to a different machine; the running machine keeps its configuration.";
            return false;
        }
        try
        {
            draft.Validate();
            if (current.TextOverlay != draft.TextOverlay)
            {
                error = "The text overlay is a cold hardware choice; a new machine draft is required. Use New machine; selected Reset keeps the adopted choice.";
                return false;
            }
            if (current.Profile != draft.Profile || current.RomSet != draft.RomSet
                || current.Hdd.IsDefault
                || !current.Hdd.OrderBy(h => h.Slot).SequenceEqual(draft.Hdd.OrderBy(h => h.Slot)))
            {
                error = $"Hardware or HDD attachment changed from {current.Profile}; a new cold machine is required. Use New machine; selected Reset keeps the adopted hardware.";
                return false;
            }
            draft.ValidateEffective();
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
        if (current.DiskA != draft.DiskA || current.DiskB != draft.DiskB || current.LastSnapshot != draft.LastSnapshot)
        {
            error = "Apply keeps the mounted A/B media and writeback choices unchanged; media changes need a new machine draft.";
            return false;
        }
        return true;
    }
}
