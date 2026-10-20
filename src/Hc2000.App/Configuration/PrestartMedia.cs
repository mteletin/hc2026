using System.Collections.Immutable;
using Hc2000.App.Media;

namespace Hc2000.App.Configuration;

/// <summary>Read/validate detached bytes before worker construction. No new codec or mount authority.</summary>
internal static class PrestartMedia
{
    internal sealed record Disk(DiskSlot Slot, string Path, ImmutableArray<byte> Bytes, DiskReadReservation.Reservation? Reservation = null);

    // operation is the host-held bounded configured-start path (guarded hosts): one parent operation
    // owns the single native cold-read fence for this frozen A/B subset, and each disk read borrows
    // it (no reacquire, no dispose); a read or parse failure aborts the operation before any enqueue.
    // A null operation keeps the plain bounded read unchanged.
    internal static async Task<ImmutableArray<Disk>> ReadAsync(MachineConfiguration configuration, DiskReadReservation.Operation? operation = null)
    {
        var result = ImmutableArray.CreateBuilder<Disk>();
        try
        {
            foreach (var (slot, disk) in new[] { (DiskSlot.A, configuration.DiskA), (DiskSlot.B, configuration.DiskB) })
            {
                if (disk is null) continue;
                if (disk.Writeback) throw new ArgumentException("Prestart writable admission is unavailable; retain the draft or explicitly turn off writeback in the source configuration.");
                var info = new FileInfo(disk.Path);
                if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Disk must be an existing regular local file, not a link.");
                if (OperatingSystem.IsLinux())
                {
                    var facts = LinuxFileFacts.Inspect(disk.Path);
                    if (!facts.Known || !facts.Regular || facts.Link) throw new IOException("Disk regular-file identity is unavailable.");
                }
                DiskReadReservation.Reservation? reservation = null;
                var bytes = operation is null
                    ? await MediaFiles.ReadAsync(disk.Path, MediaFiles.MaxDiskBytes)
                    : (reservation = await operation.ReadMemberAsync(slot, disk.Path, CancellationToken.None)).Bytes;
                var descriptor = disk.Descriptor.ToDescriptor();
                await Task.Run(() => RawFloppyAdapter.Load(bytes.AsSpan(), descriptor, new(80, 2)));
                result.Add(new(slot, disk.Path, bytes, reservation));
            }
        }
        catch (Exception ex) when (operation is not null)
        {
            // A read or parse failure ends the whole configured start before any enqueue: the started
            // I/O was already drained by the member read, the parent operation releases its single
            // fence exactly once here, and a prepared but never-dispatched member carries no dispatch
            // permission.
            operation.AbortBeforeEnqueue("prestart " + ex.GetBaseException().Message);
            throw;
        }
        return result.ToImmutable();
    }
}
