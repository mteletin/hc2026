namespace Hc2000.Machine.Memory;

/// <summary>Instance-owned physical stores and CPU/M1 views. Bus-cycle classification and port decode belong to the caller.</summary>
public sealed class HcMemory
{
    private readonly byte[] ram;
    private readonly byte[] rom;
    private readonly byte[] shadowRom;
    private readonly byte[] shadowRam;

    public MemoryProfile Profile { get; }
    /// <summary>Faithful latches plus bank selector; starts disabled (0xff), never bank 0 by accident.</summary>
    public PagingLatches Latches { get; private set; }
    public IReadOnlyList<MemoryPage> Pages { get; private set; }
    public Memory<byte> Ram => ram;
    public ReadOnlyMemory<byte> Rom => rom;
    public ReadOnlyMemory<byte> ShadowRom => shadowRom;
    public Memory<byte> ShadowRam => shadowRam;
    /// <summary>Physical video address, independent of the CPU's C5/C7 alias.</summary>
    public int VideoBase => Profile.RamPaged?.PolicyId == PagedRamPolicy.PolicyId && Latches.PagedEnabled && !Latches.Cpm
        ? (Latches.PagedControl & 8) == 0 ? 0x4000 : 0x2c000
        : (Latches.Port7E & 8) == 0 ? 0x4000 : 0xc000;

    public byte ReadPagedPort(ushort port)
    {
        if (Profile.RamPaged is null || !Latches.PagedEnabled) return 0xff;
        return port switch
        {
            0x7ffd => (byte)((Latches.PagedBank & 7) | Latches.PagedControl),
            0x7efd => (byte)Latches.PagedBank,
            0x7dfd => (byte)(Latches.PagedBank >> 8),
            0x7cfd when Profile.RamPaged.PolicyId == PagedRamPolicy.MTransferPolicyId
                => (byte)(0x40 | (Latches.PagedTransfer ? 1 : 0) | (Latches.PagedControl & 0x20)),
            _ => 0xff
        };
    }

    public void WritePagedPort(ushort port, byte value)
    {
        if (Profile.RamPaged is null || !Latches.PagedEnabled) return;
        if (port == 0x7cfd && Profile.RamPaged.PolicyId == PagedRamPolicy.MTransferPolicyId)
        {
            // Escape is independent of the bank lock; invalid writes have no latch/history effect.
            if (value == 0) Update(Latches with { PagedTransfer = false });
            else if (value == 1 && Latches.Cpm && (Latches.PagedControl & 0x20) == 0)
                Update(Latches with { PagedTransfer = true });
            return;
        }
        if ((Latches.PagedControl & 0x20) != 0) return;
        var next = port switch
        {
            0x7ffd => Latches with { PagedBank = (ushort)((Latches.PagedBank & 0xfff8) | (value & 7)), PagedControl = (byte)(value & 0x38) },
            0x7efd => Latches with { PagedBank = (ushort)((Latches.PagedBank & 0xff00) | value) },
            0x7dfd => Latches with { PagedBank = (ushort)((Latches.PagedBank & 0xff) | (value << 8)) },
            _ => Latches
        };
        Update(next);
    }

    /// <param name="cpm">Supplied full 16-KiB half for either layout; no implicit padding or image substitution.</param>
    /// <remarks>
    /// The caller composes ROMs and records provenance in the profile. For v2 the canonical upper
    /// 8 KiB is cpm-hc2kzip.bin, never the shifted cpm.rom. A synthetic lower half must be explicitly
    /// composed and labelled by the caller; it is not verified hardware. All supplied bytes are preserved.
    /// </remarks>
    public HcMemory(MemoryProfile profile, ReadOnlySpan<byte> basic, ReadOnlySpan<byte> cpm, ReadOnlySpan<byte> if1)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        if (basic.Length != 0x4000) throw new ArgumentException("BASIC must be exactly 16 KiB.", nameof(basic));
        if (cpm.Length != 0x4000) throw new ArgumentException("Supply the full 16-KiB CP/M half, with explicit provenance.", nameof(cpm));
        if (if1.Length != 0x4000) throw new ArgumentException("IF1 must be exactly 16 KiB.", nameof(if1));
        Profile = profile;
        Latches = DisabledLatches with { PagedEnabled = profile.RamPaged is not null };
        ram = new byte[profile.RamBytes];
        rom = new byte[profile.RomBytes];
        shadowRom = new byte[profile.ShadowRomBytes];
        shadowRam = new byte[profile.ShadowRamBytes];
        basic.CopyTo(rom);
        cpm.CopyTo(rom.AsSpan(0x4000));
        if1.CopyTo(shadowRom);
        Pages = MemoryPaging.Build(Profile, Latches);
    }

    /// <summary>Reset state: faithful latches, bank window disabled (hc2000-ram-banks-abi-v2).</summary>
    public static PagingLatches DisabledLatches { get; } = new(0, false, false, 0xff);

    public MemoryLocation Resolve(ushort address) => Pages[address >> 13].Resolve(address & 0x1fff);

    public byte Read(ushort address)
    {
        var location = Resolve(address);
        return Store(location.Store)[location.Offset];
    }

    /// <summary>
    /// Memory-only M1 view. The machine bus must synchronize its scheduler at the exact
    /// CPU access timestamp before calling this, and only for actual opcode M1 fetches.
    /// Ordinary operands, HALT refresh and interrupt acknowledge use Read instead.
    /// </summary>
    public byte ReadM1(ushort address)
    {
        var traps = Profile.If1Traps;
        if (address == traps.PageInFirst || address == traps.PageInSecond)
        {
            SetShadow(true);
            return Read(address);
        }
        if (address == traps.PageOut)
        {
            // The page-out M1 itself still executes from the active shadow map; only subsequent
            // accesses see the main ROM. This preserves the IF1 RET at 0700.
            var opcode = Read(address);
            SetShadow(false);
            return opcode;
        }
        return Read(address);
    }

    public void Write(ushort address, byte value)
    {
        var location = Resolve(address);
        if (location.Writable) Store(location.Store)[location.Offset] = value;
    }

    /// <summary>Only D0..D3 are latched. D2 locks further 7E writes, not C5/C7 or IF1.</summary>
    public void WritePort7E(byte value)
    {
        if ((Latches.Port7E & 4) == 0) Update(Latches with { Port7E = (byte)(value & 0x0f) });
    }

    public void SetCpm(bool value) => Update(Latches with { Cpm = value });
    public void SetShadow(bool value) => Update(Latches with { Shadow = value });

    /// <summary>
    /// Bank selector port 8B semantics (hc2000-ram-banks-abi-v2): 00..7F selects that bank,
    /// FF disables the window (it does NOT select bank 127), and 80..FE are invalid: ignored
    /// without changing selector, pages, RAM, devices, scheduler or queues. On a device-less
    /// profile this is an inert no-op, matching the absent-device unmapped-port behavior.
    /// Readback of the selector port is the confirmation; no hidden latch or error state.
    /// Invalid values and same-value writes must not rebuild the page table.
    /// </summary>
    public void SetBank(byte value)
    {
        if (Profile.RamBanks is not { } device) return;
        var next = value == 0xff || value < device.BankCount
            ? Latches with { BankSelector = value }
            : Latches; // invalid 80..FE: preserve, no history latch
        if (next != Latches) Update(next);
    }

    /// <summary>Reset latches only; never clears physical stores. Bank selector returns to disabled (0xff).</summary>
    public void ResetPaging() => Update(DisabledLatches with { PagedEnabled = Latches.PagedEnabled });

    private void Update(PagingLatches value)
    {
        if (value == Latches) return;
        Latches = value;
        Pages = MemoryPaging.Build(Profile, value);
    }

    /// <summary>Restores the page-table inputs of a captured snapshot (snapshot restore).</summary>
    internal void RestoreLatches(PagingLatches value) => Update(value);

    private byte[] Store(MemoryStore store) => store switch
    {
        MemoryStore.Ram => ram,
        MemoryStore.Rom => rom,
        MemoryStore.ShadowRom => shadowRom,
        MemoryStore.ShadowRam => shadowRam,
        _ => throw new ArgumentOutOfRangeException(nameof(store))
    };
}
