using Hc2000.Machine.Network;

namespace Hc2000.App.Network;

// A stream record is a two-byte little-endian length followed by one complete CP/NET frame.
// This only frames bytes; a live gateway must separately establish authority and authenticate.
internal sealed class CpNetStreamFraming
{
    private readonly byte[] buffer = new byte[CpNetFrame.MaxFrameBytes];
    private int prefixCount, length, received;
    private bool failed;

    public static byte[] Encode(CpNetFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var bytes = new byte[2 + frame.Bytes.Length];
        bytes[0] = (byte)frame.Bytes.Length;
        bytes[1] = (byte)(frame.Bytes.Length >> 8);
        frame.Bytes.CopyTo(bytes.AsSpan(2));
        return bytes;
    }

    // Consume at most one record; caller retains any remaining input. Malformed input poisons
    // this reader so a connection cannot silently resynchronize after losing framing.
    public bool Consume(ReadOnlySpan<byte> input, out int consumed, out CpNetFrame? frame)
    {
        consumed = 0;
        frame = null;
        if (failed) return false;
        while (prefixCount < 2 && consumed < input.Length)
        {
            length |= input[consumed++] << (8 * prefixCount++);
            if (prefixCount == 2 && (length < CpNetFrame.HeaderBytes + 1 || length > buffer.Length))
            {
                failed = true;
                return false;
            }
        }
        if (prefixCount < 2) return true;
        var count = Math.Min(length - received, input.Length - consumed);
        input.Slice(consumed, count).CopyTo(buffer.AsSpan(received));
        consumed += count;
        received += count;
        if (received != length) return true;
        if (!CpNetFrame.TryParse(buffer.AsSpan(0, length), out frame, out _))
        {
            failed = true;
            return false;
        }
        prefixCount = length = received = 0;
        return true;
    }
}
