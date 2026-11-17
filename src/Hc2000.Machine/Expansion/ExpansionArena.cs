namespace Hc2000.Machine.Expansion;

/// <summary>Four logical 16-KiB pages, in 0000/4000/8000/C000 order. Never a mutable map.</summary>
public readonly record struct ExpansionPageMap(ushort Page0, ushort Page1, ushort Page2, ushort Page3)
{
    public ushort this[int index] => index switch
    {
        0 => Page0, 1 => Page1, 2 => Page2, 3 => Page3,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    /// <summary>Whether this four-page map contains the given arena page.</summary>
    public bool Contains(ushort page) => page == Page0 || page == Page1 || page == Page2 || page == Page3;
}

public readonly record struct ExpansionPageGrant(ushort UnitId, ulong ResourceEpoch, ulong MapEpoch,
    ExpansionPageMap Pages)
{
    /// <summary>Whether the grant physically owns the given arena page.</summary>
    public bool Owns(ushort page) => Pages.Contains(page);
}

internal sealed record ExpansionDevicePageClaim(ushort Page, ushort CardId, bool MainMappable,
    ushort SourceOffset, ushort SourceCapacity, bool ExclusiveSource = true);

/// <summary>Owner-side arena admission; does not contain CPU, controller or scheduler references.</summary>
public sealed partial class ExpansionArena
{
    public const int ComputeCount = 16;
    public const int PageBytes = 0x4000;
    // Compute arena pages 512-575 (map v2, note-20261018-234241).
    public const int FirstComputePage = 512;
    // Full-X5 driver arena (hc2000-paged-store-map-v2 v4): 8 driver units x 4 pages, 832-863.
    // CP/NET reserves page 864; later device claims begin at 865 and require live admission.
    public const int DriverFirst = 17;
    public const int DriverCount = 8;
    public const int DriverLast = DriverFirst + DriverCount - 1;
    public const int FirstDriverPage = 832;
    public const int LastRosterPage = FirstDriverPage + 4 * DriverCount;
    public const int CpNetStagingPage = LastRosterPage;
    public const int FirstUnreservedDevicePage = CpNetStagingPage + 1;
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly byte[] ram;
    // Zero denotes the main pool. Roster pages are private; a conflicting main latch is refused in the constructor.
    private readonly byte[] pageOwners = new byte[65536];
    private readonly Dictionary<ushort, ExpansionDevicePageClaim> deviceClaims = new();
    private readonly ulong[] pageEpochs = new ulong[65536];
    private readonly ExpansionPageGrant[] grants = new ExpansionPageGrant[ComputeCount + DriverCount];
    private ushort mainBank;
    private readonly Action<ushort> setHostBank;

    internal ExpansionArena(byte[] ram, ushort mainBank, Action<ushort> setHostBank)
    {
        if (ram.Length != Memory.PagedRamPolicy.RamBytes)
            throw new ArgumentException("Expansion requires the existing full paged arena.", nameof(ram));
        if (mainBank is >= FirstComputePage and < FirstComputePage + 4 * ComputeCount)
            throw new InvalidOperationException("The main latch conflicts with the initial compute roster.");
        this.ram = ram;
        this.mainBank = mainBank;
        this.setHostBank = setHostBank;
        for (ushort id = 1; id <= ComputeCount; id++)
        {
            var first = (ushort)(FirstComputePage + 4 * (id - 1));
            grants[id - 1] = new(id, 1, 1, PagesOf(id, first));
            PublishOwners(id, first);
        }
    }

    internal void EnableDriverRoster()
    {
        VerifyOwner();
        if (grants[DriverFirst - 1].UnitId != 0)
            throw new InvalidOperationException("The driver roster is already composed.");
        if (!CanInitializeDriverRoster(grants.AsSpan(0, ComputeCount), mainBank,
                exchangeOwner == 0 ? null : exchangePreviousBank))
            throw new InvalidOperationException("The saved latch or compute grants conflict with the driver roster.");
        for (var page = FirstDriverPage; page < LastRosterPage; page++)
            if (pageOwners[page] != 0 || deviceClaims.ContainsKey((ushort)page) || pageEpochs[page] == ulong.MaxValue)
                throw new InvalidOperationException("The driver arena reservation conflicts with an existing grant.");
        for (ushort id = DriverFirst; id <= DriverLast; id++)
        {
            var first = (ushort)(FirstDriverPage + 4 * (id - DriverFirst));
            grants[id - 1] = new(id, 1, 1, PagesOf(id, first));
            PublishOwners(id, first);
        }
    }

    /// <summary>Preflight initial X5 against detached, already validated compatibility grants and
    /// latch state without touching RAM or publishing ownership.</summary>
    internal static bool CanInitializeDriverRoster(ReadOnlySpan<ExpansionPageGrant> computeGrants,
        ushort mainBank, ushort? exchangePreviousBank)
    {
        static bool Conflicts(ushort page) => page is >= FirstDriverPage and < LastRosterPage;
        if (computeGrants.Length != ComputeCount || Conflicts(mainBank) ||
            exchangePreviousBank is { } previous && Conflicts(previous)) return false;
        foreach (var grant in computeGrants)
            for (var index = 0; index < 4; index++)
                if (Conflicts(grant.Pages[index])) return false;
        return true;
    }

    private static ExpansionPageMap PagesOf(ushort unit, int first) => new((ushort)first, (ushort)(first + 1), (ushort)(first + 2), (ushort)(first + 3));
    private void PublishOwners(ushort unit, int first)
    {
        for (var i = 0; i < 4; i++)
        {
            pageOwners[first + i] = (byte)unit;
            pageEpochs[first + i]++;
            ram.AsSpan(Physical((ushort)(first + i)), PageBytes).Clear();
        }
    }

    public ExpansionPageGrant GetGrant(ushort unitId)
    {
        VerifyOwner();
        if (unitId is 0 or > ComputeCount + DriverCount ||
            grants[unitId - 1].UnitId == 0) throw new ArgumentOutOfRangeException(nameof(unitId));
        return grants[unitId - 1];
    }

    public ExpansionMemoryLease Acquire(ushort unitId) => new(this, GetGrant(unitId));

    internal bool CanClaimDevicePages(ReadOnlySpan<ExpansionDevicePageClaim> claims)
    {
        VerifyOwner();
        if (claims.IsEmpty || claims[0] is null || claims[0].CardId == 0) return false;
        var cardId = claims[0].CardId;
        if (deviceClaims.Values.Any(existing => existing.CardId == cardId)) return false;
        for (var index = 0; index < claims.Length; index++)
        {
            var claim = claims[index];
            if (claim is null || claim.CardId != claims[0].CardId || claim.ExclusiveSource != claims[0].ExclusiveSource ||
                claim.ExclusiveSource && claim.SourceCapacity == 0 ||
                (int)claim.SourceOffset + claim.SourceCapacity > PageBytes ||
                claim.Page == mainBank || exchangeOwner != 0 && claim.Page == exchangePreviousBank ||
                pageOwners[claim.Page] != 0 || deviceClaims.ContainsKey(claim.Page) || pageEpochs[claim.Page] == ulong.MaxValue)
                return false;
            for (var previous = 0; previous < index; previous++)
                if (claims[previous].Page == claim.Page) return false;
        }
        return true;
    }

    internal bool TryClaimDevicePages(ReadOnlySpan<ExpansionDevicePageClaim> claims)
    {
        if (!CanClaimDevicePages(claims)) return false;
        deviceClaims.EnsureCapacity(checked(deviceClaims.Count + claims.Length));
        foreach (var claim in claims)
        {
            deviceClaims.Add(claim.Page, claim);
            pageEpochs[claim.Page]++;
        }
        return true;
    }

    internal bool TryClaimDevicePage(ExpansionDevicePageClaim claim) => TryClaimDevicePages([claim]);

    internal bool HasDeviceClaim(ExpansionDevicePageClaim claim)
    {
        VerifyOwner();
        return deviceClaims.TryGetValue(claim.Page, out var actual) && actual == claim;
    }

    internal bool AllowsDeviceSource(ushort page, ushort cardId, ushort offset, uint count)
    {
        VerifyOwner();
        return deviceClaims.TryGetValue(page, out var claim) && claim.CardId == cardId &&
            count > 0 && offset >= claim.SourceOffset &&
            (long)offset + count <= (long)claim.SourceOffset + claim.SourceCapacity;
    }

    internal bool AllowsDma(ushort cardId, byte direction, ushort page, ushort offset, uint count)
    {
        VerifyOwner();
        if (deviceClaims.TryGetValue(page, out var claim))
            return claim.CardId == cardId && direction == X0DmaDirection.RamToCard &&
                offset == claim.SourceOffset && AllowsDeviceSource(page, cardId, offset, count);
        return !deviceClaims.Values.Any(claim => claim.CardId == cardId && claim.ExclusiveSource);
    }

    internal bool TryReserveCardPages(ReadOnlySpan<ushort> pages)
    {
        var claims = new ExpansionDevicePageClaim[pages.Length];
        for (var index = 0; index < pages.Length; index++)
            claims[index] = new(pages[index], ushort.MaxValue, true, 0, 0, false);
        return TryClaimDevicePages(claims);
    }

    /// <summary>The controller admits lifecycle/execution first. This owner-only operation validates
    /// every page and epoch before changing a byte or publishing any of the four claims.</summary>
    public bool TryRepurpose(ExpansionPageGrant expected, ExpansionPageMap pages)
    {
        VerifyOwner();
        if (expected.UnitId is 0 or > ComputeCount + DriverCount ||
            expected.UnitId > ComputeCount && expected.UnitId < DriverFirst ||
            grants[expected.UnitId - 1] != expected ||
            expected.ResourceEpoch == ulong.MaxValue || expected.MapEpoch == ulong.MaxValue) return false;
        var first = expected.UnitId <= ComputeCount ? FirstComputePage : FirstDriverPage;
        for (var i = 0; i < 4; i++)
        {
            var page = pages[i];
            var reservedBank = exchangeOwner == expected.UnitId ? exchangePreviousBank : mainBank;
            if (page < first || page == reservedBank ||
                exchangeOwner != 0 && exchangeOwner != expected.UnitId && page == exchangePreviousBank ||
                deviceClaims.ContainsKey(page) || pageOwners[page] != 0 && pageOwners[page] != expected.UnitId) return false;
            for (var previous = 0; previous < i; previous++)
                if (pages[previous] == page) return false;
        }
        var next = new ExpansionPageGrant(expected.UnitId, checked(expected.ResourceEpoch + 1),
            checked(expected.MapEpoch + 1), pages);
        for (var i = 0; i < 4; i++)
        {
            if (!pages.Contains(expected.Pages[i]) && pageEpochs[expected.Pages[i]] == ulong.MaxValue ||
                !expected.Pages.Contains(pages[i]) && pageEpochs[pages[i]] == ulong.MaxValue) return false;
        }
        RevokeExchange(expected.UnitId);
        // No callbacks, allocations or fallible validation inside the publication interval.
        for (var i = 0; i < 4; i++) ram.AsSpan(Physical(pages[i]), PageBytes).Clear();
        for (var i = 0; i < 4; i++)
        {
            if (!pages.Contains(expected.Pages[i])) pageEpochs[expected.Pages[i]]++;
            if (!expected.Pages.Contains(pages[i])) pageEpochs[pages[i]]++;
        }
        for (var i = 0; i < 4; i++) pageOwners[expected.Pages[i]] = 0;
        for (var i = 0; i < 4; i++) pageOwners[pages[i]] = (byte)expected.UnitId;
        grants[expected.UnitId - 1] = next;
        return true;
    }

    internal bool TrySelectMainBank(ushort page)
    {
        VerifyOwner();
        if (exchangeOwner != 0) return page == mainBank;
        if (pageOwners[page] != 0 || deviceClaims.TryGetValue(page, out var claim) && !claim.MainMappable) return false;
        mainBank = page;
        return true;
    }

    internal bool MainOwns(ushort page)
    {
        VerifyOwner();
        return pageOwners[page] == 0;
    }

    internal bool IsDevicePageMainMappable(ushort page)
    {
        VerifyOwner();
        return !deviceClaims.TryGetValue(page, out var claim) || claim.MainMappable;
    }

    internal bool MainCanMap(ushort page) => MainOwns(page) && IsDevicePageMainMappable(page);

    internal bool MainCanDma(ushort page)
    {
        VerifyOwner();
        return pageOwners[page] == 0 && !deviceClaims.ContainsKey(page);
    }

    internal ulong PageEpoch(ushort page)
    {
        VerifyOwner();
        return pageEpochs[page];
    }

    /// <summary>Managed driver routing (X5 profile): the driver's arena slot access goes through
    /// the driver's own current grant; a page the grant no longer owns faults (InvalidOperationException)
    /// instead of reaching another unit's bytes. The map preflighted the grant at map time; a root
    /// repurpose since then frees the page and the stale slot is a denial, not a crash.</summary>
    internal byte RouteRead(ushort unitId, uint page, ushort intra) => ram[Route(unitId, page) + intra];
    internal void RouteWrite(ushort unitId, uint page, ushort intra, byte value) => ram[Route(unitId, page) + intra] = value;
    private int Route(ushort unitId, uint page)
    {
        VerifyOwner();
        if (unitId is < DriverFirst or > DriverLast)
            throw new InvalidOperationException("Managed arena routing belongs to a driver unit.");
        var grant = grants[unitId - 1];
        var p = (ushort)page;
        if (!grant.Owns(p) || pageOwners[p] != unitId)
            throw new InvalidOperationException("The driver page is no longer owned by its grant.");
        return Physical(p);
    }

    internal byte Read(ExpansionPageGrant grant, ushort address) => ram[Resolve(grant, address)];
    internal void Write(ExpansionPageGrant grant, ushort address, byte value)
    {
        var physical = Resolve(grant, address);
        if (exchangeOwner == grant.UnitId && address >= 0xc000) return;
        ram[physical] = value;
    }

    private int Resolve(ExpansionPageGrant grant, ushort address)
    {
        VerifyOwner();
        if (grant.UnitId is 0 or > ComputeCount + DriverCount || grants[grant.UnitId - 1] != grant)
            throw new InvalidOperationException("The compute memory lease is stale.");
        var page = grant.Pages[address >> 14];
        if (pageOwners[page] != grant.UnitId)
            throw new InvalidOperationException("The compute page is no longer owned by this lease.");
        return checked(Physical(page) + (address & 0x3fff));
    }

    private static int Physical(ushort page) => checked(0x10000 + page * PageBytes);
    private void VerifyOwner()
    {
        if (Environment.CurrentManagedThreadId != owner)
            throw new InvalidOperationException("Expansion arena access belongs to the emulation owner.");
    }
}

/// <summary>Compute memory view: no backing array, physical-page access or mutable map escape.</summary>
public sealed class ExpansionMemoryLease
{
    private readonly ExpansionArena arena;
    private readonly ExpansionPageGrant grant;
    internal ExpansionMemoryLease(ExpansionArena arena, ExpansionPageGrant grant)
    {
        this.arena = arena;
        this.grant = grant;
    }
    public byte Read(ushort address) => arena.Read(grant, address);
    public void Write(ushort address, byte value) => arena.Write(grant, address, value);
    public byte[] Copy()
    {
        // Check before allocation as well as on every access; never lend the arena's array.
        _ = Read(0);
        var copy = new byte[0x10000];
        for (var address = 0; address < copy.Length; address++) copy[address] = Read((ushort)address);
        return copy;
    }
}
