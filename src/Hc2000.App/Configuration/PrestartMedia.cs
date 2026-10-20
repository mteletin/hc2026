using System.Collections.Immutable;
using Hc2000.App.Media;

namespace Hc2000.App.Configuration;

/// <summary>Read/validate detached bytes before worker construction. No new codec or mount authority.</summary>
internal static class PrestartMedia
{
    internal sealed record Disk(DiskSlot Slot, string Path, ImmutableArray<byte> Bytes);

    internal static async Task<ImmutableArray<Disk>> ReadAsync(MachineConfiguration configuration)
    {
        var result = ImmutableArray.CreateBuilder<Disk>();
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
            var bytes = await MediaFiles.ReadAsync(disk.Path, MediaFiles.MaxDiskBytes);
            var descriptor = disk.Descriptor.ToDescriptor();
            await Task.Run(() => RawFloppyAdapter.Load(bytes.AsSpan(), descriptor, new(80, 2)));
            result.Add(new(slot, disk.Path, bytes));
        }
        return result.ToImmutable();
    }
}
