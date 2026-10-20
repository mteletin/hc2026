using System.Buffers.Binary;

namespace Hc2000.Machine.Storage;

public sealed record HcFileHeader(byte Type, ushort Length, ushort Start, ushort Variables, ushort Autostart)
{
    public byte[] Encode()
    {
        var bytes = new byte[9]; bytes[0] = Type;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(3), Start);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(5), Variables);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(7), Autostart); return bytes;
    }
    public static HcFileHeader Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 9) throw new InvalidDataException("Truncated HC file header.");
        return new(bytes[0], BinaryPrimitives.ReadUInt16LittleEndian(bytes[1..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[3..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[5..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[7..]));
    }
}
public sealed record HcFile(CpmFile Directory, HcFileHeader? Header, string? Diagnostic, bool IsUntyped = false);

/// <summary>HC BASIC uses explicit 16-bit block allocations even on 40T disks.</summary>
public sealed class HcFs
{
    private readonly CpmFs cpm;
    public HcFs(CpmFs cpm)
    {
        if (!cpm.Parameters.HcHeader) throw new InvalidDataException("Not an HC BASIC profile.");
        this.cpm = cpm;
    }
    public IReadOnlyList<HcFile> List() => cpm.List().Select(Inspect).ToArray();
    public HcFile Inspect(CpmFile file)
    {
        try
        {
            var records = cpm.Get(file.Name, file.User);
            if (records.Length == 0 || records[0] > 3) return new(file, null, "Untyped/headerless file; raw records available through CpmFs.Get.", true);
            if (records.Length < 9) return new(file, null, "HC header absent/truncated.");
            var header = HcFileHeader.Decode(records);
            return new(file, header, header.Length > records.Length - 9 ? "HC header payload length exceeds recorded file." : null);
        }
        catch (InvalidDataException ex) { return new(file, null, ex.Message); }
    }
    public HcFileHeader Header(string name, int user = 0)
    {
        var records = cpm.Get(name, user);
        if (records.Length == 0 || records[0] > 3) throw new InvalidDataException("Untyped/headerless file has no HC header; use CpmFs.Get for raw records.");
        var header = HcFileHeader.Decode(records);
        if (header.Length > records.Length - 9) throw new InvalidDataException("HC header payload length exceeds recorded file.");
        return header;
    }
    public byte[] Get(string name, int user = 0)
    {
        var records = cpm.Get(name, user);
        if (records.Length == 0 || records[0] > 3) throw new InvalidDataException("Untyped/headerless file has no HC header; use CpmFs.Get for raw records.");
        var header = HcFileHeader.Decode(records);
        if (header.Length > records.Length - 9) throw new InvalidDataException("HC header payload length exceeds recorded file.");
        return records.AsSpan(9, header.Length).ToArray();
    }
    public void Put(string name, ReadOnlySpan<byte> data, int user = 0, byte type = 3, ushort start = 0,
        ushort variables = 0, ushort autostart = 0x8000)
    {
        if (type > 3) throw new InvalidDataException("Typed HC put requires Spectrum type 0..3; use CpmFs.Put for raw untyped files.");
        if (data.Length > ushort.MaxValue) throw new InvalidDataException("HC payload exceeds 16-bit length.");
        var header = new HcFileHeader(type, (ushort)data.Length, start, variables, autostart).Encode();
        var bytes = new byte[data.Length + 9]; header.CopyTo(bytes, 0); data.CopyTo(bytes.AsSpan(9)); cpm.Put(name, bytes, user);
    }
    public void Delete(string name, int user = 0) => cpm.Delete(name, user);
}
