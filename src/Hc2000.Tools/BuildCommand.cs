using System.Security.Cryptography;
using System.Text.Json;
using Hc2000.Asm;

namespace Hc2000.Tools;

/// <summary>The hc build adapter (decision hc2000-asm-library-build-v1, contract §5): every recipe in the source library's
/// category manifests that declares the target is assembled for it, one line per program; a program that does not declare
/// the target is skipped, never built wrongly. A recipe that pins its bytes must still build byte-identical.</summary>
public static class BuildCommand
{
    public const string Usage = "usage: hc build --target <name> [--only <category>] [--out <dir>] [--library <src-library>]\n"
        + "       hc build --facts <name>   (print the target's targets.inc)";

    /// <summary>One recipe: a manifest row (contract v7) as the build reads it.</summary>
    public sealed record Recipe(string Category, string Name, string Path, IReadOnlyList<string> Targets, int? OriginalBytes, string? OriginalSha256);

    /// <summary>What one recipe's build gave: its bytes, or why there are none.</summary>
    public sealed record Built(Recipe Recipe, byte[]? Bytes, string Outcome, bool Failed);

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? targetName = null, only = null, outDirectory = null, library = null, facts = null;
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                string Value()
                {
                    if (++i >= args.Length) throw new FormatException($"Missing value for {args[i - 1]}.");
                    return args[i];
                }
                switch (args[i])
                {
                    case "--target": targetName = Value(); break;
                    case "--only": only = Value(); break;
                    case "--out": outDirectory = Value(); break;
                    case "--library": library = Value(); break;
                    case "--facts": facts = Value(); break;
                    case "--help": output.WriteLine(Usage); return 0;
                    default: throw new FormatException($"Unknown option '{args[i]}'.\n{Usage}");
                }
            }
            if (facts is not null)
            {
                output.Write(TargetFacts.TargetsInc(Target(facts)));
                return 0;
            }
            if (targetName is null) throw new FormatException(Usage);
            var target = Target(targetName);
            var root = library is not null ? Path.GetFullPath(library)
                : LibraryTargets.FindLibraryRoot(Directory.GetCurrentDirectory())
                  ?? throw new FormatException("No src-library with shared/ here or above; pass --library <src-library>.");
            var recipes = Recipes(root);
            if (only is not null)
            {
                recipes = [.. recipes.Where(recipe => string.Equals(recipe.Category, only, StringComparison.OrdinalIgnoreCase))];
                if (recipes.Count == 0) throw new FormatException($"No category '{only}' in {root}.");
            }
            if (!target.Built)
            {
                output.WriteLine($"{target.Name}: declared, not built yet ({target.Meaning}); {recipes.Count(recipe => Declares(recipe, target))} programs declare it.");
                return 0;
            }
            int built = 0, skipped = 0;
            foreach (var recipe in recipes)
            {
                var result = Build(root, recipe, target);
                output.WriteLine($"{target.Name} {recipe.Category}/{recipe.Name}: {result.Outcome}");
                if (result.Failed) return 1;
                if (result.Bytes is null) { skipped++; continue; }
                built++;
                if (outDirectory is not null)
                {
                    var file = System.IO.Path.Combine(outDirectory, target.Name, recipe.Category, recipe.Name + ".bin");
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                    File.WriteAllBytes(file, result.Bytes);
                }
            }
            output.WriteLine($"{target.Name}: {built} built, {skipped} skipped (not declared for this target).");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException)
        {
            error.WriteLine($"hc build: {ex.Message}");
            return 1;
        }
    }

    private static LibraryTarget Target(string name) =>
        LibraryTargets.Find(name) ?? throw new FormatException($"Unknown target '{name}'; the targets are {LibraryTargets.Names}.");

    private static bool Declares(Recipe recipe, LibraryTarget target) =>
        recipe.Targets.Any(name => LibraryTargets.Find(name) == target);

    /// <summary>Every recipe in the category manifests under <paramref name="root"/>, in category and manifest order.</summary>
    public static List<Recipe> Recipes(string root)
    {
        var recipes = new List<Recipe>();
        foreach (var manifest in Directory.GetFiles(root, "manifest.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var category = document.RootElement.GetProperty("category").GetString()!;
            foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
            {
                int? bytes = entry.TryGetProperty("originalBytes", out var size) ? size.GetInt32() : null;
                string? sha = entry.TryGetProperty("originalSha256", out var hash) ? hash.GetString() : null;
                recipes.Add(new(category, entry.GetProperty("name").GetString()!, entry.GetProperty("path").GetString()!,
                    [.. entry.GetProperty("targets").EnumerateArray().Select(name => name.GetString()!)], bytes, sha));
            }
        }
        return recipes;
    }

    /// <summary>Builds one recipe for one target: skipped when the recipe does not declare it, refused when the source
    /// does not assemble or when bytes it pins come out different.</summary>
    public static Built Build(string root, Recipe recipe, LibraryTarget target)
    {
        if (!Declares(recipe, target))
            return new(recipe, null, $"skipped: it declares {string.Join(", ", recipe.Targets)}", false);
        // A recipe's path is relative to the repository, which holds src-library.
        var source = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(root)!, recipe.Path);
        var result = Assembler.Assemble(source, LibraryTargets.Options(source, target));
        var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => $"{diagnostic.File}({diagnostic.Line},{diagnostic.Column}): error: {diagnostic.Message}").ToArray();
        if (errors.Length != 0) return new(recipe, null, "failed:\n  " + string.Join("\n  ", errors), true);
        var bytes = result.ToBinary();
        if (recipe.OriginalSha256 is { } pinned)
        {
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (sha != pinned || (recipe.OriginalBytes is { } size && size != bytes.Length))
                return new(recipe, null, $"failed: {bytes.Length} bytes, sha256 {sha}, but the manifest pins {recipe.OriginalBytes} bytes, {pinned}", true);
            return new(recipe, bytes, $"{bytes.Length} bytes, byte-identical to its pinned sha256", false);
        }
        return new(recipe, bytes, $"{bytes.Length} bytes", false);
    }
}
