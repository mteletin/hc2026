using Hc2000.App.Library;
using System.Runtime.InteropServices;
using Xunit;
using static Hc2000.App.Tests.DiskLibraryCatalogLiteralFixtures;

namespace Hc2000.App.Tests;

public sealed class DiskLibraryCatalogAdmissionTests
{
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existing, string alias);

    [Fact]
    public async Task HardlinkIdentityIsRefusedButOriginalAndAliasAreUntouched()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux native file identity contract");
        using var dir = new DiskLibraryCatalogTestDirectory();
        var source = dir.Write("original.dsk", Standard());
        var alias = Path.Combine(dir.Root, "hardlink.dsk");
        Assert.Equal(0, Link(source, alias));
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        Assert.Equal(DiskLibraryRefusal.Accepted, (await catalog.RegisterAsync(source, "fixture")).Reason);
        Assert.Equal(DiskLibraryRefusal.RepeatOrAlias, (await catalog.RegisterAsync(alias, "alias")).Reason);
        Assert.Single(catalog.Entries);
        Assert.Equal(Standard(), File.ReadAllBytes(source));
        Assert.Equal(Standard(), File.ReadAllBytes(alias));
    }

    [Fact]
    public async Task AccountedFileIdentityRefusesEvenWhenAccountedPathIsDifferent()
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        var source = dir.Write("original.dsk", Standard());
        var initial = DiskLibraryCatalog.Load(dir.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        var first = await initial.RegisterAsync(source, "fixture");
        var fileId = Assert.IsType<string>(first.Entry!.FileId);
        var admission = new DiskLibraryHostAdmission([new AccountedHostFile(Path.Combine(dir.Root, "another-name.dsk"), fileId)], false);
        var blocked = DiskLibraryCatalog.Load(dir.CatalogPath, admission);
        Assert.Equal(DiskLibraryRefusal.ActiveWriter, (await blocked.RegisterAsync(source, "fixture")).Reason);
        Assert.Empty(blocked.Entries);
        Assert.Equal(Standard(), File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData(4194304, true)]
    [InlineData(4194305, false)]
    public async Task ActualContainerByteCapIncludesUninterpretedTrailingBytes(int length, bool accepted)
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        var bytes = new byte[length];
        Standard().CopyTo(bytes, 0);
        var source = dir.Write("padded.dsk", bytes);
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        var result = await catalog.RegisterAsync(source, "fixture");
        Assert.Equal(accepted ? DiskLibraryRefusal.Accepted : DiskLibraryRefusal.TooLarge, result.Reason);
        if (accepted) Assert.Equal(length, result.Entry!.Length);
        else Assert.Empty(catalog.Entries);
        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.False(File.Exists(dir.CatalogPath));
    }

    [Fact]
    public async Task UnformattedExtendedContainerRemainsUnknownWhenScanningRequested()
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        var source = dir.Write("unformatted.dsk", Unformatted());
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        var result = await catalog.RegisterAsync(source, "fixture", listContents: true);
        Assert.Equal(DiskLibraryRefusal.Accepted, result.Reason);
        Assert.Equal(UnformattedHash, result.Entry!.Sha256);
        Assert.Equal(DiskLibraryContentState.Unknown, result.Entry.Contents.State);
        Assert.Empty(result.Entry.Contents.Files);
        Assert.Equal(Unformatted(), File.ReadAllBytes(source));
    }

    [Fact]
    public async Task OwnershipChangesAfterLoadAreNotAuthorizedByOldMetadata()
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        var source = dir.Write("original.dsk", Standard());
        var second = dir.Write("second.dsk", Extended());
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        var entry = (await catalog.RegisterAsync(source, "fixture")).Entry!;
        catalog.SetAdmission(null);
        Assert.Equal(DiskLibraryAvailability.OwnershipUnknown, (await catalog.VerifyAsync(entry.Id)).Status);
        Assert.Equal(DiskLibraryRefusal.OwnershipUnknown, (await catalog.RegisterAsync(second, "fixture")).Reason);
        catalog.SetAdmission(new DiskLibraryHostAdmission([], true));
        Assert.Equal(DiskLibraryAvailability.ActiveWriter, (await catalog.VerifyAsync(entry.Id)).Status);
        Assert.Equal(DiskLibraryRefusal.ActiveWriter, (await catalog.RegisterAsync(second, "fixture")).Reason);
        catalog.SetAdmission(new DiskLibraryHostAdmission([new AccountedHostFile(Path.Combine(dir.Root, "alias-name.dsk"), entry.FileId)], false));
        Assert.Equal(DiskLibraryAvailability.ActiveWriter, (await catalog.VerifyAsync(entry.Id)).Status);
        catalog.SetAdmission(DiskLibraryHostAdmission.StoppedHost);
        Assert.Equal(DiskLibraryAvailability.Available, (await catalog.VerifyAsync(entry.Id)).Status);
        Assert.Single(catalog.Entries);
        Assert.Equal(Standard(), File.ReadAllBytes(source));
        Assert.Equal(Extended(), File.ReadAllBytes(second));
        Assert.False(File.Exists(dir.CatalogPath));
    }

    [Fact]
    public async Task OmittedHostAdmissionNeverInventsAStoppedHost()
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        var source = dir.Write("source.dsk", Standard());
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath);
        var result = await catalog.RegisterAsync(source, "literal fixture");
        Assert.Equal(DiskLibraryRefusal.OwnershipUnknown, result.Reason);
        Assert.Null(result.Entry);
        Assert.Empty(catalog.Entries);
        Assert.Equal(Standard(), File.ReadAllBytes(source));
        Assert.False(File.Exists(dir.CatalogPath));
    }

    [Theory]
    [InlineData(false, StandardHash, DiskLibraryFormat.StandardDsk)]
    [InlineData(true, ExtendedHash, DiskLibraryFormat.ExtendedDsk)]
    public async Task ExplicitStoppedFactsRegisterLiteralBytesWithoutChangingOriginal(
        bool extended, string hash, DiskLibraryFormat format)
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        var bytes = extended ? Extended() : Standard();
        var source = dir.Write("source.not-a-format-hint", bytes);
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        var result = await catalog.RegisterAsync(source, "literal attribution", "not permission");
        Assert.Equal(DiskLibraryRefusal.Accepted, result.Reason);
        var entry = Assert.IsType<DiskLibraryEntry>(result.Entry);
        Assert.Equal(hash, entry.Sha256);
        Assert.Equal(768, entry.Length);
        Assert.Equal(format, entry.Format);
        Assert.Equal("source.not-a-format-hint", entry.Name);
        Assert.Equal("literal attribution", entry.Provenance.Source);
        Assert.Equal("not permission", entry.Provenance.Note);
        Assert.Equal("not-scanned", entry.Contents.Reason);
        Assert.Empty(entry.Contents.Files);
        Assert.Equal(entry.Id, Guid.Parse(entry.Id).ToString("D"));
        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.False(File.Exists(dir.CatalogPath));
        Assert.Equal(DiskLibraryAvailability.Available, (await catalog.VerifyAsync(entry.Id)).Status);
    }

    [Fact]
    public async Task EqualBytesAtIndependentFilesAreNotTheSameIdentity()
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        var first = dir.Write("first.dsk", Standard());
        var second = dir.Write("second.dsk", Standard());
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        var a = await catalog.RegisterAsync(first, "fixture");
        var b = await catalog.RegisterAsync(second, "fixture");
        Assert.Equal(DiskLibraryRefusal.Accepted, a.Reason);
        Assert.Equal(DiskLibraryRefusal.Accepted, b.Reason);
        Assert.NotEqual(a.Entry!.Id, b.Entry!.Id);
        Assert.NotEqual(a.Entry.FileId, b.Entry.FileId);
        Assert.Equal(StandardHash, a.Entry.Sha256);
        Assert.Equal(StandardHash, b.Entry.Sha256);
        Assert.Equal(DiskLibraryRefusal.RepeatOrAlias, (await catalog.RegisterAsync(first, "again")).Reason);
        Assert.Equal(2, catalog.Entries.Count);
        Assert.Equal(Standard(), File.ReadAllBytes(first));
        Assert.Equal(Standard(), File.ReadAllBytes(second));
    }

    [Fact]
    public async Task KnownWriterOrAccountedPathRefusesBeforeRegistration()
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        // Unsupported bytes ensure a writer refusal is not masked by container parsing.
        var source = dir.Write("source.dsk", [1, 2, 3, 4]);
        foreach (var admission in new[]
        {
            new DiskLibraryHostAdmission([], true),
            new DiskLibraryHostAdmission([new AccountedHostFile(source, null)], false)
        })
        {
            var catalog = DiskLibraryCatalog.Load(dir.CatalogPath, admission);
            Assert.Equal(DiskLibraryRefusal.ActiveWriter, (await catalog.RegisterAsync(source, "fixture")).Reason);
            Assert.Empty(catalog.Entries);
        }
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task SymlinkedAncestorIsNotSafeJustBecauseTheFinalFileIsRegular()
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        var real = Directory.CreateDirectory(Path.Combine(dir.Root, "real")).FullName;
        var bytes = Standard();
        File.WriteAllBytes(Path.Combine(real, "source.dsk"), bytes);
        Directory.CreateSymbolicLink(Path.Combine(dir.Root, "linked"), real);
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        var result = await catalog.RegisterAsync(Path.Combine(dir.Root, "linked", "source.dsk"), "fixture");
        Assert.NotEqual(DiskLibraryRefusal.Accepted, result.Reason);
        Assert.Null(result.Entry);
        Assert.Empty(catalog.Entries);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(real, "source.dsk")));
    }

    [Fact]
    public async Task FinalSymlinkIsRefusedWithoutChangingTarget()
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        var source = dir.Write("source.dsk", Standard());
        var link = Path.Combine(dir.Root, "link.dsk");
        File.CreateSymbolicLink(link, source);
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        Assert.Equal(DiskLibraryRefusal.FinalLink, (await catalog.RegisterAsync(link, "fixture")).Reason);
        Assert.Empty(catalog.Entries);
        Assert.Equal(Standard(), File.ReadAllBytes(source));
    }

    [Fact]
    public async Task WholeContainerChangeDoesNotRefreshRegisteredDigest()
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        var source = dir.Write("source.dsk", Standard());
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        var result = await catalog.RegisterAsync(source, "fixture");
        var entry = Assert.IsType<DiskLibraryEntry>(result.Entry);
        var changed = Standard();
        changed[0x22] = 42; // creator/padding, not sector data
        File.WriteAllBytes(source, changed);
        Assert.Equal(DiskLibraryAvailability.Changed, (await catalog.VerifyAsync(entry.Id)).Status);
        Assert.Equal(StandardHash, Assert.Single(catalog.Entries).Sha256);
        Assert.Equal(changed, File.ReadAllBytes(source));
        File.Delete(source);
        Assert.Equal(DiskLibraryAvailability.Missing, (await catalog.VerifyAsync(entry.Id)).Status);
        Assert.Equal(StandardHash, Assert.Single(catalog.Entries).Sha256);
    }

    [Theory]
    [InlineData("random")]
    [InlineData("raw")]
    [InlineData("truncated")]
    [InlineData("bad-track")]
    [InlineData("track-sector-overflow")]
    public async Task UnsupportedLiteralContainersDoNotRegisterOrPublish(string kind)
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        var bytes = kind switch
        {
            "random" => new byte[] { 1, 2, 3, 4 },
            "raw" => new byte[128],
            "truncated" => Standard()[..^1],
            _ => Standard()
        };
        if (kind == "bad-track") bytes[0x100] = 0;
        if (kind == "track-sector-overflow") bytes[0x115] = 30;
        var source = dir.Write("plausible.dsk", bytes);
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        Assert.Equal(DiskLibraryRefusal.UnsupportedContainer, (await catalog.RegisterAsync(source, "fixture")).Reason);
        Assert.Empty(catalog.Entries);
        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.False(File.Exists(dir.CatalogPath));
    }
}
