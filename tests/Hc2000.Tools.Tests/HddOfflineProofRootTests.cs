using Xunit;
using Xunit.Sdk;

namespace Hc2000.Tools.Tests;

public sealed class HddOfflineProofRootTests : IDisposable
{
    private readonly string temporaryDirectory = Directory.CreateTempSubdirectory("hc-proof-root-tests-").FullName;
    private readonly List<string> ownedDefaults = [];

    [Fact]
    public void UnsetProofRootCreatesFreshPrivateSubdirectoryOfRunTmpdir()
    {
        var first = HddOfflineReplacementFixturesTests.ResolveProofRoot(null, temporaryDirectory);
        var second = HddOfflineReplacementFixturesTests.ResolveProofRoot(null, temporaryDirectory);
        Assert.Equal(temporaryDirectory, Path.GetDirectoryName(first));
        Assert.NotEqual(first, second);
        Assert.Empty(Directory.EnumerateFileSystemEntries(first));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(first));
    }

    [Fact]
    public void ExplicitProofRootIsPreservedWithoutRequiringTmpdir()
    {
        Assert.Equal(temporaryDirectory, HddOfflineReplacementFixturesTests.ResolveProofRoot(temporaryDirectory, null));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory));
    }

    [Fact]
    public void UnsetProofRootAndTmpdirCreatePrivateSystemTemporaryChild()
    {
        var root = HddOfflineReplacementFixturesTests.ResolveProofRoot(null, null);
        ownedDefaults.Add(root);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), Path.GetDirectoryName(root));
        Assert.StartsWith("hc-hdd-offline-proof-", Path.GetFileName(root));
        AssertPrivateEmpty(root);
    }

    [Fact]
    public void UnsetInspectionRootCreatesPrivateSystemTemporaryChild()
    {
        var root = HddOfflineInspectionCliTests.ResolveProofRoot(null);
        ownedDefaults.Add(root);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), Path.GetDirectoryName(root));
        Assert.StartsWith("hc-hdd-inspection-proof-", Path.GetFileName(root));
        Assert.NotEqual(Path.Combine(Path.GetTempPath(), "hc-hdd-inspection-proof"), root);
        AssertPrivateEmpty(root);
    }

    [Theory]
    [InlineData("")]
    [InlineData("deliberate-relative-root")]
    public void InspectionExplicitRootIsReturnedUnchanged(string root)
        => Assert.Equal(root, HddOfflineInspectionCliTests.ResolveProofRoot(root));

    [Fact]
    public void InspectionExplicitRootIsNotCreatedOrCleanedByDefaultResolution()
    {
        var root = Path.Combine(temporaryDirectory, "missing-explicit-inspection");
        Assert.Equal(root, HddOfflineInspectionCliTests.ResolveProofRoot(root));
        Assert.False(Directory.Exists(root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentIndependentDefaultsAndOwnedCleanupLeaveOtherOutputUntouched(bool inspection)
    {
        // This pre-existing directory stands in for another run/owner: no traversal or permission repair.
        var stale = Path.Combine(temporaryDirectory, "hc-hdd-inspection-proof");
        Directory.CreateDirectory(stale);
        var sentinel = Path.Combine(stale, "foreign-receipt.txt");
        File.WriteAllText(sentinel, "retain another owner's proof");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(stale, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var staleMode = OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(stale);
        Assert.Equal(stale, inspection
            ? HddOfflineInspectionCliTests.ResolveProofRoot(stale)
            : HddOfflineReplacementFixturesTests.ResolveProofRoot(stale, null));
        var roots = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => inspection
            ? HddOfflineInspectionCliTests.ResolveProofRoot(null)
            : HddOfflineReplacementFixturesTests.ResolveProofRoot(null, null))));
        ownedDefaults.AddRange(roots);
        Assert.Equal(roots.Length, roots.Distinct(StringComparer.Ordinal).Count());
        foreach (var root in roots)
        {
            AssertPrivateEmpty(root);
            File.WriteAllText(Path.Combine(root, "same-case-name.txt"), root);
        }

        Directory.Delete(roots[0], recursive: true);
        Assert.False(Directory.Exists(roots[0]));
        foreach (var root in roots.Skip(1))
            Assert.Equal(root, File.ReadAllText(Path.Combine(root, "same-case-name.txt")));
        Assert.Equal("retain another owner's proof", File.ReadAllText(sentinel));
        if (!OperatingSystem.IsWindows()) Assert.Equal(staleMode, File.GetUnixFileMode(stale));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptySuppliedTmpdirIsNotReplacedBySystemDefault(string supplied)
        => Assert.Throws<FalseException>(() => HddOfflineReplacementFixturesTests.ResolveProofRoot(null, supplied));

    [Fact]
    public void MissingSuppliedTmpdirIsNotCreatedOrReplaced()
    {
        var missing = Path.Combine(temporaryDirectory, "missing-tmpdir");
        Assert.Throws<TrueException>(() => HddOfflineReplacementFixturesTests.ResolveProofRoot(null, missing));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void ReparseExplicitRootAndSuppliedTmpdirAreNotUsed()
    {
        if (OperatingSystem.IsWindows()) return; // Creating symlinks can require elevated privileges.
        var link = Path.Combine(temporaryDirectory, "linked-root");
        Directory.CreateSymbolicLink(link, temporaryDirectory);
        Assert.Throws<FalseException>(() => HddOfflineReplacementFixturesTests.ResolveProofRoot(link, null));
        Assert.Throws<FalseException>(() => HddOfflineReplacementFixturesTests.ResolveProofRoot(null, link));
        Assert.Equal(new[] { link }, Directory.GetFileSystemEntries(temporaryDirectory));
    }

    [Fact]
    public void MissingExplicitRootIsNotReplacedByTmpdir()
    {
        var missing = Path.Combine(temporaryDirectory, "missing");
        Assert.Throws<TrueException>(() => HddOfflineReplacementFixturesTests.ResolveProofRoot(missing, temporaryDirectory));
        Assert.False(Directory.Exists(missing));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory));
    }

    private static void AssertPrivateEmpty(string root)
    {
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(root));
    }

    public void Dispose()
    {
        // Delete only roots returned by our own resolver calls, never a fixed/shared parent or explicit override.
        foreach (var root in ownedDefaults)
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var stale = Path.Combine(temporaryDirectory, "hc-hdd-inspection-proof");
        if (!OperatingSystem.IsWindows() && Directory.Exists(stale))
            File.SetUnixFileMode(stale, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(temporaryDirectory, true);
    }
}
