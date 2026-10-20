namespace Hc2000.Machine.Expansion;

/// <summary>Four logical 16-KiB pages, in 0000/4000/8000/C000 order. Never a mutable map.</summary>
public readonly record struct ExpansionPageMap(ushort Page0, ushort Page1, ushort Page2, ushort Page3)
{
    public ushort this[int index] => index switch
    {
        0 => Page0, 1 => Page1, 2 => Page2, 3 => Page3,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
}

public readonly record struct ExpansionPageGrant(ushort UnitId, ulong ResourceEpoch, ulong MapEpoch,
    ExpansionPageMap Pages);

/// <summary>Owner-side arena admission; does not contain CPU, controller or scheduler references.</summary>
public sealed partial class ExpansionArena
{
    public const int ComputeCount = 16;
    public const int PageBytes = 0x4000;
    // Compute arena pages 512-575 (map v2, note-20261018-234241).
    public const int FirstComputePage = 512;
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly byte[] ram;
    // Zero denotes the main pool. Roster pages are private; a conflicting main latch is refused in the constructor.
    private readonly byte[] pageOwners = new byte[65536];
    private readonly ExpansionPageGrant[] grants = new ExpansionPageGrant[ComputeCount];
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
            var map = new ExpansionPageMap(first, (ushort)(first + 1), (ushort)(first + 2), (ushort)(first + 3));
            grants[id - 1] = new(id, 1, 1, map);
            for (var i = 0; i < 4; i++)
            {
                pageOwners[map[i]] = (byte)id;
                ram.AsSpan(Physical(map[i]), PageBytes).Clear();
            }
        }
    }

    public ExpansionPageGrant GetGrant(ushort unitId)
    {
        VerifyOwner();
        if (unitId is 0 or > ComputeCount) throw new ArgumentOutOfRangeException(nameof(unitId));
        return grants[unitId - 1];
    }

    public ExpansionMemoryLease Acquire(ushort unitId) => new(this, GetGrant(unitId));

    /// <summary>The controller admits lifecycle/execution first. This owner-only operation validates
    /// every page and epoch before changing a byte or publishing any of the four claims.</summary>
    public bool TryRepurpose(ExpansionPageGrant expected, ExpansionPageMap pages)
    {
        VerifyOwner();
        if (expected.UnitId is 0 or > ComputeCount || grants[expected.UnitId - 1] != expected ||
            expected.ResourceEpoch == ulong.MaxValue || expected.MapEpoch == ulong.MaxValue) return false;
        for (var i = 0; i < 4; i++)
        {
            var page = pages[i];
            var reservedBank = exchangeOwner == expected.UnitId ? exchangePreviousBank : mainBank;
            if (page < FirstComputePage || page == reservedBank ||
                exchangeOwner != 0 && exchangeOwner != expected.UnitId && page == exchangePreviousBank ||
                pageOwners[page] != 0 && pageOwners[page] != expected.UnitId) return false;
            for (var previous = 0; previous < i; previous++)
                if (pages[previous] == page) return false;
        }
        var next = new ExpansionPageGrant(expected.UnitId, checked(expected.ResourceEpoch + 1),
            checked(expected.MapEpoch + 1), pages);
        RevokeExchange(expected.UnitId);
        // No callbacks, allocations or fallible validation inside the publication interval.
        for (var i = 0; i < 4; i++) ram.AsSpan(Physical(pages[i]), PageBytes).Clear();
        for (var i = 0; i < 4; i++) pageOwners[expected.Pages[i]] = 0;
        for (var i = 0; i < 4; i++) pageOwners[pages[i]] = (byte)expected.UnitId;
        grants[expected.UnitId - 1] = next;
        return true;
    }

    internal bool TrySelectMainBank(ushort page)
    {
        VerifyOwner();
        if (exchangeOwner != 0) return page == mainBank;
        if (pageOwners[page] != 0) return false;
        mainBank = page;
        return true;
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
        if (grant.UnitId is 0 or > ComputeCount || grants[grant.UnitId - 1] != grant)
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
