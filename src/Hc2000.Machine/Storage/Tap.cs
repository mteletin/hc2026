using System.Buffers.Binary;
using System.Text;
using Hc2000.Machine.Tape;

namespace Hc2000.Machine.Storage;

public sealed record TapeFile(string Name, byte Type, ushort Start, ushort Variables, byte[] Data)
{
    public HcFileHeader HcHeader => new(Type <= 3 ? Type : throw new InvalidDataException("Unsupported tape type."),
        checked((ushort)Data.Length), Type == 3 ? Start : (ushort)0, Variables, Type == 0 ? Start : (ushort)0x8000);
}
public sealed class Tap
{
    private readonly List<byte[]> blocks;
    private Tap(List<byte[]> blocks) => this.blocks = blocks;
    /// <summary>Detached copies of raw blocks, including flag/checksum; no named-file pairing.</summary>
    public IReadOnlyList<byte[]> CopyRawBlocks() => blocks.Select(b => b.ToArray()).ToArray();

    public static Tap Load(ReadOnlySpan<byte> bytes, int maxBytes = 64 * 1024 * 1024, int maxBlocks = 4096)
    {
        var result = LoadBounded(bytes, maxBytes, maxBlocks);
        return result.Success ? result.Value : throw new InvalidDataException(result.Failure!.Detail);
    }
    /// <summary>
    /// One native parser with explicit finite caps. The first reached refusal is returned;
    /// a reached bound may mask later corruption. Failed results carry no partial Tap.
    /// </summary>
    public static TapeResult<Tap> LoadBounded(ReadOnlySpan<byte> bytes, int maxBytes, int maxBlocks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBlocks);
        if (bytes.Length > maxBytes) return TapeResult<Tap>.Fail(TapeError.BoundsExceeded, "TAP byte bound exceeded.");
        var blocks = new List<byte[]>(); var offset = 0;
        while (offset < bytes.Length)
        {
            if (blocks.Count >= maxBlocks) return TapeResult<Tap>.Fail(TapeError.BoundsExceeded, "TAP block bound exceeded.");
            if (bytes.Length - offset < 2) return TapeResult<Tap>.Fail(TapeError.InvalidFormat, "Truncated TAP length.");
            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]); offset += 2;
            if (length < 2 || length > bytes.Length - offset) return TapeResult<Tap>.Fail(TapeError.InvalidFormat, "Truncated TAP block.");
            var block = bytes.Slice(offset, length).ToArray();
            if (block.Aggregate((byte)0, (x, y) => (byte)(x ^ y)) != 0) return TapeResult<Tap>.Fail(TapeError.InvalidFormat, "Invalid TAP checksum.");
            blocks.Add(block); offset += length;
        }
        return TapeResult<Tap>.Ok(new(blocks));
    }

    public byte[] Save()
    {
        using var output = new MemoryStream();
        foreach (var block in blocks)
        {
            var length = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(length, checked((ushort)block.Length));
            output.Write(length); output.Write(block);
        }
        return output.ToArray();
    }
    public IReadOnlyList<TapeFile> List()
    {
        var files = new List<TapeFile>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var header = blocks[i];
            if (header[0] != 0 || header.Length != 19) throw new InvalidDataException("TAP file lacks a standard header.");
            if (header[1] > 3 || i + 1 >= blocks.Count || blocks[i + 1][0] != 0xff) throw new InvalidDataException("TAP header lacks a data block.");
            var data = blocks[++i]; var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12));
            if (length != data.Length - 2) throw new InvalidDataException("TAP header/data length mismatch.");
            files.Add(new(Encoding.ASCII.GetString(header, 2, 10).TrimEnd(), header[1],
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14)), BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(16)), data.AsSpan(1, length).ToArray()));
        }
        return files;
    }
    public TapeFile Get(string name) => List().SingleOrDefault(f => f.Name == name) ?? throw new FileNotFoundException("Tape file not found: " + name);
    public void Put(TapeFile file)
    {
        if (file.Name.Length is < 1 or > 10 || file.Name.Any(c => c is < ' ' or > '~') || file.Type > 3 || file.Data.Length > 65533)
            throw new InvalidDataException("Invalid TAP name, type or payload length.");
        if (List().Any(f => f.Name == file.Name)) throw new InvalidDataException("Duplicate tape filename.");
        var header = new byte[19]; header[1] = file.Type; Encoding.ASCII.GetBytes(file.Name.PadRight(10)).CopyTo(header, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), (ushort)file.Data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), file.Start); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(16), file.Variables);
        var data = new byte[file.Data.Length + 2]; data[0] = 0xff; file.Data.CopyTo(data, 1);
        header[^1] = header[..^1].Aggregate((byte)0, (x, y) => (byte)(x ^ y));
        data[^1] = data[..^1].Aggregate((byte)0, (x, y) => (byte)(x ^ y)); blocks.Add(header); blocks.Add(data);
    }
}
