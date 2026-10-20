using System.Collections.Immutable;
using Hc2000.HostFiles;

namespace Hc2000.App.Media;

/// <summary>Host-only raw image I/O. Success is returned only after the complete copy is persisted.</summary>
public static class HddFiles
{
    public static async Task<ImmutableArray<byte>> ReadAsync(string path, CancellationToken cancellation = default)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != HddMediaAdapter.ImageLength) throw new InvalidDataException("HDD image must be exactly 8,388,608 bytes.");
        var bytes = new byte[HddMediaAdapter.ImageLength];
        await input.ReadExactlyAsync(bytes, cancellation);
        if (await input.ReadAsync(new byte[1], cancellation) != 0)
            throw new InvalidDataException("HDD image changed size while reading.");
        return ImmutableArray.CreateRange(bytes);
    }

    public static async Task SaveAsync(string path, ImmutableArray<byte> bytes, CancellationToken cancellation = default)
    {
        if (bytes.IsDefault || bytes.Length != HddMediaAdapter.ImageLength)
            throw new InvalidDataException("HDD export must be exactly 8,388,608 bytes.");
        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target)!;
        var temporary = Path.Combine(directory, ".hc-hdd-" + Guid.NewGuid().ToString("N") + ".tmp");
        var admission = NativeImageAdmission.BeginExistingPublication(target, cancellation);
        bool moveStarted = false, moved = false;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes.AsMemory(), cancellation);
                await output.FlushAsync(cancellation);
                output.Flush(flushToDisk: true);
            }
            cancellation.ThrowIfCancellationRequested();
            admission?.ValidatePublicationTargetStillCurrent();
            moveStarted = true;
            File.Move(temporary, target, overwrite: true); // Same directory: never overwrite target with a partial copy.
            moved = true;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally
            {
                if (moved || !moveStarted) admission?.CompleteKnownPublicationAfterDrain(
                    moved ? KnownImagePublication.KnownNew : KnownImagePublication.Unpublished);
                else admission?.RetainUnknownPublication();
            }
        }
    }
}
