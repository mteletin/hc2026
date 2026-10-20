using Hc2000.Machine.Expansion;
using Hc2000.Machine.Memory;

namespace Hc2000.Machine.Io;

public static class ExpansionIoProfile
{
    /// <summary>Add eight exact endpoints without rebuilding or broadening any existing row.</summary>
    public static IReadOnlyList<PortMapping> Compose(MachineProfile profile, HcMemory memory,
        IReadOnlyList<PortMapping> existing, ExpansionController controller)
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(existing); ArgumentNullException.ThrowIfNull(controller);
        profile.Validate();
        if (profile.Name != "hc2000-v2-paged-expansion-v1" ||
            profile.Memory with { RomProvenance = memory.Profile.RomProvenance } != memory.Profile)
            throw new ArgumentException("Expansion ports require their explicit matching profile.", nameof(profile));
        return [.. existing, .. controller.Mappings()];
    }
}
