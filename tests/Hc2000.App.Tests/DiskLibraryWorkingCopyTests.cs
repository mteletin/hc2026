using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Hc2000.App.Library;
using Hc2000.App.Media;
using Xunit;

namespace Hc2000.App.Tests;

// The fixture skips non-Linux hosts before any native or Unix mode operation.
#pragma warning disable CA1416

public sealed class DiskLibraryWorkingCopyTests
{
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existing, string alias);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiteralOriginalRemainsReadonlyAndEachCopyIsIndependent(bool extended)
    {
        using var f = new DiskLibraryWorkingCopyFixtures(extended);
        await f.RegisterAsync();
        for (var i = 0; i < 2; i++)
        {
            var result = await f.Copy();
            Assert.Equal(DiskLibraryCopyStatus.Created, result.Status);
            var copy = Assert.IsType<DiskLibraryCopy>(result.Copy);
            Assert.Equal(f.Hash, copy.OriginalSha256);
            Assert.Equal(f.Hash, copy.InitialSha256);
            Assert.Equal(f.Entry.Id, copy.EntryId);
            Assert.Equal(768, copy.Length);
            var path = Path.Combine(f.Root, copy.RelativePath);
            Assert.Equal(f.Bytes, File.ReadAllBytes(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.NotEqual(f.Entry.FileId, LinuxFileFacts.Inspect(path).FileId);
        }
        var paths = f.Catalog.Copies.Select(c => Path.Combine(f.Root, c.RelativePath)).ToArray();
        Assert.NotEqual(LinuxFileFacts.Inspect(paths[0]).FileId, LinuxFileFacts.Inspect(paths[1]).FileId);
        File.WriteAllBytes(paths[0], [7]);
        Assert.Equal(f.Bytes, File.ReadAllBytes(paths[1]));
        Assert.Equal(2, DiskLibrarySchema.Load(File.ReadAllBytes(f.CatalogPath)).Copies.Length);
        Assert.Equal(DiskLibraryWorkingCopyFixtures.Private, File.GetUnixFileMode(f.CopiesPath));
        f.Preserved(false);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("same-length-changed")]
    [InlineData("symlink")]
    [InlineData("hardlink")]
    [InlineData("oversize")]
    public async Task UnsafeOrChangedSourceRefusesBeforeWritingCandidates(string kind)
    {
        using var f = new DiskLibraryWorkingCopyFixtures();
        await f.RegisterAsync();
        var old = Path.Combine(f.Root, "saved-original");
        File.Move(f.Source, old);
        switch (kind)
        {
            case "changed": File.WriteAllBytes(f.Source, [1, 2, 3]); break;
            case "same-length-changed":
                var changed = (byte[])f.Bytes.Clone(); changed[0x210] ^= 1;
                File.WriteAllBytes(f.Source, changed); break;
            case "symlink": File.CreateSymbolicLink(f.Source, old); break;
            case "hardlink":
                Assert.Equal(0, Link(old, f.Source));
                f.Catalog.SetAdmission(new([new(old, f.Entry.FileId)], false));
                break;
            case "oversize": using (var stream = File.Create(f.Source)) stream.SetLength(4194305); break;
        }
        var result = await f.Copy();
        Assert.Equal(DiskLibraryCopyStatus.Refused, result.Status);
        f.NoCandidates();
        Assert.Equal(f.Bytes, File.ReadAllBytes(old));
        Assert.Equal(f.Prior, File.ReadAllBytes(f.CatalogPath));
    }

    [Fact]
    public async Task FreshHardlinkOfAnotherRegisteredOriginalCannotBecomeCopySource()
    {
        using var f = new DiskLibraryWorkingCopyFixtures();
        await f.RegisterAsync();
        var alias = Path.Combine(f.Root, "other-original.dsk");
        Assert.Equal(0, Link(f.Source, alias));
        var document = DiskLibrarySchema.Load(f.Prior);
        var other = f.Entry with { Id = Guid.NewGuid().ToString("d"), SourcePath = alias };
        File.WriteAllBytes(f.CatalogPath, DiskLibrarySchema.Encode(document with { Entries = document.Entries.Add(other) }));
        var catalog = DiskLibraryCatalog.Load(f.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        var prior = File.ReadAllBytes(f.CatalogPath);
        Assert.Equal(DiskLibraryCopyStatus.Refused, (await catalog.CreateWorkingCopyAsync(f.Entry.Id, f.Root)).Status);
        Assert.Empty(catalog.Copies); Assert.Equal(prior, File.ReadAllBytes(f.CatalogPath));
        Assert.Equal(f.Bytes, File.ReadAllBytes(alias)); f.Preserved(false);
    }

    [Theory]
    [InlineData(4194304, true)]
    [InlineData(4194305, false)]
    public async Task RealSourceReadAndDestinationRereadHonorContainerBudget(int length, bool allowed)
    {
        using var f = new DiskLibraryWorkingCopyFixtures();
        await f.RegisterAsync();
        var padded = new byte[length]; f.Bytes.CopyTo(padded, 0);
        var path = Path.Combine(f.Root, "padded.dsk"); File.WriteAllBytes(path, padded);
        var entry = f.Entry with
        {
            SourcePath = path, Length = Math.Min(length, 4194304),
            FileId = LinuxFileFacts.Inspect(path).FileId,
            Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(padded))
        };
        var document = DiskLibrarySchema.Load(f.Prior) with { Entries = [entry] };
        File.WriteAllBytes(f.CatalogPath, DiskLibrarySchema.Encode(document));
        var catalog = DiskLibraryCatalog.Load(f.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        var prior = File.ReadAllBytes(f.CatalogPath);
        var result = await catalog.CreateWorkingCopyAsync(entry.Id, f.Root);
        Assert.Equal(allowed ? DiskLibraryCopyStatus.Created : DiskLibraryCopyStatus.Refused, result.Status);
        if (allowed)
        {
            var copy = Assert.Single(catalog.Copies);
            Assert.Equal(length, copy.Length);
            Assert.Equal(padded, File.ReadAllBytes(Path.Combine(f.Root, copy.RelativePath)));
        }
        else { Assert.Empty(catalog.Copies); Assert.Equal(prior, File.ReadAllBytes(f.CatalogPath)); }
        Assert.Equal(padded, File.ReadAllBytes(path)); f.Preserved(false);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("default-array")]
    [InlineData("writer")]
    [InlineData("source")]
    [InlineData("missing-id")]
    public async Task OwnershipMustBeExplicitAndUnaccounted(string kind)
    {
        using var f = new DiskLibraryWorkingCopyFixtures();
        await f.RegisterAsync();
        f.Catalog.SetAdmission(kind switch
        {
            "unknown" => null,
            "default-array" => new(default, false),
            "writer" => new([], true),
            "source" => new([new(f.Source, f.Entry.FileId)], false),
            _ => new([new(Path.Combine(f.Root, "unknown-owner"), null)], false)
        });
        Assert.Equal(DiskLibraryCopyStatus.Refused, (await f.Copy()).Status);
        f.NoCandidates(); f.Preserved();
    }

    [Theory]
    [InlineData("root-mode")]
    [InlineData("copies-mode")]
    [InlineData("copies-link")]
    [InlineData("catalog-link")]
    [InlineData("catalog-source-hardlink")]
    [InlineData("wrong-root")]
    public async Task UnsafeNamespaceRefusesWithoutTouchingOriginal(string kind)
    {
        using var f = new DiskLibraryWorkingCopyFixtures();
        await f.RegisterAsync();
        switch (kind)
        {
            case "root-mode": File.SetUnixFileMode(f.Root, DiskLibraryWorkingCopyFixtures.Private | UnixFileMode.OtherRead); break;
            case "copies-mode": Directory.CreateDirectory(f.CopiesPath); File.SetUnixFileMode(f.CopiesPath, (UnixFileMode)0x1ed); break;
            case "copies-link": Directory.CreateSymbolicLink(f.CopiesPath, f.Root); break;
            case "catalog-link": File.Delete(f.CatalogPath); File.CreateSymbolicLink(f.CatalogPath, f.Source); break;
            case "catalog-source-hardlink": File.Delete(f.CatalogPath); Assert.Equal(0, Link(f.Source, f.CatalogPath)); break;
        }
        var result = kind == "wrong-root"
            ? await f.Catalog.CreateWorkingCopyAsync(f.Entry.Id, Path.Combine(f.Root, "elsewhere")) : await f.Copy();
        Assert.Equal(DiskLibraryCopyStatus.Refused, result.Status);
        Assert.Empty(f.Catalog.Copies);
        f.Preserved(!kind.StartsWith("catalog-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1023, true)]
    [InlineData(1024, false)]
    public async Task GlobalCopyBudgetIsCheckedBeforeCreatingFile(int count, bool allowed)
    {
        using var f = new DiskLibraryWorkingCopyFixtures();
        await f.RegisterAsync();
        var document = DiskLibrarySchema.Load(f.Prior);
        var entries = Enumerable.Range(0, 64).Select(i => f.Entry with
        {
            Id = Guid.NewGuid().ToString("d"), SourcePath = Path.Combine(f.Root, "metadata-only-" + i)
        }).ToImmutableArray();
        var copies = Enumerable.Range(0, count).Select(i =>
        {
            var id = Guid.NewGuid().ToString("d");
            return new DiskLibraryCopy(id, entries[i / 16].Id, "copies/" + id + ".dsk", f.Hash, f.Hash, 768);
        }).ToImmutableArray();
        File.WriteAllBytes(f.CatalogPath, DiskLibrarySchema.Encode(document with { Entries = entries.Add(f.Entry), Copies = copies }));
        var before = File.ReadAllBytes(f.CatalogPath);
        var catalog = DiskLibraryCatalog.Load(f.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        var result = await catalog.CreateWorkingCopyAsync(f.Entry.Id, f.Root);
        Assert.Equal(allowed ? DiskLibraryCopyStatus.Created : DiskLibraryCopyStatus.Refused, result.Status);
        Assert.Equal(1024, catalog.Copies.Count);
        if (!allowed)
        {
            Assert.Equal(before, File.ReadAllBytes(f.CatalogPath));
            Assert.False(Directory.Exists(f.CopiesPath));
        }
        f.Preserved(false);
    }

    [Theory]
    [InlineData(15, true)]
    [InlineData(16, false)]
    public async Task PerEntryBudgetAllowsSixteenthButNotSeventeenth(int count, bool allowed)
    {
        using var f = new DiskLibraryWorkingCopyFixtures();
        await f.RegisterAsync();
        var document = DiskLibrarySchema.Load(f.Prior);
        var copies = Enumerable.Range(0, count).Select(_ =>
        {
            var id = Guid.NewGuid().ToString("d");
            return new DiskLibraryCopy(id, f.Entry.Id, "copies/" + id + ".dsk", f.Hash, f.Hash, 768);
        }).ToImmutableArray();
        File.WriteAllBytes(f.CatalogPath, DiskLibrarySchema.Encode(document with { Copies = copies }));
        var catalog = DiskLibraryCatalog.Load(f.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        var before = File.ReadAllBytes(f.CatalogPath);
        var result = await catalog.CreateWorkingCopyAsync(f.Entry.Id, f.Root);
        Assert.Equal(allowed ? DiskLibraryCopyStatus.Created : DiskLibraryCopyStatus.Refused, result.Status);
        Assert.Equal(allowed ? 16 : count, catalog.Copies.Count);
        if (!allowed) Assert.Equal(before, File.ReadAllBytes(f.CatalogPath));
        f.Preserved(false);
    }
}
