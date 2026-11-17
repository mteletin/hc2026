using System.Buffers.Binary;

namespace Hc2000.Machine.Tape;

public sealed partial class TapeVolumeSession
{
    // Common RAM, outside the paged window and the resident payload. This is
    // guest coherence only; ending the host alias is the primary protection.
    public const int DescriptorAddress = 0x7400;
    // The full workstation: page 880 (its switch's state page, WsStatePage) at +0xC40 (WsTapeDesc in
    // src-library/extended/bios-upgrade/workstation/ws.layout.inc), a physical address in paged RAM.
    public const int WorkstationDescriptorAddress = 0x10000 + 880 * 0x4000 + 0xC40;
    private int Descriptor => fullWorkstation ? WorkstationDescriptorAddress : DescriptorAddress;
    // The six binding bytes (+2..+7) drawn from the session's authoritative state: first 16
    // KiB page, size in 4 KiB blocks, CP/M drive number and write-protect. This is exactly
    // what PublishDescriptor places in the guest descriptor, and what the host binder embeds
    // in the TAPEINIT header (TapeGuestInstaller). The guest may overwrite the RAM
    // descriptor between mounts, so the host must never read binding metadata back out of
    // that guest-visible channel; both paths below write from this single source.
    internal void WriteBinding(Span<byte> dest)
    {
        if (dest.Length != 6) throw new ArgumentException("The binding is six bytes.", nameof(dest));
        BinaryPrimitives.WriteUInt16LittleEndian(dest, checked((ushort)FirstPage));
        BinaryPrimitives.WriteUInt16LittleEndian(dest[2..], checked((ushort)(ImageBytes / 4096)));
        dest[4] = checked((byte)(Letter - 'A'));
        dest[5] = boundWriteProtected ? (byte)1 : (byte)0;
    }

    private void InvalidateDescriptor() => memory.Ram.Span[Descriptor] = 0;

    private void PublishDescriptor()
    {
        var bytes = memory.Ram.Span.Slice(Descriptor, 20);
        bytes.Clear();
        bytes[1] = 1;
        WriteBinding(bytes[2..8]);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], epoch);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[16..], ImageBytes);
        // Publish validity last, after both the metadata and mapping agree.
        bytes[0] = 1;
    }
}
