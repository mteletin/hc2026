namespace Hc2000.App.Audio;

/// <summary>One sound-dump request and its outcome. <see cref="Ok"/> with a null <see cref="Path"/> means no
/// dump was requested; a refused request carries the route's one refusal sentence and nothing else.</summary>
public sealed record SoundDumpResolution(bool Ok, string? Path, string? Refusal)
{
    public static SoundDumpResolution None { get; } = new(true, null, null);
}

/// <summary>The sound-dump root fence (PO ruling board 634859; mechanism ruled by architect-1, doc
/// note-20261019-091323). One named helper in the sound-dump path itself - NOT a shared file-input
/// fence; a shared fence is extracted only when a second host-file writer appears.
///
/// The only allowed root is the OS user's home (<see cref="Environment.SpecialFolder.UserProfile"/>):
/// a relative request resolves under the home, never the current directory; the check runs on the
/// fully resolved absolute path - symlinks and <c>..</c> both included - and the file name is chosen
/// before the check, so the refusal cannot probe the filesystem. The refusal is one sentence naming
/// the root, identical text on the CLI flag and the App setting. Media homes and the settings
/// directory (ApplicationData) are not dump roots.</summary>
public static class SoundDumpGate
{
    /// <summary>The catalog-owned spelling of this route: the CLI flag and the App setting field.</summary>
    public const string Name = "sound-dump";

    /// <summary>Symbolic-link hops a resolution may follow before it refuses instead of looping.</summary>
    private const int MaxLinkHops = 40;

    /// <summary>The one refusal sentence; both surfaces show exactly this.</summary>
    public static string Refusal(string home)
        => "Sound dump refused: the WAV file must be inside your user home directory (" + home + "); this path is outside it.";

    /// <summary>Resolve a requested dump path under the user home (or <paramref name="home"/>, for
    /// tests). Blank means no dump. Anything that cannot be resolved, that escapes the home, or whose
    /// home itself cannot be resolved is refused with the one sentence - it never reaches the
    /// filesystem with a write.</summary>
    public static SoundDumpResolution Resolve(string? requested, string? home = null)
    {
        var homeFull = System.IO.Path.GetFullPath(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var homeRoot = ResolveExisting(homeFull, 0);
        if (homeRoot is null)
            return new SoundDumpResolution(false, null, Refusal(homeFull)); // the root itself is unresolvable: fail closed
        if (string.IsNullOrWhiteSpace(requested))
            return SoundDumpResolution.None;
        var candidate = System.IO.Path.IsPathRooted(requested)
            ? requested
            : System.IO.Path.Combine(homeRoot, requested);
        // No GetFullPath here: it normalizes `..` lexically and would hide a link escape
        // (home/l/../x.wav -> home/x.wav) before the walk can follow the link.
        var resolved = ResolveExisting(candidate, 0);
        if (resolved is null)
            return new SoundDumpResolution(false, null, Refusal(homeRoot));
        if (resolved == homeRoot
            || resolved.StartsWith(homeRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return new SoundDumpResolution(true, resolved, null);
        return new SoundDumpResolution(false, null, Refusal(homeRoot));
    }

    /// <summary>Walk <paramref name="full"/> from its root, following symbolic links (the .NET path
    /// API is lexical only), with <see cref="MaxLinkHops"/> shared across the whole walk. The first
    /// part that does not exist ends the walk: the remainder is kept lexically, because it cannot
    /// resolve further or escape any further. An unresolvable link or a link loop returns null.
    /// A <c>..</c> after a link is applied to the link's target - the link is resolved first.</summary>
    private static string? ResolveExisting(string full, int hops)
    {
        var root = System.IO.Path.GetPathRoot(full) ?? "";
        var parts = full[root.Length..].Split(
            System.IO.Path.DirectorySeparatorChar, System.StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part == ".")
                continue;
            if (part == "..")
            {
                // Applied to the fully resolved prefix: after a link, `..` means the link's
                // target's parent, never the link's own parent.
                current = System.IO.Directory.GetParent(current)?.FullName ?? current;
                continue;
            }
            var candidate = System.IO.Path.Combine(current, part);
            try
            {
                var linkTarget = new System.IO.DirectoryInfo(candidate).LinkTarget;
                if (!string.IsNullOrEmpty(linkTarget))
                {
                    if (hops >= MaxLinkHops)
                        return null;
                    var target = System.IO.Path.IsPathRooted(linkTarget)
                        ? linkTarget
                        : System.IO.Path.Combine(current, linkTarget);
                    var resolvedTarget = ResolveExisting(System.IO.Path.GetFullPath(target), hops + 1);
                    if (resolvedTarget is null)
                        return null;
                    current = resolvedTarget;
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null; // the path cannot be inspected: refuse, don't probe
            }
            if (System.IO.Directory.Exists(candidate))
            {
                current = candidate;
                continue;
            }
            return System.IO.Path.Combine(current, string.Join(
                System.IO.Path.DirectorySeparatorChar, parts[i..]));
        }
        return current;
    }
}
