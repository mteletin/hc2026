namespace Hc2000.App.Media;

using Hc2000.HostFiles;
using Hc2000.Machine.Storage;

/// <summary>The App's new-image entry point (hc2000-disk-explorer-new-image-ui; contract section 4,
/// caller only): the same shared writers the hc CLI calls and the one shared writer to a media home.
/// Refusals (unset, missing or non-directory home, existing name) come back as stated reasons, never
/// as exceptions.</summary>
internal static class MediaHomeNewImage
{
    internal sealed record NewImageResult(bool Created, string? Path, string? Refusal);

    internal static NewImageResult NewImage(string kind, string? home, string name)
    {
        Func<byte[]> write = kind switch
        {
            "floppy-720" => MediaBlanks.Floppy720,
            "hdd-volume-8m" => MediaBlanks.HddVolume8M,
            "tape-blank" => MediaBlanks.TapeBlank,
            _ => throw new ArgumentException("Unknown new-image kind: " + kind, nameof(kind))
        };
        try
        {
            return new NewImageResult(true, MediaHomeCreate.Create(home, name, write), null);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new NewImageResult(false, null, ex.Message);
        }
    }
}
