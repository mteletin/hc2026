namespace Hc2000.Machine.Memory;

/// <summary>Physical paged-store ownership, never a CPU address or another latch.</summary>
public sealed record PagedMemoryRange(int FirstPage, int PageCount);

public sealed partial class HcMemory
{
    private sealed record Claim(object Owner, PagedMemoryRange Range, Action<ulong, byte> Store);
    private readonly List<Claim> pagedClaims = [];
    public IReadOnlyList<PagedMemoryRange> ClaimedPagedRanges => pagedClaims.Select(c => c.Range).ToArray();

    /// <summary>First 256 pages are permanently reserved for M:v1; claims never overlap them.</summary>
    public PagedMemoryRange ClaimPagedPages(object owner, int pages, Action<ulong, byte> store, int? firstPage = null)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(store);
        if (Profile.RamPaged?.PolicyId != PagedRamPolicy.PolicyId)
            throw new InvalidOperationException("Plain paged storage is required.");
        if (pages is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(pages));
        if (pagedClaims.Any(c => ReferenceEquals(c.Owner, owner))) throw new InvalidOperationException("Owner already has a range.");
        int first = 256;
        if (firstPage is { } requested)
        {
            if (requested < 256 || requested > 65536 - pages || pagedClaims.Any(c => requested < c.Range.FirstPage + c.Range.PageCount && c.Range.FirstPage < requested + pages))
                throw new InvalidOperationException("Restored range is reserved or already owned.");
            var restored = new PagedMemoryRange(requested, pages);
            pagedClaims.Add(new(owner, restored, store)); return restored;
        }
        foreach (var c in pagedClaims.OrderBy(c => c.Range.FirstPage))
        {
            if (first + pages <= c.Range.FirstPage) break;
            first = c.Range.FirstPage + c.Range.PageCount;
        }
        if (first + pages > 65536) throw new InvalidOperationException("No disjoint paged range.");
        var range = new PagedMemoryRange(first, pages);
        pagedClaims.Add(new(owner, range, store));
        return range;
    }

    public void ReleasePagedPages(object owner) => pagedClaims.RemoveAll(c => ReferenceEquals(c.Owner, owner));
    internal Memory<byte> ClaimedPagedBytes(object owner)
    {
        var range = pagedClaims.Single(c => ReferenceEquals(c.Owner, owner)).Range;
        return ram.AsMemory(0x10000 + range.FirstPage * PagedRamPolicy.PageBytes, range.PageCount * PagedRamPolicy.PageBytes);
    }
    private bool WriteClaimedPagedByte(int physicalOffset, byte value)
    {
        foreach (var c in pagedClaims)
        {
            int offset = physicalOffset - (0x10000 + c.Range.FirstPage * PagedRamPolicy.PageBytes);
            if ((uint)offset < (uint)(c.Range.PageCount * PagedRamPolicy.PageBytes))
            {
                c.Store((ulong)offset, value); return true;
            }
        }
        return false;
    }
}
