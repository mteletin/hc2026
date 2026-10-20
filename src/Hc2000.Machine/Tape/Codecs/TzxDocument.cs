using System.Collections.Immutable;
using System.Buffers.Binary;

namespace Hc2000.Machine.Tape.Codecs;

/// <summary>Source blocks including their exact original framing, never an editable second timeline.</summary>
public sealed record TzxSourceBlock(uint Index, int Offset, byte Id, ImmutableArray<byte> Body);
public sealed record TzxDocument(byte Major, byte Minor, ImmutableArray<TzxSourceBlock> Blocks)
{
    public static TapeResult<TzxDocument> Parse(ReadOnlySpan<byte> bytes, TapeLimits limits)
    {
        try
        {
            if (limits.InputBytes < 0 || limits.SourceBlocks < 0 || limits.DecodedBytes < 0 ||
                bytes.Length > limits.InputBytes || 2L * bytes.Length > limits.DecodedBytes)
                throw new TzxException(TapeError.BoundsExceeded, "TZX input budget exceeded.");
            var cursor = new TzxCursor(bytes.ToArray());
            if (!cursor.Take(8).AsSpan().SequenceEqual("ZXTape!\x1a"u8))
                throw new TzxException(TapeError.InvalidFormat, "TZX signature is missing.");
            byte major = cursor.Byte(), minor = cursor.Byte();
            if (major != 1 || minor > 20)
                throw new TzxException(TapeError.UnsupportedVersion, $"TZX {major}.{minor} is unsupported.");
            var blocks = ImmutableArray.CreateBuilder<TzxSourceBlock>();
            while (cursor.Remaining > 0)
            {
                if (blocks.Count >= limits.SourceBlocks)
                    throw new TzxException(TapeError.BoundsExceeded, "TZX source block budget exceeded.");
                int offset = cursor.Position;
                byte id = cursor.Byte();
                int start = cursor.Position;
                switch (id)
                {
                    case 0x10: cursor.Skip(2); cursor.Skip(cursor.Word()); break;
                    case 0x11: cursor.Skip(15); cursor.Skip(cursor.UInt24()); break;
                    case 0x12: cursor.Skip(4); break;
                    case 0x13: cursor.Skip(cursor.Byte() * 2); break;
                    case 0x14: cursor.Skip(7); cursor.Skip(cursor.UInt24()); break;
                    case 0x15: cursor.Skip(5); cursor.Skip(cursor.UInt24()); break;
                    case 0x16: case 0x17:
                    {
                        uint length = cursor.Dword();
                        int fixedSize = id == 0x16 ? 0x28 : 0x16;
                        cursor.Skip(fixedSize - 7);
                        int n = cursor.UInt24();
                        int actual = checked(fixedSize + n);
                        if (length != actual && length != actual - 4)
                            throw new TzxException(TapeError.InvalidFormat, "Invalid bounded C64 length convention.");
                        cursor.Skip(n); break;
                    }
                    case 0x18: case 0x19: case 0x2A: case 0x2B:
                        cursor.Skip(cursor.Length32()); break;
                    case 0x20: case 0x23: case 0x24: cursor.Skip(2); break;
                    case 0x21: case 0x30: cursor.Skip(cursor.Byte()); break;
                    case 0x22: case 0x25: case 0x27: break;
                    case 0x26: cursor.Skip(cursor.Word() * 2); break;
                    case 0x28: case 0x32: cursor.Skip(cursor.Word()); break;
                    case 0x31: cursor.Skip(1); cursor.Skip(cursor.Byte()); break;
                    case 0x33: cursor.Skip(cursor.Byte() * 3); break;
                    case 0x34: cursor.Skip(8); break;
                    case 0x35: cursor.Skip(16); cursor.Skip(cursor.Length32()); break;
                    case 0x40: cursor.Skip(1); cursor.Skip(cursor.UInt24()); break;
                    case 0x5A: cursor.Skip(9); break;
                    default:
                        // Published post-1.10 extension framing is retained, never played silently.
                        cursor.Skip(cursor.Length32()); break;
                }
                blocks.Add(new((uint)blocks.Count, offset, id,
                    ImmutableArray.Create(cursor.Copy(start, cursor.Position - start))));
            }
            return TapeResult<TzxDocument>.Ok(new(major, minor, blocks.ToImmutable()));
        }
        catch (TzxException e) { return TapeResult<TzxDocument>.Fail(e.Error, e.Message); }
        catch (OverflowException) { return TapeResult<TzxDocument>.Fail(TapeError.BoundsExceeded, "TZX length overflow."); }
    }
}

internal sealed class TzxException(TapeError error, string detail) : Exception(detail)
{
    public TapeError Error { get; } = error;
}

internal sealed class TzxCursor(byte[] bytes)
{
    public int Position { get; private set; }
    public int Remaining => bytes.Length - Position;
    public byte Byte() => Take(1)[0];
    public ushort Word() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public short SignedWord() => unchecked((short)Word());
    public uint Dword() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int UInt24() { var b = Take(3); return b[0] | b[1] << 8 | b[2] << 16; }
    public int Length32()
    {
        uint value = Dword();
        if (value > int.MaxValue) throw new TzxException(TapeError.BoundsExceeded, "TZX length exceeds integer bounds.");
        return (int)value;
    }
    public void Skip(int length)
    {
        if (length < 0 || length > Remaining) throw new TzxException(TapeError.InvalidFormat, "Truncated TZX field/payload.");
        Position += length;
    }
    public byte[] Take(int length) { int start = Position; Skip(length); return Copy(start, length); }
    public byte[] Copy(int start, int length) => bytes.AsSpan(start, length).ToArray();
    public void End()
    {
        if (Remaining != 0) throw new TzxException(TapeError.InvalidFormat, "TZX block payload/count mismatch.");
    }
}
