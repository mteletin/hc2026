using Hc2000.Machine.Floppy;
using Hc2000.Machine.Memory;

namespace Hc2000.Machine.Io;

public static class HcIoProfile
{
    /// <summary>
    /// Schematic/ROM decoding. F7 carries TX/RX and EF handshake (hardware.md overrides spec table typos).
    /// Unknown even 04/06 FDC aliases are NOT enabled: the selected odd-only model is unverified.
    /// Read/write callbacks for 04d/04e are supplied by machine construction, never host adapters here.
    /// </summary>
    public static IReadOnlyList<PortMapping> Faithful(HcMemory memory, IPortDevice ula,
        IPortDevice driveLatch, IPortDevice fdcStatus, IPortDevice fdcData,
        IPortDevice serialF7, IPortDevice handshakeEf)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return [
            new(0x80, 0x81, ula, PortAccess.ReadWrite),
            new(0x00, 0x81, new PortDevice(write: (_, value, _) => memory.WritePort7E(value)), PortAccess.Write),
            new(0x05, 0xfd, driveLatch, PortAccess.ReadWrite),
            new(0x85, 0xff, fdcStatus, PortAccess.Read),
            new(0x87, 0xff, fdcData, PortAccess.ReadWrite),
            new(0xc5, 0xfd, new PortDevice(write: (port, _, _) => memory.SetCpm((port & 2) != 0)), PortAccess.Write),
            new(0xf7, 0xff, serialF7, PortAccess.ReadWrite),
            new(0xef, 0xff, handshakeEf, PortAccess.ReadWrite),
            new(0xfb, 0xff, new PortDevice(), PortAccess.ReadWrite)
        ];
    }

    /// <summary>
    /// Same faithful decode plus the profile-gated banked-RAM registers (hc2000-ram-banks-abi-v2):
    /// low-byte 8B R/W selector (readback = confirmation; OUT 00..7F selects, FF disables,
    /// 80..FE ignored) and low-byte 8D R constant 0x80 count/presence; OUT 8D is a no-op.
    /// 8B/8D/8F stay unallocated when the profile has no device (unmapped-port behavior kept).
    /// </summary>
    public static IReadOnlyList<PortMapping> WithRamBanks(HcMemory memory, IPortDevice ula,
        IPortDevice driveLatch, IPortDevice fdcStatus, IPortDevice fdcData,
        IPortDevice serialF7, IPortDevice handshakeEf)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (memory.Profile.RamBanks is not { } device) throw new ArgumentException("Profile has no RAM-bank device; use Faithful.", nameof(memory));
        device.Validate();
        return [
            new(0x8b, 0xff, new PortDevice(
                read: (_, _) => memory.Latches.BankSelector,
                write: (_, value, _) => memory.SetBank(value)), PortAccess.ReadWrite),
            new(0x8d, 0xff, new PortDevice(read: (_, _) => BankPolicy.CountValue), PortAccess.Read),
            .. Faithful(memory, ula, driveLatch, fdcStatus, fdcData, serialF7, handshakeEf)
        ];
    }

    /// <summary>Exact spec-12 ports; every other xxFD address remains unallocated.</summary>
    public static IReadOnlyList<PortMapping> WithPagedRam(HcMemory memory, IPortDevice ula,
        IPortDevice driveLatch, IPortDevice fdcStatus, IPortDevice fdcData,
        IPortDevice serialF7, IPortDevice handshakeEf)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (memory.Profile.RamPaged is null) throw new ArgumentException("Profile has no paged-RAM device.", nameof(memory));
        var device = new PortDevice(read: (port, _) => memory.ReadPagedPort(port),
            write: (port, value, _) => memory.WritePagedPort(port, value));
        return [
            new(0x7ffd, 0xffff, device, PortAccess.ReadWrite),
            new(0x7efd, 0xffff, device, PortAccess.ReadWrite),
            new(0x7dfd, 0xffff, device, PortAccess.ReadWrite),
            .. memory.Profile.RamPaged.PolicyId == PagedRamPolicy.MTransferPolicyId
                ? new PortMapping[] { new(0x7cfd, 0xffff, device, PortAccess.ReadWrite) } : [],
            .. Faithful(memory, ula, driveLatch, fdcStatus, fdcData, serialF7, handshakeEf)
        ];
    }

    /// <summary>Attach the block-disk ports only to the explicitly typed blockport or HDD profile.</summary>
    public static IReadOnlyList<PortMapping> WithBlockDisk(MachineProfile profile, HcMemory memory,
        IPortDevice ula, IPortDevice driveLatch, IPortDevice fdcStatus, IPortDevice fdcData,
        IPortDevice serialF7, IPortDevice handshakeEf, BlockDiskPort blockDisk)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(blockDisk);
        profile.Validate();
        if (profile.BlockDisk is not (BlockDiskPolicy.V1 or BlockDiskPolicy.HddV1))
            throw new ArgumentException("Only blockport/HDD profiles install ports 81/83/89.", nameof(profile));
        if (blockDisk.HddEnabled != (profile.BlockDisk == BlockDiskPolicy.HddV1))
            throw new ArgumentException("Block-disk device and profile policy disagree.", nameof(blockDisk));
        return [.. Faithful(memory, ula, driveLatch, fdcStatus, fdcData, serialF7, handshakeEf),
            .. blockDisk.Mappings()];
    }

    /// <summary>Faithful decode plus the spec 14 tape-control endpoint, only on the tape-control identity.</summary>
    public static IReadOnlyList<PortMapping> WithTapeControl(MachineProfile profile, HcMemory memory,
        IPortDevice ula, IPortDevice driveLatch, IPortDevice fdcStatus, IPortDevice fdcData,
        IPortDevice serialF7, IPortDevice handshakeEf, Tape.TapeControlPort tapeControl)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tapeControl);
        profile.Validate();
        if (!profile.TapeControl) throw new ArgumentException("Only the tape-control profile installs 0091.", nameof(profile));
        return [.. Faithful(memory, ula, driveLatch, fdcStatus, fdcData, serialF7, handshakeEf), .. tapeControl.Mappings()];
    }
}
