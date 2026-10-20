using System.Buffers.Binary;

namespace Hc2000.Machine.Tape;

public sealed partial class TapeVolumeSession
{
    // Common RAM, outside the paged window and the resident payload. This is
    // guest coherence only; ending the host alias is the primary protection.
    public const int DescriptorAddress = 0x7400;
    internal ReadOnlySpan<byte> BindingMetadata => memory.Ram.Span.Slice(DescriptorAddress + 2, 6);

    private void InvalidateDescriptor() => memory.Ram.Span[DescriptorAddress] = 0;

    private void PublishDescriptor()
    {
        var bytes = memory.Ram.Span.Slice(DescriptorAddress, 20);
        bytes.Clear();
        bytes[1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[2..], checked((ushort)FirstPage));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..], checked((ushort)(ImageBytes / 4096)));
        bytes[6] = checked((byte)(Letter - 'A'));
        bytes[7] = boundWriteProtected ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], epoch);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[16..], ImageBytes);
        // Publish validity last, after both the metadata and mapping agree.
        bytes[0] = 1;
    }
}
