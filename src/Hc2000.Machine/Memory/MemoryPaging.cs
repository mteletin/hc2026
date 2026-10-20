namespace Hc2000.Machine.Memory;

public enum MemoryStore { Ram, Rom, ShadowRom, ShadowRam }
/// <summary>
/// Faithful latches plus the bank selector (hc2000-ram-banks-abi-v2). The selector is always
/// carried so a faithful-latch change rebuilds the table WITH the current selector; the profile
/// decides whether it has any effect. 0xff = window disabled (not bank 127).
/// </summary>
public readonly record struct PagingLatches(byte Port7E, bool Cpm, bool Shadow, byte BankSelector = 0xff,
    ushort PagedBank = 0, byte PagedControl = 0, bool PagedEnabled = false, bool PagedTransfer = false);
public readonly record struct MemoryLocation(MemoryStore Store, int Offset, bool Writable);

/// <summary>Offset is a physical array offset, never a CPU address or a 16-bit quantity.</summary>
public readonly record struct MemoryMapping(MemoryStore Store, int Offset, bool Writable, int AddressMask = 0x1fff)
{
    public MemoryLocation Resolve(int pageOffset) => new(Store, checked(Offset + (pageOffset & AddressMask)), Writable);
}

public readonly record struct MemorySubwindow(int Start, int Length, MemoryMapping Mapping);

/// <summary>An 8-KiB page with optional finer chip-select windows (the IF1 RAM).</summary>
public sealed class MemoryPage
{
    private readonly MemorySubwindow[] subwindows;
    public MemoryMapping Mapping { get; }
    public IReadOnlyList<MemorySubwindow> Subwindows { get; }

    public MemoryPage(MemoryMapping mapping, params MemorySubwindow[] subwindows)
    {
        Mapping = mapping;
        this.subwindows = (MemorySubwindow[])subwindows.Clone();
        Subwindows = Array.AsReadOnly(this.subwindows);
    }

    public MemoryLocation Resolve(int pageOffset)
    {
        if ((uint)pageOffset >= 0x2000) throw new ArgumentOutOfRangeException(nameof(pageOffset));
        for (var i = 0; i < subwindows.Length; i++)
        {
            var window = subwindows[i];
            if (pageOffset >= window.Start && pageOffset < window.Start + window.Length)
                return window.Mapping.Resolve(pageOffset - window.Start);
        }
        return Mapping.Resolve(pageOffset);
    }
}

/// <summary>Pure latch-to-page-table mapping. No ports, CPU events, device time or store contents.</summary>
public static class MemoryPaging
{
    public static IReadOnlyList<MemoryPage> Build(MemoryProfile profile, PagingLatches latches, PagedMemoryRange? boundRange = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        var mTransfer = PagedRamPolicy.IsMTransfer(profile.RamPaged?.PolicyId);
        var boundOnly = profile.RamPaged?.BoundCpmWindow == true;
        var window = profile.RamPaged is not null && latches.PagedEnabled
            && (!mTransfer || (latches.Cpm && latches.PagedTransfer))
            && (!boundOnly || (latches.Cpm && boundRange is { } range
                && latches.PagedBank >= range.FirstPage && latches.PagedBank < range.FirstPage + range.PageCount));
        var pages = new MemoryPage[8];
        for (var i = 0; i < pages.Length; i++)
            pages[i] = new(new(MemoryStore.Ram, i * 0x2000, true));
        if (latches.Cpm) pages[6] = new(new(MemoryStore.Ram, 0xe000, true));
        var half = (latches.Port7E & 1) * 0x4000;
        if ((latches.Port7E & 2) != 0)
        {
            pages[7] = new(new(MemoryStore.Rom, half + 0x2000, false));
        }
        else if (latches.Shadow)
        {
            pages[0] = new(new(MemoryStore.ShadowRom, 0, false));
            var ram = new MemoryMapping(MemoryStore.ShadowRam, 0, true, profile.ShadowRamBytes - 1);
            pages[1] = new(new(MemoryStore.ShadowRom, 0x2000, false),
                new(0x800, 0x800, ram), new(0x1800, 0x800, ram));
        }
        else
        {
            if (profile.RamPaged is not null && !mTransfer && latches.PagedEnabled && (!boundOnly || window))
                half = (latches.PagedControl & 0x10) == 0 ? 0 : 0x4000;
            pages[0] = new(new(MemoryStore.Rom, half, false));
            pages[1] = new(new(MemoryStore.Rom, half + 0x2000, false));
        }
        if (window)
        {
            var offset = checked(0x10000 + latches.PagedBank * PagedRamPolicy.PageBytes);
            var first = latches.Cpm ? 4 : 6;
            pages[first] = new(new(MemoryStore.Ram, offset, true));
            if (latches.Cpm || (latches.Port7E & 2) == 0)
                pages[first + 1] = new(new(MemoryStore.Ram, offset + 0x2000, true));
        }
        // Banked overlay (chosen virtual ABI, profile-gated): the faithful table is built FIRST
        // (including any IF1 subwindows); disabled selector returns it unchanged. Otherwise ONLY
        // page 5 is replaced with the selected bank at an int physical offset.
        if (profile.RamBanks is { } device && latches.BankSelector != 0xff)
        {
            device.Validate();
            var bank = latches.BankSelector;
            if (bank >= device.BankCount)
                throw new ArgumentOutOfRangeException(nameof(latches), $"Bank selector 0x{bank:x2} is outside 00..{device.BankCount - 1:x2} for {device.PolicyId}.");
            var offset = checked(BankPolicy.BaseRamBytes + checked(bank * device.BankBytes));
            pages[5] = new(new(MemoryStore.Ram, offset, true, 0x1fff));
        }
        return Array.AsReadOnly(pages);
    }
}
