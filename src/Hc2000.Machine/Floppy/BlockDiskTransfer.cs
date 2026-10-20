using Hc2000.Machine.Memory;

namespace Hc2000.Machine.Floppy;

/// <summary>Experimental host-side result only; these values are NOT a guest port ABI.</summary>
public enum BlockDiskStatus
{
    Success, InvalidDrive, NoMedia, SectorNotFound, InvalidSectorSize,
    WriteProtected, InvalidDma, MediaError
}

/// <summary>Owner-thread, synchronous upgrade transfer; no host I/O or FDC timing effects.</summary>
public sealed class BlockDiskTransfer(HcMemory memory, FloppyDrive driveA, FloppyDrive driveB)
{
    public BlockDiskStatus Execute(bool write, byte drive, byte cylinder, byte side, byte sector, ushort dma)
    {
        var selected = drive switch { 0 => driveA, 1 => driveB, _ => null };
        if (selected is null) return BlockDiskStatus.InvalidDrive;
        if (selected.Image is null) return BlockDiskStatus.NoMedia;
        var track = selected.TrackAt(cylinder, side);
        if (track is null) return BlockDiskStatus.SectorNotFound;
        var matches = track.Sectors.Select((s, i) => (Sector: s, Index: i))
            .Where(s => s.Sector.C == cylinder && s.Sector.H == side && s.Sector.R == sector).ToArray();
        if (matches.Length != 1) return BlockDiskStatus.SectorNotFound;
        var (target, index) = matches[0];
        if (target.N != 2 || target.Data.Length != 512) return BlockDiskStatus.InvalidSectorSize;
        if (write && selected.WriteProtected) return BlockDiskStatus.WriteProtected;
        // Never silently read CRC/deleted marks as ordinary data. A full write may repair them.
        if (!write && selected.Status(target) != (0, 0)) return BlockDiskStatus.MediaError;
        if (!ValidDma(dma)) return BlockDiskStatus.InvalidDma;
        // Preflight above makes a rejected command atomic. Paging cannot change mid-transfer
        // on the machine owner thread. Capture source first to avoid overlap surprises.
        var data = write ? ReadDma(dma) : (byte[])target.Data.Clone();
        for (var i = 0; i < 512; i++)
            if (write) selected.WriteAt(target, cylinder, side, index, i, data[i], false);
            else memory.Write((ushort)(dma + i), data[i]);
        return BlockDiskStatus.Success;
    }

    internal bool ValidDma(ushort dma)
    {
        if (dma > 0xfe00) return false;
        for (var i = 0; i < 512; i++)
        {
            var location = memory.Resolve((ushort)(dma + i));
            if (!location.Writable || location.Store is not (MemoryStore.Ram or MemoryStore.ShadowRam)) return false;
        }
        return true;
    }

    internal byte[] ReadDma(ushort dma) => Enumerable.Range(0, 512).Select(i => memory.Read((ushort)(dma + i))).ToArray();
    internal void WriteDma(ushort dma, ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++) memory.Write((ushort)(dma + i), data[i]);
    }
}
