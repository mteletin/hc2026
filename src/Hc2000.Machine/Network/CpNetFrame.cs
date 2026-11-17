namespace Hc2000.Machine.Network;

/// <summary>The bounded format-0/1 logical record from the CP/NET 1.2 manual, Appendix B.
/// This is not TCP framing, function-specific validation, authentication or a card ABI.</summary>
public sealed class CpNetFrame
{
    public const int HeaderBytes = 5;
    public const int MaxPayloadBytes = 256;
    public const int MaxFrameBytes = HeaderBytes + MaxPayloadBytes;

    private readonly byte[] bytes;

    private CpNetFrame(ReadOnlySpan<byte> source) => bytes = source.ToArray();

    public byte Format => bytes[0];
    public byte Destination => bytes[1];
    public byte Source => bytes[2];
    public byte Function => bytes[3];
    public ReadOnlySpan<byte> Payload => bytes.AsSpan(HeaderBytes);
    public ReadOnlySpan<byte> Bytes => bytes;

    /// <summary>Admit exactly one complete logical frame, copying only after validation.
    /// A stream transport must assemble a bounded record first; trailing bytes are refused.</summary>
    public static bool TryParse(ReadOnlySpan<byte> source, out CpNetFrame? frame,
        out CpNetFrameError error)
    {
        frame = null;
        if (source.Length < HeaderBytes + 1 || source.Length > MaxFrameBytes)
        {
            error = CpNetFrameError.Length;
            return false;
        }
        if (source[0] > 1)
        {
            error = CpNetFrameError.Format;
            return false;
        }
        if (source[1] == 0xff || source[2] == 0xff)
        {
            error = CpNetFrameError.ProcessorId;
            return false;
        }
        if (source.Length != HeaderBytes + source[4] + 1)
        {
            error = CpNetFrameError.Length;
            return false;
        }
        frame = new(source);
        error = CpNetFrameError.None;
        return true;
    }
}

public enum CpNetFrameError { None, Length, Format, ProcessorId }
