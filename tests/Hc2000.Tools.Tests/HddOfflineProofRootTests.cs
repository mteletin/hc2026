using Xunit;
using Xunit.Sdk;

namespace Hc2000.Tools.Tests;

public sealed class HddOfflineProofRootTests : IDisposable
{
    private readonly string temporaryDirectory = Directory.CreateTempSubdirectory("hc-proof-root-tests-").FullName;

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
    public void UnsetProofRootDoesNotFallBackToAnUnspecifiedSystemDirectory()
        => Assert.Throws<FalseException>(() => HddOfflineReplacementFixturesTests.ResolveProofRoot(null, null));

    [Fact]
    public void MissingExplicitRootIsNotReplacedByTmpdir()
    {
        var missing = Path.Combine(temporaryDirectory, "missing");
        Assert.Throws<TrueException>(() => HddOfflineReplacementFixturesTests.ResolveProofRoot(missing, temporaryDirectory));
        Assert.False(Directory.Exists(missing));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory));
    }

    public void Dispose() => Directory.Delete(temporaryDirectory, true);
}
