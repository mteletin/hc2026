#if INSTALLED_HDD_VALIDATION
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Io;
using Xunit;
using Xunit.Abstractions;

namespace Hc2000.Machine.Tests;

// Test-only provenance guard. Expected identity comes from the registered archive,
// never from a caller-supplied version/hash or a source-built product assembly.
internal static class InstalledHddBinding
{
    internal const string Version = "0.1.84";
    internal const string Source = "31b784a99ef22ef1cb558dc8495b1bffe811bffd";
    internal const string ArchiveHash = "367d9fb2a3f482de645092679374195981b2e4ffcf13d01e64ece59497002f6f";
    private static readonly Lazy<string> provenance = new(Verify);
    internal static string Ensure() => provenance.Value;

    private static string Verify()
    {
        var metadata = typeof(InstalledHddBinding).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(a => a.Key, a => a.Value);
        var stage = Environment.GetEnvironmentVariable("HC2000_HDD_STAGE")
            ?? throw new InvalidOperationException("Missing private registered-artifact stage.");
        stage = Path.GetFullPath(stage);
        var sourceTree = metadata["InstalledHddSourceTree"]
            ?? throw new InvalidOperationException("Missing source-tree exclusion.");
        if (!stage.StartsWith("/tmp/hc2000-hdd-installed-binding.", StringComparison.Ordinal))
            throw new InvalidOperationException("Installed validation requires its private /tmp stage.");
        var root = metadata["InstalledHddRoot"]
            ?? throw new InvalidOperationException("Missing compile-time reference origin.");
        VerifyReferenceRoot(root, stage);
        var archive = Path.Combine(stage, "package.tar.gz");
        VerifyFileHash(archive, ArchiveHash);

        var wanted = new HashSet<string>(StringComparer.Ordinal)
            { "VERSION", "source-commit", "bin/Hc2000.Machine.dll", "bin/Hc2000.Z80.dll" };
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var gzip = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
        {
            TarEntry? entry;
            while ((entry = tar.GetNextEntry()) is not null)
            {
                var name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
                if (!wanted.Contains(name)) continue;
                if (entry.DataStream is null || identities.ContainsKey(name))
                    throw new InvalidOperationException("Missing/duplicate registered archive member: " + name);
                if (name.EndsWith(".dll", StringComparison.Ordinal))
                    identities.Add(name, Convert.ToHexString(SHA256.HashData(entry.DataStream)).ToLowerInvariant());
                else
                {
                    using var reader = new StreamReader(entry.DataStream, Encoding.UTF8, leaveOpen: true);
                    identities.Add(name, reader.ReadToEnd().Trim());
                }
            }
        }
        if (identities.Count != wanted.Count) throw new InvalidOperationException("Incomplete registered archive.");
        VerifyMetadata(identities["VERSION"], identities["source-commit"]);
        VerifyMetadata(File.ReadAllText(Path.Combine(root, "VERSION")).Trim(),
            File.ReadAllText(Path.Combine(root, "source-commit")).Trim());

        var machine = typeof(BlockDiskPort).Assembly;
        if (typeof(PortDecoder).Assembly != machine)
            throw new InvalidOperationException("Controller and decoder are not the same registered module.");
        var modules = new[] { machine, typeof(Hc2000.Z80.IBus).Assembly };
        var report = new StringBuilder($"registered emulator {Version}, source {Source}, archive {archive}, sha256 {ArchiveHash}\n");
        foreach (var module in modules)
        {
            var fileName = module.GetName().Name + ".dll";
            var reference = Path.Combine(root, "bin", fileName);
            VerifyFileHash(reference, identities["bin/" + fileName]);
            var loaded = module.Location;
            VerifyLoadedLocation(loaded, Path.Combine(AppContext.BaseDirectory, fileName), stage, sourceTree);
            VerifyFileHash(loaded, identities["bin/" + fileName]);
            var file = FileVersionInfo.GetVersionInfo(loaded);
            report.AppendLine($"reference={reference}; loaded={loaded}; sha256={identities["bin/" + fileName]}; " +
                $"AssemblyVersion={module.GetName().Version}; FileVersion={file.FileVersion}; " +
                $"InformationalVersion={module.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
        }
        return report.ToString();
    }

    internal static void VerifyMetadata(string version, string source)
    {
        if (version != Version || source != Source)
            throw new InvalidOperationException("Wrong registered product VERSION/source-commit.");
    }

    internal static void VerifyReferenceRoot(string root, string stage)
    {
        if (Path.GetFullPath(root) != Path.Combine(Path.GetFullPath(stage), "product"))
            throw new InvalidOperationException("Untraced compile-time product reference root.");
        RejectLinks(root);
    }

    internal static void VerifyLoadedLocation(string loaded, string expected, string stage, string sourceTree)
    {
        var full = Path.GetFullPath(loaded);
        if (full != Path.GetFullPath(expected) ||
            !full.StartsWith(Path.GetFullPath(stage) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            full.StartsWith(Path.GetFullPath(sourceTree) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Source-tree/untraced product module; not an installed testhost copy.");
        RejectLinks(full);
    }

    internal static void VerifyFileHash(string path, string expected)
    {
        RejectLinks(path);
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (actual != expected) throw new InvalidOperationException("Registered artifact hash mismatch: " + path);
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked/untraced artifact origin: " + current);
    }
}

public sealed class InstalledHddBindingTests
{
    private readonly ITestOutputHelper output;
    public InstalledHddBindingTests(ITestOutputHelper output)
    {
        this.output = output;
        // All negative guard tests also establish the actual loaded positive provenance.
        output.WriteLine(InstalledHddBinding.Ensure());
    }

    [Fact]
    public void RegisteredAssemblyCopyHasPinnedProvenance() =>
        Assert.Contains(InstalledHddBinding.Source, InstalledHddBinding.Ensure());

    [Theory]
    [InlineData("0.1.83", InstalledHddBinding.Source)]
    [InlineData(InstalledHddBinding.Version, "locally-built-source")]
    public void WrongVersionOrSourceIsRefused(string version, string source) =>
        Assert.Throws<InvalidOperationException>(() => InstalledHddBinding.VerifyMetadata(version, source));

    [Fact]
    public void SourceTreeOutputAndUntracedCopyAreRefusedEvenBeforeHashComparison()
    {
        var stage = Environment.GetEnvironmentVariable("HC2000_HDD_STAGE")!;
        var source = typeof(InstalledHddBinding).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "InstalledHddSourceTree").Value!;
        var loaded = typeof(BlockDiskPort).Assembly.Location;
        Assert.Throws<InvalidOperationException>(() => InstalledHddBinding.VerifyLoadedLocation(
            Path.Combine(source, "src/Hc2000.Machine/bin/Release/net10.0/Hc2000.Machine.dll"), loaded, stage, source));
        Assert.Throws<InvalidOperationException>(() => InstalledHddBinding.VerifyLoadedLocation(
            loaded, Path.Combine(stage, "untraced/Hc2000.Machine.dll"), stage, source));
        Assert.Throws<InvalidOperationException>(() => InstalledHddBinding.VerifyReferenceRoot(source, stage));
    }

    [Fact]
    public void ChangedDllAndWrongArchiveCannotSupplyTrustedIdentity()
    {
        var stage = Environment.GetEnvironmentVariable("HC2000_HDD_STAGE")!;
        var changed = Path.Combine(stage, "guard-negative-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(changed, [1, 2, 3]);
            // Deliberately invalid bytes test only the guard, never a controller response.
            Assert.Throws<InvalidOperationException>(() => InstalledHddBinding.VerifyFileHash(changed,
                "7fea3c4fee2ed592e63cf6a574b0812890738206f29bff8dc44ff5e273606d5c"));
            Assert.Throws<InvalidOperationException>(() => InstalledHddBinding.VerifyFileHash(changed, InstalledHddBinding.ArchiveHash));
        }
        finally { File.Delete(changed); }
        Assert.False(File.Exists(changed));
        output.WriteLine("Guard-negative temporary bytes deleted; no device fake used.");
    }
}
#endif
