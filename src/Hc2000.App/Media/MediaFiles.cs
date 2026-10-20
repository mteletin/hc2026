using System.Collections.Immutable;
using Hc2000.HostFiles;

namespace Hc2000.App.Media;

/// <summary>Explicit host-side file actions, never called by the emulation boundary.</summary>
public static class MediaFiles
{
    public const int MaxDiskBytes = 4 * 1024 * 1024;
    public const int MaxTapeBytes = 16 * 1024 * 1024;
    // A .hcs container maximum, equal to HcsLimits.TotalBytes: the UI accepts exactly what the codec accepts.
    public const int MaxSnapshotBytes = 256 * 1024 * 1024;
    public static async Task<ImmutableArray<byte>> ReadAsync(string path, int maximum)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous);
        if (input.Length > maximum) throw new InvalidDataException("Media file exceeds the size limit.");
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int length;
        while ((length = await input.ReadAsync(buffer)) != 0)
        {
            if (output.Length + length > maximum) throw new InvalidDataException("Media file exceeds the size limit.");
            output.Write(buffer, 0, length);
        }
        return ImmutableArray.CreateRange(output.ToArray());
    }

    // Snapshot bytes came from the owner; persisting them must never remount/reset a running drive.
    public static async Task SaveAsync(string path, ImmutableArray<byte> bytes)
    {
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var admission = NativeImageAdmission.BeginExistingPublication(fullPath);
        bool moveStarted = false, moved = false;
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes.ToArray());
            admission?.ValidatePublicationTargetStillCurrent();
            moveStarted = true;
            File.Move(temporary, fullPath, overwrite: true);
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
