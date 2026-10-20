using System.Buffers.Binary;
using System.Text;

namespace Hc2000.Machine.Storage;

public enum DiskFormat { Extended, Standard, Raw }
public sealed record DiskGeometry(int Cylinders, int Sides, int Sectors, int SectorSize)
{
    public int Length => checked(Cylinders * Sides * Sectors * SectorSize);
    public void Validate()
    {
        if (Cylinders is < 1 or > 102 || Sides is < 1 or > 2 || Sectors is < 1 or > 29 ||
            SectorSize is < 128 or > 8192 || (SectorSize & (SectorSize - 1)) != 0)
            throw new InvalidDataException("Invalid disk geometry (cylinders,sides,sectors,sector-size).");
    }
}
public sealed class DiskSector
{
    internal DiskSector(byte c, byte h, byte r, byte n, byte st1, byte st2, int offset, byte[] data)
        => (C, H, R, N, ST1, ST2, Offset, Data) = (c, h, r, n, st1, st2, offset, data);
    public byte C { get; }
    public byte H { get; }
    public byte R { get; }
    public byte N { get; }
    public byte ST1 { get; }
    public byte ST2 { get; }
    public byte[] Data { get; }
    internal int Offset { get; }
}
public sealed record DiskTrack(int Cylinder, int Side, byte Gap, byte Filler, IReadOnlyList<DiskSector> Sectors);

/// <summary>Container-preserving disk model; no host I/O. Sector edits keep recorded lengths.</summary>
public sealed class Dsk
{
    private readonly byte[] image;
    private Dsk(byte[] image, DiskFormat format, int cylinders, int sides, List<DiskTrack> tracks)
        => (this.image, Format, Cylinders, Sides, Tracks) = (image, format, cylinders, sides, tracks.AsReadOnly());
    public DiskFormat Format { get; }
    public int Cylinders { get; }
    public int Sides { get; }
    public IReadOnlyList<DiskTrack> Tracks { get; }

    public static Dsk Load(ReadOnlySpan<byte> input, DiskGeometry? rawGeometry = null)
    {
        var bytes = input.ToArray();
        var extended = input.StartsWith("EXTENDED CPC DSK File\r\nDisk-Info\r\n"u8);
        var standard = input.StartsWith("MV - CPCEMU Disk-File\r\nDisk-Info\r\n"u8);
        if (!extended && !standard)
        {
            if (rawGeometry is null) throw new InvalidDataException("Unknown disk format; raw images require geometry.");
            rawGeometry.Validate();
            if (input.Length != rawGeometry.Length) throw new InvalidDataException("Raw image length does not match geometry.");
            var rawTracks = new List<DiskTrack>(); var position = 0;
            var n = (byte)System.Numerics.BitOperations.Log2((uint)(rawGeometry.SectorSize / 128));
            for (var c = 0; c < rawGeometry.Cylinders; c++)
                for (var h = 0; h < rawGeometry.Sides; h++)
                {
                    var sectors = new List<DiskSector>();
                    for (var r = 1; r <= rawGeometry.Sectors; r++)
                    {
                        sectors.Add(new((byte)c, (byte)h, (byte)r, n, 0, 0, position, bytes.AsSpan(position, rawGeometry.SectorSize).ToArray()));
                        position += rawGeometry.SectorSize;
                    }
                    rawTracks.Add(new(c, h, 0x4e, 0xe5, sectors.AsReadOnly()));
                }
            return new(bytes, DiskFormat.Raw, rawGeometry.Cylinders, rawGeometry.Sides, rawTracks);
        }
        if (bytes.Length < 256) throw new InvalidDataException("Truncated disk header.");
        var cylinders = bytes[48]; var sides = bytes[49];
        if (cylinders < 1 || sides is < 1 or > 2 || cylinders * sides > 204)
            throw new InvalidDataException("Invalid DSK track count.");
        var offset = 256; var tracks = new List<DiskTrack>();
        for (var i = 0; i < cylinders * sides; i++)
        {
            var length = extended ? bytes[52 + i] * 256 : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(50, 2));
            if (length == 0 && extended) { tracks.Add(new(i / sides, i % sides, 0, 0, Array.Empty<DiskSector>())); continue; }
            if (length < 256 || offset > bytes.Length - length) throw new InvalidDataException("Truncated DSK track.");
            var header = bytes.AsSpan(offset, 256);
            if (!header.StartsWith("Track-Info\r\n"u8) || header[21] > 29) throw new InvalidDataException("Invalid track header.");
            var sectors = new List<DiskSector>(); var position = offset + 256;
            for (var j = 0; j < header[21]; j++)
            {
                var id = header.Slice(24 + j * 8, 8);
                if (id[3] > 6 || header[20] > 6) throw new InvalidDataException("Invalid sector size code.");
                var size = extended ? BinaryPrimitives.ReadUInt16LittleEndian(id[6..]) : 128 << header[20];
                if (position > offset + length - size) throw new InvalidDataException("Sector exceeds recorded track size.");
                sectors.Add(new(id[0], id[1], id[2], id[3], id[4], id[5], position, bytes.AsSpan(position, size).ToArray()));
                position += size;
            }
            tracks.Add(new(header[16], header[17], header[22], header[23], sectors.AsReadOnly())); offset += length;
        }
        return new(bytes, extended ? DiskFormat.Extended : DiskFormat.Standard, cylinders, sides, tracks);
    }

    public byte[] Save()
    {
        var result = image.ToArray();
        foreach (var sector in Tracks.SelectMany(t => t.Sectors)) sector.Data.CopyTo(result, sector.Offset);
        return result;
    }

    public static Dsk Create(DiskGeometry geometry, DiskFormat format = DiskFormat.Extended, byte fill = 0xe5)
    {
        geometry.Validate();
        var raw = new byte[geometry.Length]; Array.Fill(raw, fill);
        var disk = Load(raw, geometry);
        return format == DiskFormat.Raw ? disk : Load(disk.ToContainer(format));
    }

    public DiskGeometry UniformGeometry()
    {
        var first = Tracks[0];
        if (first.Sectors.Count == 0) throw new InvalidDataException("Unformatted track cannot be represented as raw sectors.");
        var size = first.Sectors[0].Data.Length;
        foreach (var track in Tracks)
        {
            var ordered = track.Sectors.OrderBy(s => s.R).ToArray();
            if (ordered.Length != first.Sectors.Count || ordered.Where((s, i) => s.R != i + 1 || s.C != track.Cylinder || s.H != track.Side ||
                s.Data.Length != size || size != (128 << s.N) || s.ST1 != 0 || s.ST2 != 0).Any())
                throw new InvalidDataException("Raw conversion requires uniform, healthy sectors with IDs 1..n (no weak/short sectors).");
        }
        var geometry = new DiskGeometry(Cylinders, Sides, first.Sectors.Count, size); geometry.Validate(); return geometry;
    }
    public byte[] ToRaw()
    {
        UniformGeometry();
        return Tracks.SelectMany(t => t.Sectors.OrderBy(s => s.R)).SelectMany(s => s.Data).ToArray();
    }
    public byte[] ToContainer(DiskFormat format)
    {
        if (format == Format) return Save();
        if (format == DiskFormat.Raw) return ToRaw();
        var g = UniformGeometry();
        var trackLength = (256 + g.Sectors * g.SectorSize + 255) & ~255;
        if (format == DiskFormat.Extended && trackLength / 256 > 255) throw new InvalidDataException("Track too large for EDSK.");
        var bytes = new byte[256 + Cylinders * Sides * trackLength];
        Encoding.ASCII.GetBytes(format == DiskFormat.Extended ? "EXTENDED CPC DSK File\r\nDisk-Info\r\n" : "MV - CPCEMU Disk-File\r\nDisk-Info\r\n").CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("HC2000").CopyTo(bytes, 34); bytes[48] = (byte)Cylinders; bytes[49] = (byte)Sides;
        if (format == DiskFormat.Standard) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(50, 2), checked((ushort)trackLength));
        for (var i = 0; i < Tracks.Count; i++)
        {
            if (format == DiskFormat.Extended) bytes[52 + i] = (byte)(trackLength / 256);
            var track = Tracks[i]; var offset = 256 + i * trackLength;
            Encoding.ASCII.GetBytes("Track-Info\r\n").CopyTo(bytes, offset);
            bytes[offset + 16] = (byte)(i / Sides); bytes[offset + 17] = (byte)(i % Sides);
            bytes[offset + 20] = track.Sectors[0].N; bytes[offset + 21] = (byte)g.Sectors;
            bytes[offset + 22] = track.Gap; bytes[offset + 23] = track.Filler;
            var position = offset + 256;
            foreach (var (sector, j) in track.Sectors.OrderBy(s => s.R).Select((s, j) => (s, j)))
            {
                var id = bytes.AsSpan(offset + 24 + j * 8, 8);
                id[0] = sector.C; id[1] = sector.H; id[2] = sector.R; id[3] = sector.N;
                if (format == DiskFormat.Extended) BinaryPrimitives.WriteUInt16LittleEndian(id[6..], (ushort)sector.Data.Length);
                sector.Data.CopyTo(bytes, position); position += sector.Data.Length;
            }
        }
        return bytes;
    }

    internal byte[] LogicalBytes(IReadOnlyList<int> ids)
    {
        UniformGeometry();
        if (ids.Count != Tracks[0].Sectors.Count || ids.Distinct().Count() != ids.Count) throw new InvalidDataException("Skew does not match disk.");
        return Tracks.SelectMany(t => ids.Select(id => t.Sectors.Single(s => s.R == id))).SelectMany(s => s.Data).ToArray();
    }
    internal void WriteLogicalBytes(byte[] bytes, IReadOnlyList<int> ids)
    {
        var sectors = Tracks.SelectMany(t => ids.Select(id => t.Sectors.Single(s => s.R == id))).ToArray();
        if (sectors.Sum(s => s.Data.Length) != bytes.Length) throw new InvalidDataException("Logical image size mismatch.");
        var offset = 0;
        foreach (var sector in sectors) { bytes.AsSpan(offset, sector.Data.Length).CopyTo(sector.Data); offset += sector.Data.Length; }
    }
}
