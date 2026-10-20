namespace Hc2000.HostFiles;

/// <summary>New-image create in a media home (KB hc2000-media-home-scan-admission-v1 sec 4): the one
/// writer to a media home. Writes the target's bytes as a new file inside the home and refuses a home
/// that is unset, missing or not a directory and a name that already exists; never overwrites, never
/// registers the new file. Shared by the hc CLI and the App; the bytes come from the shared writers
/// (Hc2000.Machine.Storage.MediaBlanks).</summary>
internal static class MediaHomeCreate
{
    public static string Create(string? home, string name, Func<byte[]> write)
    {
        if (string.IsNullOrWhiteSpace(home))
            throw new ArgumentException("Media home is not set.", nameof(home));
        if (!Directory.Exists(home))
            throw new IOException($"Media home is missing or not a directory: {home}.");
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A new image needs a name.", nameof(name));
        var fullHome = Path.GetFullPath(home);
        var full = Path.GetFullPath(Path.Combine(fullHome, name));
        if (!full.StartsWith(fullHome + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Name must stay inside the media home.", nameof(name));
        if (File.Exists(full) || Directory.Exists(full))
            throw new IOException($"Refusing to replace an existing path with media create: {full}.");
        var temporary = full + ".hc-" + Guid.NewGuid().ToString("N");
        var admission = NativeImageAdmission.BeginExistingPublication(full);
        bool moveStarted = false, moved = false;
        try
        {
            File.WriteAllBytes(temporary, write());
            admission?.ValidatePublicationTargetStillCurrent();
            moveStarted = true;
            File.Move(temporary, full, false);
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
        return full;
    }
}
