using Hc2000.Machine.Expansion;

namespace Hc2000.Machine.Memory;

public sealed partial class HcMemory
{
    private ExpansionArena? expansionArena;

    /// <summary>Host composition only; never inferred from the shared M paging policy.
    /// Must precede CPU/snapshot composition. No allocation occurs for other profiles.</summary>
    public ExpansionArena EnableExpansionArena(MachineProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        if (profile.Name != "hc2000-v2-paged-expansion-v1" ||
            profile.Memory with { RomProvenance = Profile.RomProvenance } != Profile)
            throw new ArgumentException("Expansion requires its explicit matching machine profile.", nameof(profile));
        if (expansionArena is not null)
            throw new InvalidOperationException("The expansion arena is already composed.");
        return expansionArena = new(ram, Latches.PagedBank, SetExpansionBank);
    }

    private void SetExpansionBank(ushort bank)
    {
        var next = Latches with { PagedBank = bank };
        var pages = MemoryPaging.Build(Profile, next, sessionRange);
        Latches = next; Pages = pages;
    }
}
