using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;

namespace Hc2000.Machine.Capabilities;

/// <summary>The machine identity record, decision hc2000-machine-identity-bank-v1 (doc contract v2): sixteen read-only
/// bytes a guest reads with IN A,(C), byte n at port 0x609D + 0x100*n (0x609D..0x6F9D). Every extended profile
/// answers it; the faithful hc2000-v1 and hc2000-v2 never get the mapping and read 0xFF there. The 0x..9D family is
/// also safe on a COBRA, which decodes the low byte only and reads its 8255 port B there.
/// <para>Record v1: 0-3 'H','C','2','K'; 4 layout version 1; 5 model id (1 HC2000); 6 revision code (1 HC2000
/// extended v1); 7-10 capability bits 0-31, low byte first (<see cref="Bits"/>, each one a
/// <see cref="CapabilityCatalog"/> row); 11 HDD drive count (C: to F:); 12 paged RAM beyond the 64K map as log2
/// bytes, 0 for none; 13-14 M: drive size in 1024-byte blocks, low byte first; 15 the low byte of the sum of bytes
/// 0-14. A reader trusts the record only when the four signature bytes and the checksum match.</para></summary>
public sealed class IdentityRecord : IPortDevice
{
    public const ushort FirstPort = 0x609d;
    public const ushort PortMask = 0xf0ff;
    public const byte LayoutVersion = 1;
    public const byte ModelHc2000 = 1;
    public const byte RevisionHc2000ExtendedV1 = 1;

    /// <summary>Capability bit n is set when the catalog row has the value (null: when the row is present at all).
    /// The X0 mailbox and the fixed sixteen compute units come with the side Z80s, so their bits follow that row;
    /// X1 is the text overlay; X5 is the driver roster, which the catalog lists for the X5 profile only.</summary>
    public static IReadOnlyList<(int Bit, string Name, string Row, string? Value)> Bits { get; } =
    [
        (0, "hdd", "hdd", "true"),
        (1, "paged-ram", "memory", "paged"),
        (2, "m-drive", "m-view", "true"),
        (3, "tape", "tape-control", "true"),
        (4, "cpnet", "c-net", "true"),
        (5, "x0", "side-z80", "16"),
        (6, "x1", "text-overlay", "true"),
        (7, "x2", "x2-vga", "true"),
        (8, "x3", "x3-sound", "true"),
        (9, "x4", "x4-math", "true"),
        (10, "x5", "driver-z80", null),
        (11, "side-z80", "side-z80", "16"),
        (12, "expansion-fixed-16", "side-z80", "16"),
        (13, "ram-banks", "memory", "banks"),
        (14, "blockport", "block-disk", "true"),
        (15, "x6", "x6-net", "true")
    ];

    private readonly byte[] bytes;

    private IdentityRecord(byte[] bytes) => this.bytes = bytes;

    /// <summary>The sixteen bytes in port order.</summary>
    public ReadOnlySpan<byte> Bytes => bytes;

    /// <summary>The record a composed machine answers, or null for a faithful profile. X2, X3 and X4 are composition
    /// choices, not profile fields, so the caller names the cards it fitted. Bit 15 is the X6 network card
    /// (decision hc2000-x6-net-device-v1).</summary>
    public static IdentityRecord? FromProfile(MachineProfile profile, bool x2 = false, bool x3 = false, bool x4 = false, bool x6 = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Name is "hc2000-v1" or "hc2000-v2") return null;
        // These profiles report the same HDD hardware; their ROM identities are not interchangeable at launch.
        var name = profile.Name is "hc2000-v2-hdd-guest-v1" or "hc2000-v2-hdd-cpm3-v1" ? "hc2000-v2-hdd-v1" : profile.Name;
        var set = CapabilityCatalog.FromProfile(name, profile.Video.TextOverlay, profile.CpuTurboMultiplier,
            profile.ExpansionDriverCount, x2, x3, x4, x6)
            ?? throw new ArgumentException($"{profile.Name} has no capability catalog entry.", nameof(profile));
        var capabilities = 0u;
        foreach (var (bit, _, row, value) in Bits)
            if (value is null ? set.Has(row) : set.Get(row) == value)
                capabilities |= 1u << bit;
        var memory = set.Get("memory");
        var extraBytes = memory == "paged" ? (long)PagedRamPolicy.ExtraBytes
            : memory == "banks" ? (long)BankPolicy.BankCount * BankPolicy.BankBytes : 0;
        var mBytes = set.Get("m-view") != "true" ? 0
            : long.Parse(set.Get("m-drive-bytes") ?? "4194304", System.Globalization.CultureInfo.InvariantCulture);
        var record = new byte[16];
        "HC2K"u8.CopyTo(record);
        record[4] = LayoutVersion;
        record[5] = ModelHc2000;
        record[6] = RevisionHc2000ExtendedV1;
        for (var i = 0; i < 4; i++) record[7 + i] = (byte)(capabilities >> (8 * i));
        record[11] = (byte)(set.Get("hdd") == "true" ? 4 : 0);
        record[12] = (byte)(extraBytes == 0 ? 0 : System.Numerics.BitOperations.Log2((ulong)extraBytes));
        record[13] = (byte)(mBytes / 1024);
        record[14] = (byte)(mBytes / 1024 >> 8);
        var sum = 0;
        for (var i = 0; i < 15; i++) sum += record[i];
        record[15] = (byte)sum;
        return new(record);
    }

    /// <summary>The one mapping extended profiles add: read-only, 0x609D..0x6F9D.</summary>
    public PortMapping Mapping() => new(FirstPort, PortMask, this, PortAccess.Read);

    public byte Read(ushort port, long masterTick) => bytes[(port >> 8) - 0x60];

    public void Write(ushort port, byte value, long masterTick) { }
}
