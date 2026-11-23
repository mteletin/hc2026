using Hc2000.Machine.Expansion;
using Hc2000.Machine.Network;

namespace Hc2000.Machine.Memory;

public sealed partial class HcMemory
{
    private ExpansionArena? expansionArena;
    private bool x2PlanesReserved;

    /// <summary>Host composition only; never inferred from the shared M paging policy.
    /// Must precede CPU/snapshot composition. No allocation occurs for other profiles.</summary>
    public ExpansionArena EnableExpansionArena(MachineProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        if (profile.Name is not ("hc2000-v2-paged-expansion-v1" or "hc2000-v2-paged-expansion-x5-v1"
            or "hc2000-v2-paged-expansion-x5-hdd-guest-v1"
            or "hc2000-v2-cpnet-v1" or "hc2000-v2-combined-workstation-v1" or "hc2000-v2-combined-workstation-v2"
            or "hc2000-v2-full-workstation-v1") ||
            profile.Memory with { RomProvenance = Profile.RomProvenance } != Profile)
            throw new ArgumentException("Expansion requires its explicit matching machine profile.", nameof(profile));
        if (expansionArena is not null)
            throw new InvalidOperationException("The expansion arena is already composed.");
        if (profile.CpNet && Latches.PagedBank == CpNetX0Card.StagingPage)
            throw new InvalidOperationException("The main latch conflicts with the CP/NET staging claim.");
        var arena = new ExpansionArena(ram, Latches.PagedBank, SetExpansionBank);
        if (profile.CpNet)
        {
            var claim = new ExpansionDevicePageClaim(CpNetX0Card.StagingPage, CpNetX0Card.CardId, true, 0, CpNetX0Card.StagingCapacity);
            if (!arena.TryClaimDevicePage(claim))
                throw new InvalidOperationException("The CP/NET staging claim conflicts with an existing owner.");
            ram.AsSpan(0x10000 + CpNetX0Card.StagingPage * ExpansionArena.PageBytes, ExpansionArena.PageBytes).Clear();
        }
        return expansionArena = arena;
    }

    private void SetExpansionBank(ushort bank)
    {
        var next = Latches with { PagedBank = bank };
        var pages = MemoryPaging.Build(Profile, next, sessionRange);
        Latches = next; Pages = pages;
    }

    internal bool TryDmaPageEpoch(ushort page, out ulong epoch)
    {
        epoch = 0;
        if (expansionArena is null || !expansionArena.MainCanDma(page) ||
            pagedClaims.Any(c => page >= c.Range.FirstPage && page < c.Range.FirstPage + c.Range.PageCount))
            return false;
        epoch = expansionArena.PageEpoch(page);
        return true;
    }

    internal bool TryDmaPageEpoch(ushort cardId, byte direction, ushort page, ushort offset, uint count, out ulong epoch)
    {
        epoch = 0;
        if (expansionArena is null || !expansionArena.MainOwns(page) && !expansionArena.PageLentTo(page, cardId) ||
            !expansionArena.AllowsDma(cardId, direction, page, offset, count) ||
            pagedClaims.Any(c => page >= c.Range.FirstPage && page < c.Range.FirstPage + c.Range.PageCount))
            return false;
        epoch = expansionArena.PageEpoch(page);
        return true;
    }

    internal bool IsCardReservedPage(ushort page) => expansionArena is not null &&
        expansionArena.MainOwns(page) && !expansionArena.MainCanDma(page);

    internal bool HasCpNetStagingClaim() => expansionArena?.HasDeviceClaim(
        new(CpNetX0Card.StagingPage, CpNetX0Card.CardId, true, 0, CpNetX0Card.StagingCapacity)) == true;

    /// <summary>Cold composition only: bind two disjoint, five-page X2 planes into the existing
    /// main-owned arena. The final unused bytes of each fifth page remain reserved to the card.</summary>
    internal bool CanReserveX2Planes(ushort backFirst, ushort frontFirst, ushort cardId = 3)
    {
        if (expansionArena is null || x2PlanesReserved || backFirst > 65536 - 5 || frontFirst > 65536 - 5)
            return false;
        var claims = new ExpansionDevicePageClaim[10];
        for (var index = 0; index < 5; index++)
        {
            claims[index] = new((ushort)(backFirst + index), cardId, true, 0, 0, false);
            claims[index + 5] = new((ushort)(frontFirst + index), cardId, true, 0, 0, false);
        }
        foreach (var claim in claims)
            if (!TryDmaPageEpoch(claim.Page, out _)) return false;
        return expansionArena.CanClaimDevicePages(claims);
    }

    internal bool TryReserveX2Planes(ushort backFirst, ushort frontFirst, out Memory<byte> back, out Memory<byte> front,
        ushort cardId = 3)
    {
        back = default; front = default;
        if (!CanReserveX2Planes(backFirst, frontFirst, cardId)) return false;
        var claims = new ExpansionDevicePageClaim[10];
        for (var index = 0; index < 5; index++)
        {
            claims[index] = new((ushort)(backFirst + index), cardId, true, 0, 0, false);
            claims[index + 5] = new((ushort)(frontFirst + index), cardId, true, 0, 0, false);
        }
        if (!expansionArena!.TryClaimDevicePages(claims)) return false;
        const int pixels = ExpansionX2Surface.Width * ExpansionX2Surface.Height;
        back = ram.AsMemory(0x10000 + backFirst * PagedRamPolicy.PageBytes, pixels);
        front = ram.AsMemory(0x10000 + frontFirst * PagedRamPolicy.PageBytes, pixels);
        x2PlanesReserved = true;
        return true;
    }

    /// <summary>The full workstation's paged-drive switch keeps its state on page 880 and its hard-disk cache on
    /// page 881 (hc2000-full-workstation-default, ws.layout.inc). Cold composition reserves both, main-mappable,
    /// so no card claim or unit repurpose can take them.</summary>
    internal const ushort WorkstationStatePage = 880;
    internal const ushort WorkstationCachePage = 881;
    // System v2's BDOS (hc2000-v2-system): BDOS.SYS runs from this page (src-library/extended/bios-upgrade/v2).
    internal const ushort WorkstationBdosPage = 882;
    // The v2 shell's pipe buffer (hc2000-v2-system): redirection and pipes keep a command's console output here
    // across the warm boot that ends it (src-library/extended/bios-upgrade/v2/v2.layout.inc, PipePageFirst).
    internal const ushort WorkstationPipeFirstPage = 876;
    internal const int WorkstationPipePages = 4;

    internal bool TryReserveWorkstationPages() =>
        expansionArena?.TryReserveCardPages([WorkstationStatePage, WorkstationCachePage, WorkstationBdosPage,
            .. Enumerable.Range(WorkstationPipeFirstPage, WorkstationPipePages).Select(page => (ushort)page)]) == true;

    internal ExpansionArena? ExpansionArena => expansionArena;

    /// <summary>
    /// X0 shared physical DMA only: an owner-thread view of one 16-KiB arena segment. The arena
    /// is composed only under the explicit expansion profile; page p maps exactly to
    /// 0x10000 + p*0x4000 of the physical RAM.
    /// </summary>
    public ReadOnlySpan<byte> ExpansionArenaSpan(ushort page, ushort offset, int count)
    {
        var baseAddress = ExpansionArenaOffset(page, offset, count);
        return ram.AsSpan(baseAddress, count);
    }

    /// <summary>X0 shared physical DMA only: one segment write into the arena.</summary>
    public void ExpansionArenaCopy(ushort page, ushort offset, ReadOnlySpan<byte> data)
    {
        var baseAddress = ExpansionArenaOffset(page, offset, data.Length);
        data.CopyTo(ram.AsSpan(baseAddress, data.Length));
    }

    /// <summary>Full-X5 managed main/driver path only: one byte of the 64-KiB base RAM, raw physical
    /// (no latch or paging model; those live in the faithful Legacy projection). The base RAM is
    /// never a paged-claim target, so raw access preserves the single-copy RAM asset.</summary>
    public byte BaseRamRead(ushort offset)
    {
        VerifyBaseRam(offset);
        return ram[offset];
    }

    /// <summary>Full-X5 managed main/driver path only: one byte written into the 64-KiB base RAM.</summary>
    public void BaseRamWrite(ushort offset, byte value)
    {
        VerifyBaseRam(offset);
        ram[offset] = value;
    }

    private void VerifyBaseRam(ushort offset)
    {
        if (expansionArena is null)
            throw new InvalidOperationException("Base RAM access requires the explicit expansion composition.");
        if (checked((int)offset) >= BankPolicy.BaseRamBytes)
            throw new ArgumentOutOfRangeException(nameof(offset), "Base RAM is the first 64 KiB.");
    }

    private int ExpansionArenaOffset(ushort page, ushort offset, int count)
    {
        if (expansionArena is null)
            throw new InvalidOperationException("Expansion arena access requires the explicit expansion composition.");
        if (count is < 0 or > PagedRamPolicy.PageBytes || offset >= PagedRamPolicy.PageBytes ||
            checked((long)offset + count) > PagedRamPolicy.PageBytes)
            throw new ArgumentOutOfRangeException(nameof(offset), "Arena segment leaves the 16-KiB page.");
        var baseAddress = checked(0x10000 + checked((int)page * PagedRamPolicy.PageBytes));
        if (baseAddress + count > ram.Length) throw new ArgumentOutOfRangeException(nameof(page), "Arena page out of range.");
        return baseAddress + offset;
    }
}
