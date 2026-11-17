using System.Buffers.Binary;
using System.Text;

namespace Hc2000.App.Network.Ssh;

/// <summary>A failed SSH session, with a short reason the X6 runtime can show (for example "auth failed").</summary>
public sealed class SshException(string reason) : IOException(reason);

/// <summary>The SSH message numbers used here (RFC 4250 §4.1.2, RFC 4256, RFC 8308). 60 means a different message for
/// each login method.</summary>
internal static class SshMessage
{
    public const byte Disconnect = 1, Ignore = 2, Unimplemented = 3, Debug = 4, ServiceRequest = 5, ServiceAccept = 6,
        ExtInfo = 7, KexInit = 20, NewKeys = 21, KexEcdhInit = 30, KexEcdhReply = 31,
        UserauthRequest = 50, UserauthFailure = 51, UserauthSuccess = 52, UserauthBanner = 53,
        UserauthPkOk = 60, UserauthPasswordChange = 60, UserauthInfoRequest = 60, UserauthInfoResponse = 61,
        GlobalRequest = 80, RequestSuccess = 81, RequestFailure = 82,
        ChannelOpen = 90, ChannelOpenConfirmation = 91, ChannelOpenFailure = 92, ChannelWindowAdjust = 93,
        ChannelData = 94, ChannelExtendedData = 95, ChannelEof = 96, ChannelClose = 97, ChannelRequest = 98,
        ChannelSuccess = 99, ChannelFailure = 100;
}

/// <summary>Writes the SSH data types (RFC 4251 §5), big-endian, into a growing buffer. A message starts with its
/// number.</summary>
internal sealed class SshWriter
{
    private byte[] buffer = new byte[128];
    private int length;

    public SshWriter() { }

    public SshWriter(byte message) => Byte(message);

    public SshWriter Byte(byte value)
    {
        Room(1)[0] = value;
        return this;
    }

    public SshWriter Boolean(bool value) => Byte(value ? (byte)1 : (byte)0);

    public SshWriter UInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(Room(4), value);
        return this;
    }

    public SshWriter Raw(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(Room(bytes.Length));
        return this;
    }

    public SshWriter String(ReadOnlySpan<byte> bytes) => UInt32((uint)bytes.Length).Raw(bytes);

    public SshWriter String(string text) => String(Encoding.UTF8.GetBytes(text));

    public SshWriter NameList(IEnumerable<string> names) => String(string.Join(',', names));

    /// <summary>An mpint from an unsigned big-endian number: leading zeros dropped, one zero byte put back when the top
    /// bit is set, so it stays positive.</summary>
    public SshWriter Mpint(ReadOnlySpan<byte> magnitude)
    {
        magnitude = magnitude.TrimStart((byte)0);
        var positive = magnitude.Length > 0 && magnitude[0] >= 0x80;
        UInt32((uint)(magnitude.Length + (positive ? 1 : 0)));
        if (positive) Byte(0);
        return Raw(magnitude);
    }

    public byte[] ToArray() => buffer[..length];

    private Span<byte> Room(int count)
    {
        if (length + count > buffer.Length) Array.Resize(ref buffer, Math.Max(buffer.Length * 2, length + count));
        var room = buffer.AsSpan(length, count);
        length += count;
        return room;
    }
}

/// <summary>Reads the SSH data types from one message. Anything short or out of range is a malformed message.</summary>
internal sealed class SshReader(byte[] data, int start = 0)
{
    private int position = start;

    public byte Byte() => Take(1)[0];

    public bool Boolean() => Byte() != 0;

    public uint UInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

    public byte[] String()
    {
        var count = UInt32();
        return Take(count > int.MaxValue ? -1 : (int)count).ToArray();
    }

    public string Text() => Encoding.UTF8.GetString(String());

    public string[] NameList()
    {
        var names = Text();
        return names.Length == 0 ? [] : names.Split(',');
    }

    /// <summary>A non-negative mpint as its unsigned big-endian number, without leading zeros.</summary>
    public byte[] Mpint()
    {
        var value = String();
        if (value.Length > 0 && value[0] >= 0x80) throw new SshException("malformed message");
        return value.AsSpan().TrimStart((byte)0).ToArray();
    }

    /// <summary>An unsigned number widened to exactly <paramref name="size"/> bytes, as the crypto APIs want it.</summary>
    public static byte[] PadLeft(ReadOnlySpan<byte> magnitude, int size)
    {
        magnitude = magnitude.TrimStart((byte)0);
        if (magnitude.Length > size) throw new SshException("malformed message");
        var value = new byte[size];
        magnitude.CopyTo(value.AsSpan(size - magnitude.Length));
        return value;
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > data.Length - position) throw new SshException("malformed message");
        var span = data.AsSpan(position, count);
        position += count;
        return span;
    }
}
