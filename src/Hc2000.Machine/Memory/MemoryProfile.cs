namespace Hc2000.Machine.Memory;

public enum HcRomLayout { V1, V2 }

/// <summary>Spec 12: independent opt-in 65,536 by 16-KiB paged store.</summary>
public sealed record PagedRamDevice(string PolicyId = PagedRamPolicy.PolicyId)
{
    /// <summary>Tape-profile restriction; does not change the standalone spec 12 policy.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool BoundCpmWindow { get; init; }

    internal void Validate()
    {
        if (PolicyId is not (PagedRamPolicy.PolicyId or PagedRamPolicy.MTransferPolicyId or PagedRamPolicy.MTransferV2PolicyId)) throw new ArgumentException("Unknown paged-RAM policy.", nameof(PolicyId));
        if (BoundCpmWindow && PolicyId != PagedRamPolicy.PolicyId)
            throw new ArgumentException("The bound CP/M window requires plain paging.");
    }
}

public static class PagedRamPolicy
{
    public const string PolicyId = "hc2000-ram-paged-16k65536-v1";
    public const string MTransferPolicyId = "hc2000-ram-paged-m-transfer-16k65536-v1";
    public const string MTransferV2PolicyId = "hc2000-ram-paged-m-transfer-16k65536-v2";
    public const int PageBytes = 0x4000;
    public const int PageCount = 65536;
    public const int ExtraBytes = 0x40000000;
    public const int RamBytes = 0x40010000;

    /// <summary>M-transfer view under either identity (v1's 256-page M: or v2's 512-page M:); host wiring is identical.</summary>
    public static bool IsMTransfer(string? policyId) => policyId is MTransferPolicyId or MTransferV2PolicyId;
}

/// <summary>
/// Chosen virtual device, not hardware (hc2000-ram-banks-abi-v2): 128 8-KiB banks appended to the
/// existing physical Ram behind a selector port; window A000-BFFF (page 5). Profile-gated.
/// </summary>
public sealed record RamBankDevice(string PolicyId, int BankCount, int BankBytes)
{
    internal void Validate()
    {
        if (PolicyId != BankPolicy.PolicyId) throw new ArgumentException("Unknown RAM-bank device policy id (case-sensitive).", nameof(PolicyId));
        if (BankCount != 128) throw new ArgumentOutOfRangeException(nameof(BankCount), "Policy 8k128-v1 pins exactly 128 banks.");
        if (BankBytes != 0x2000) throw new ArgumentOutOfRangeException(nameof(BankBytes), "Policy 8k128-v1 pins 8-KiB banks.");
    }
}

/// <summary>Immutable device policy id (hc2000-ram-banks-abi-v2); the only defined policy.</summary>
public static class BankPolicy
{
    public const string PolicyId = "hc2000-ram-banks-8k128-v1";
    public const int BankCount = 128;
    public const int BankBytes = 0x2000;
    /// <summary>Exact active-policy physical RAM: base 64 KiB plus 1 MiB EXTRA (110000h), never 1 MiB total.</summary>
    public const int RamBytes = 0x110000;
    public const int BaseRamBytes = 0x10000;
    public const int WindowPage = 5;
    public const int SelectorPort = 0x8b;
    public const int CountPort = 0x8d;
    public const byte CountValue = 0x80;
    public const byte Disabled = 0xff;
}

/// <summary>Memory choices only; a MachineProfile can compose these without bank-port assumptions.</summary>
public sealed record MemoryProfile(
    HcRomLayout RomLayout = HcRomLayout.V2,
    int RamBytes = 0x10000,
    int RomBytes = 0x8000,
    int ShadowRomBytes = 0x4000,
    int ShadowRamBytes = 0x400,
    string RomProvenance = "unverified supplied images",
    RamBankDevice? RamBanks = null,
    PagedRamDevice? RamPaged = null)
{
    public If1TrapProfile If1Traps { get; init; } = new();

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(If1Traps);
        If1Traps.Validate();
        if (!Enum.IsDefined(RomLayout)) throw new ArgumentOutOfRangeException(nameof(RomLayout));
        if (string.IsNullOrWhiteSpace(RomProvenance)) throw new ArgumentException("Identify the supplied ROM configuration.", nameof(RomProvenance));
        if (RamPaged is { } paged)
        {
            paged.Validate();
            if (RamBanks is not null) throw new ArgumentException("ram.paged and ram.banks cannot compose.");
            if (RamBytes != PagedRamPolicy.RamBytes) throw new ArgumentOutOfRangeException(nameof(RamBytes));
            if (RomLayout != HcRomLayout.V2) throw new ArgumentException("ram.paged requires HC2000 v2.");
        }
        if (RamBanks is { } banks)
        {
            banks.Validate();
            // Fail-not-repair: the active policy pins the exact physical store size.
            if (RamBytes != BankPolicy.RamBytes)
                throw new ArgumentOutOfRangeException(nameof(RamBytes), $"With {banks.PolicyId} the physical RAM must be exactly 110000h (1,114,112) bytes; got {RamBytes:x}.");
        }
        else if (RamBytes < 0x10000 || RamBytes % 0x2000 != 0) throw new ArgumentOutOfRangeException(nameof(RamBytes));
        if (RomBytes < 0x8000 || RomBytes % 0x2000 != 0) throw new ArgumentOutOfRangeException(nameof(RomBytes));
        if (ShadowRomBytes < 0x4000 || ShadowRomBytes % 0x2000 != 0) throw new ArgumentOutOfRangeException(nameof(ShadowRomBytes));
        if (ShadowRamBytes is not (0x400 or 0x800)) throw new ArgumentOutOfRangeException(nameof(ShadowRamBytes));
    }
}
