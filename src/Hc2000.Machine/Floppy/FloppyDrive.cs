using System.Buffers.Binary;
using System.Text;
using Hc2000.Machine.Storage;

namespace Hc2000.Machine.Floppy;

public sealed record DirtySector(int Cylinder, int Side, int SectorIndex);

/// <summary>Physical drive and image-backed sector overlay; never opens a host file or touches CPU RAM.</summary>
public sealed class FloppyDrive
{
    private byte[]? mountedBytes;
    private readonly Dictionary<(int, int), DiskTrack> formatted = new();
    private readonly HashSet<DirtySector> dirty = new();
    private readonly Dictionary<DiskSector, (byte ST1, byte ST2)> status = new();
    public DriveProfile Profile { get; }
    public Dsk? Image { get; private set; }
    public int HeadPosition { get; private set; }
    public bool HeadLoaded { get; internal set; }
    public bool MotorOn { get; private set; }
    public long RotationOrigin { get; private set; }
    public bool WriteProtected { get; set; }
    public bool DiskChanged { get; private set; }
    public IReadOnlyCollection<DirtySector> DirtySectors => dirty.ToArray();
    public IReadOnlyCollection<DiskTrack> FormattedTracks => formatted.Values.ToArray();

    public FloppyDrive(DriveProfile profile, Dsk? image = null)
    {
        profile.Validate();
        Profile = profile;
        Mount(image);
    }

    public void Mount(Dsk? image)
    {
        if (MotorOn || HeadLoaded) throw new InvalidOperationException("Stop/unload the drive before replacing media.");
        Image = image;
        // Snapshot the unmodified media at mount time (provenance for the snapshot disk-base
        // asset); FDC writes mutate the mounted image in place, so a deep copy is required.
        mountedBytes = image?.Save();
        DiskChanged = true;
        formatted.Clear(); dirty.Clear(); status.Clear();
    }

    /// <summary>Unmodified media bytes at the last mount (snapshot disk-base provenance); null when empty.</summary>
    internal byte[]? BaseMedia => mountedBytes;

    /// <summary>Power-cycle mechanics, retaining the mounted medium, overlays and write protection.</summary>
    public void PowerCycle()
    {
        HeadPosition = 0;
        HeadLoaded = false;
        MotorOn = false;
        RotationOrigin = 0;
        DiskChanged = Image is not null;
    }

    public void Step(int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        HeadPosition = Math.Clamp(HeadPosition + direction, 0, Profile.Cylinders - 1);
        DiskChanged = false;
    }

    internal void SetMotor(bool on, long now)
    {
        if (on && !MotorOn) RotationOrigin = now;
        MotorOn = on;
    }

    // A 40-track medium in the 80-track mechanism spans two physical step positions.
    public int MediaCylinder => Image?.Cylinders == 40 && Profile.Cylinders == 80 ? HeadPosition / 2 : HeadPosition;
    public DiskTrack? Track(int side) => TrackAt(MediaCylinder, side);

    // Direct logical-cylinder access for the opt-in block upgrade; it does not seek the FDC head.
    internal DiskTrack? TrackAt(int cylinder, int side)
    {
        if (Image is null || cylinder < 0 || cylinder >= Image.Cylinders || side < 0 || side >= Profile.Sides || side >= Image.Sides) return null;
        if (formatted.TryGetValue((cylinder, side), out var track)) return track;
        // Container order denotes physical placement; the recorded C/H bytes are not a seek target.
        return Image.Tracks[cylinder * Image.Sides + side];
    }

    public bool IndexAt(long tick, FdcTiming timing)
    {
        if (!MotorOn || Image is null) return false;
        if (tick < RotationOrigin) throw new ArgumentOutOfRangeException(nameof(tick));
        var phase = (Int128)(tick - RotationOrigin) * timing.Rpm % ((Int128)timing.MasterHz * 60);
        return phase < (Int128)timing.MasterHz * timing.IndexPulseMicroseconds * timing.Rpm / 1_000_000;
    }

    internal long NextIndex(long now, FdcTiming timing) => NextSlot(now, timing, 0, 1);
    internal long NextSector(long now, FdcTiming timing, int index, int count) => NextSlot(now, timing, index, count);
    private long NextSlot(long now, FdcTiming timing, int index, int count)
    {
        // Containers do not supply magnetic angular offsets. Uniform ID positions are a documented model, not measured hardware.
        var denominator = checked((long)timing.Rpm * count);
        var periodNumerator = checked(timing.MasterHz * 60);
        var elapsed = checked(now - RotationOrigin);
        var slot = (Int128)elapsed * denominator / periodNumerator;
        var next = slot + 1;
        next += (index - (next % count) + count) % count;
        return checked(RotationOrigin + FdcTiming.Ceil(next * periodNumerator, denominator));
    }

    internal (byte ST1, byte ST2) Status(DiskSector sector) => status.GetValueOrDefault(sector, (sector.ST1, sector.ST2));
    internal void Write(DiskSector sector, int side, int index, int offset, byte value, bool deleted)
        => WriteAt(sector, MediaCylinder, side, index, offset, value, deleted);

    internal void WriteAt(DiskSector sector, int cylinder, int side, int index, int offset, byte value, bool deleted)
    {
        if (WriteProtected) throw new InvalidOperationException("Write protected.");
        sector.Data[offset] = value;
        status[sector] = (0, deleted ? (byte)0x40 : (byte)0);
        dirty.Add(new(cylinder, side, index));
    }

    internal void Format(int side, IReadOnlyList<(byte C, byte H, byte R, byte N)> ids, byte gap, byte fill, byte sizeCode)
    {
        if (WriteProtected || Image is null) throw new InvalidOperationException("No writable medium.");
        foreach (var oldSector in Track(side)?.Sectors ?? Array.Empty<DiskSector>()) status.Remove(oldSector);
        var sectors = ids.Select(id => new DiskSector(id.C, id.H, id.R, id.N, 0, 0, -1,
            Enumerable.Repeat(fill, 128 << sizeCode).ToArray())).ToArray();
        formatted[(MediaCylinder, side)] = new(MediaCylinder, side, gap, fill, Array.AsReadOnly(sectors));
        dirty.RemoveWhere(s => s.Cylinder == MediaCylinder && s.Side == side);
        for (var i = 0; i < sectors.Length; i++) dirty.Add(new(MediaCylinder, side, i));
    }

    /// <summary>Restores the drive's logical state from a captured snapshot (snapshot restore): media,
    /// head position and load, motor and rotation origin, protection and change marks, dirty sectors.
    /// The media bytes must already be decoded by the caller into <paramref name="image"/>.
    /// The captured formatted-overlay and status marks live inside the media export, so no further
    /// sector state is carried here.</summary>
    internal void RestoreSnapshot(Dsk? image, int headPosition, bool headLoaded, bool motorOn,
        long rotationOrigin, bool writeProtected, bool diskChanged, IReadOnlyCollection<DirtySector> dirtySectors,
        byte[]? baseMedia = null)
    {
        if (MotorOn || HeadLoaded)
            throw new InvalidOperationException("Drive restore requires a stopped drive.");
        Image = image;
        // The captured disk-base provenance (unmodified media at the source's last mount);
        // fall back to the restored media bytes when the caller has none.
        mountedBytes = baseMedia ?? image?.Save();
        DiskChanged = diskChanged;
        HeadPosition = headPosition;
        WriteProtected = writeProtected;
        RotationOrigin = motorOn ? rotationOrigin : 0;
        MotorOn = motorOn;
        HeadLoaded = headLoaded;
        dirty.UnionWith(dirtySectors);
    }

    /// <summary>Export logical sectors, formatted overlays and status marks as EDSK bytes; caller owns host persistence.</summary>
    /// <remarks>Not a byte-identical archive save or .hcs snapshot. Dsk.Save preserves the original container.</remarks>
    public byte[] ExportMedia()
    {
        if (Image is null) throw new InvalidOperationException("No medium.");
        var tracks = Image.Tracks.Select((t, i) => formatted.GetValueOrDefault((i / Image.Sides, i % Image.Sides), t)).ToArray();
        var lengths = tracks.Select(t => t.Sectors.Count == 0 ? 0 :
            checked((256 + t.Sectors.Sum(s => s.Data.Length) + 255) & ~255)).ToArray();
        if (lengths.Any(l => l / 256 > 255) || tracks.Any(t => t.Sectors.Count > 29 || t.Sectors.Any(s => s.Data.Length > ushort.MaxValue || s.N > 6)))
            throw new InvalidDataException("Track cannot be represented as EDSK.");
        var bytes = new byte[checked(256 + lengths.Sum())];
        Encoding.ASCII.GetBytes("EXTENDED CPC DSK File\r\nDisk-Info\r\n").CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("HC2000 FDC").CopyTo(bytes, 34);
        bytes[48] = (byte)Image.Cylinders; bytes[49] = (byte)Image.Sides;
        var offset = 256;
        for (var i = 0; i < tracks.Length; i++)
        {
            bytes[52 + i] = (byte)(lengths[i] / 256);
            if (lengths[i] == 0) continue;
            var track = tracks[i];
            Encoding.ASCII.GetBytes("Track-Info\r\n").CopyTo(bytes, offset);
            bytes[offset + 16] = (byte)track.Cylinder; bytes[offset + 17] = (byte)track.Side;
            bytes[offset + 20] = track.Sectors[0].N; bytes[offset + 21] = (byte)track.Sectors.Count;
            bytes[offset + 22] = track.Gap; bytes[offset + 23] = track.Filler;
            var data = offset + 256;
            for (var j = 0; j < track.Sectors.Count; j++)
            {
                var sector = track.Sectors[j]; var id = bytes.AsSpan(offset + 24 + j * 8, 8);
                id[0] = sector.C; id[1] = sector.H; id[2] = sector.R; id[3] = sector.N;
                var marks = Status(sector); id[4] = marks.ST1; id[5] = marks.ST2;
                BinaryPrimitives.WriteUInt16LittleEndian(id[6..], (ushort)sector.Data.Length);
                sector.Data.CopyTo(bytes, data); data += sector.Data.Length;
            }
            offset += lengths[i];
        }
        return bytes;
    }
}
