using System.Buffers.Binary;
using System.Text;

namespace Hc2000.App.Network.Sftp;

/// <summary>SFTP version 3 packet types (draft-ietf-secsh-filexfer-02 §3).</summary>
internal static class SftpPacket
{
    public const byte Init = 1, Version = 2, Open = 3, Close = 4, Read = 5, Write = 6, Lstat = 7, Fstat = 8, Setstat = 9,
        Fsetstat = 10, Opendir = 11, Readdir = 12, Remove = 13, Mkdir = 14, Rmdir = 15, Realpath = 16, Stat = 17,
        Rename = 18, Readlink = 19, Symlink = 20, Status = 101, Handle = 102, Data = 103, Name = 104, Attrs = 105,
        Extended = 200, ExtendedReply = 201;

    public static string Describe(byte type) => type switch
    {
        Init => "INIT", Open => "OPEN", Close => "CLOSE", Read => "READ", Write => "WRITE", Lstat => "LSTAT",
        Fstat => "FSTAT", Setstat => "SETSTAT", Fsetstat => "FSETSTAT", Opendir => "OPENDIR", Readdir => "READDIR",
        Remove => "REMOVE", Mkdir => "MKDIR", Rmdir => "RMDIR", Realpath => "REALPATH", Stat => "STAT",
        Rename => "RENAME", Readlink => "READLINK", Symlink => "SYMLINK", Extended => "EXTENDED",
        _ => "type " + type.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>SFTP version 3 status codes (§7). Version 3 has no code for a bad name (SSH_FX_INVALID_FILENAME is 20, from
/// version 6, and OpenSSH and sshfs read it as an unknown status), so <see cref="BadName"/> is SSH_FX_FAILURE and its
/// message names the 8.3 rule.</summary>
internal static class SftpStatus
{
    public const uint Ok = 0, Eof = 1, NoSuchFile = 2, PermissionDenied = 3, Failure = 4, BadMessage = 5,
        NoConnection = 6, ConnectionLost = 7, OpUnsupported = 8, BadName = Failure;
}

/// <summary>OPEN's pflags and the ATTRS flags (§6.3, §5).</summary>
internal static class SftpFlags
{
    public const uint Read = 1, Write = 2, Append = 4, Create = 8, Truncate = 0x10, Exclusive = 0x20;
    public const uint Size = 1, UidGid = 2, Permissions = 4, Times = 8, Extended = 0x80000000;
}

/// <summary>A request the server cannot parse: the channel ends with this reason.</summary>
internal sealed class SftpMalformedException(string reason) : Exception(reason);

/// <summary>Reads the SFTP data types (big-endian, as SSH's) from one packet body. Anything short is malformed.</summary>
internal sealed class SftpReader(byte[] data)
{
    private int position;

    public byte Byte() => Take(1)[0];

    public uint UInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

    public ulong UInt64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));

    public byte[] String()
    {
        var count = UInt32();
        return Take(count > int.MaxValue ? -1 : (int)count).ToArray();
    }

    public string Text() => Encoding.UTF8.GetString(String());

    /// <summary>An ATTRS block, read whole, of which only the size counts: CP/M keeps no modes, owners or times, and its
    /// R/O attribute stays the authority whatever mode a client sets. Null when the block carries no size.</summary>
    public ulong? Attributes()
    {
        var flags = UInt32();
        ulong? size = (flags & SftpFlags.Size) != 0 ? UInt64() : null;
        if ((flags & SftpFlags.UidGid) != 0) { UInt32(); UInt32(); }
        if ((flags & SftpFlags.Permissions) != 0) UInt32();
        if ((flags & SftpFlags.Times) != 0) { UInt32(); UInt32(); }
        if ((flags & SftpFlags.Extended) != 0)
            for (var count = UInt32(); count > 0; count--) { String(); String(); }
        return size;
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > data.Length - position) throw new SftpMalformedException("the packet ends inside a field");
        var span = data.AsSpan(position, count);
        position += count;
        return span;
    }
}

/// <summary>Writes one SFTP packet: the length, the type, the request id (none for VERSION), then the fields.</summary>
internal sealed class SftpWriter
{
    private byte[] buffer = new byte[256];
    private int length = 4;

    public SftpWriter(byte type, uint? id = null)
    {
        Byte(type);
        if (id is { } value) UInt32(value);
    }

    public SftpWriter Byte(byte value)
    {
        Room(1)[0] = value;
        return this;
    }

    public SftpWriter UInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(Room(4), value);
        return this;
    }

    public SftpWriter UInt64(ulong value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(Room(8), value);
        return this;
    }

    public SftpWriter String(ReadOnlySpan<byte> bytes)
    {
        UInt32((uint)bytes.Length);
        bytes.CopyTo(Room(bytes.Length));
        return this;
    }

    public SftpWriter String(string text) => String(Encoding.UTF8.GetBytes(text));

    /// <summary>The packet with its length in front.</summary>
    public byte[] ToPacket()
    {
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)(length - 4));
        return buffer[..length];
    }

    private Span<byte> Room(int count)
    {
        if (length + count > buffer.Length) Array.Resize(ref buffer, Math.Max(buffer.Length * 2, length + count));
        var room = buffer.AsSpan(length, count);
        length += count;
        return room;
    }
}
