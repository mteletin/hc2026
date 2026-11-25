namespace Hc2000.Asm;

/// <summary>One machine the source library builds for (decision hc2000-asm-library-build-v1, contract §2): its name on
/// the command line, the <c>TARGET</c> value a program tests, the name the category manifests (contract v7) use for it,
/// and whether anything is built for it yet.</summary>
public sealed record LibraryTarget(string Name, int Id, string ManifestName, string Meaning, bool Built)
{
    /// <summary>The define a program may test, e.g. <c>TARGET_HC2000_V2</c>.</summary>
    public string Define => "TARGET_" + Name.ToUpperInvariant().Replace('-', '_');
}

/// <summary>The closed list of targets, the facts each one's <c>targets.inc</c> carries, and the include search path a
/// build for a target uses (decision hc2000-asm-library-build-v1, contract §1-§4).</summary>
public static class LibraryTargets
{
    public static IReadOnlyList<LibraryTarget> All { get; } =
    [
        new("hc2000-v1", 1, "original", "the original HC2000: the faithful ROM and CP/M 2.2 as it ships", Built: true),
        new("hc2000-v2", 2, "extended-cpm22", "the extended HC2000 under CP/M 2.2", Built: true),
        new("cpm3", 3, "cpm3", "the extended HC2000 under CP/M 3.0", Built: true),
    ];

    /// <summary>The target by its command-line name or its manifest name, ignoring case; null for anything else.</summary>
    public static LibraryTarget? Find(string name) => All.FirstOrDefault(target =>
        string.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(target.ManifestName, name, StringComparison.OrdinalIgnoreCase));

    public static string Names => string.Join(", ", All.Select(target => target.Name));

    /// <summary>The source library's root (the folder holding <c>shared/</c>) above <paramref name="start"/>: the
    /// nearest <c>src-library</c> folder that has one, or null.</summary>
    public static string? FindLibraryRoot(string start)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(start)); directory is not null; directory = directory.Parent)
        {
            if (directory.Name == "src-library" && Directory.Exists(Path.Combine(directory.FullName, "shared"))) return directory.FullName;
            var below = Path.Combine(directory.FullName, "src-library");
            if (Directory.Exists(Path.Combine(below, "shared"))) return below;
        }
        return null;
    }

    /// <summary>Where a build for <paramref name="target"/> looks for INCLUDEs after the source's own folder: the
    /// target's <c>targets.inc</c> first, then the shared libraries, so <c>INCLUDE "targets.inc"</c> and
    /// <c>INCLUDE "bdos/bdos.inc"</c> resolve the same from every program.</summary>
    public static IReadOnlyList<string> IncludePaths(string libraryRoot, LibraryTarget target) =>
        [Path.Combine(libraryRoot, "shared", "targets", target.Name), Path.Combine(libraryRoot, "shared")];

    /// <summary>The defines <c>--target</c> stands for: <c>TARGET</c> = the target's id and its own name = 1.</summary>
    public static IReadOnlyDictionary<string, int> Defines(LibraryTarget target) =>
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["TARGET"] = target.Id, [target.Define] = 1 };

    /// <summary>The assembler options a build of <paramref name="source"/> for <paramref name="target"/> uses: the
    /// target's defines, and its search path when the source sits in a source library (a source outside one, such as a
    /// test fixture, gets the defines alone).</summary>
    public static AssemblerOptions Options(string source, LibraryTarget target, byte fill = 0xFF)
    {
        var root = FindLibraryRoot(Path.GetDirectoryName(Path.GetFullPath(source))!);
        return new() { Fill = fill, Defines = Defines(target), IncludePaths = root is null ? [] : IncludePaths(root, target) };
    }
}
