using Hc2000.App.Library;
using System.Collections.Immutable;
using Xunit;
using static Hc2000.App.Tests.DiskLibraryCatalogLiteralFixtures;

namespace Hc2000.App.Tests;

public sealed class DiskLibraryCatalogPersistenceTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("short-write")]
    [InlineData("readback")]
    [InlineData("rename-before")]
    public async Task ProvenPrepublicationFailuresKeepPriorCatalogAndOriginal(string failure)
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        var prior = Utf8(EmptyJson);
        dir.Write("catalog.json", prior);
        var source = dir.Write("original.dsk", Standard());
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        await catalog.RegisterAsync(source, "fixture");
        var pub = new DiskLibraryPublication();
        var read = pub.ReadBack;
        if (failure == "create") pub.WriteStage = (_, _) => throw new IOException("injected create failure");
        if (failure == "short-write") pub.WriteStage = (path, bytes) => File.WriteAllBytesAsync(path, bytes[..7]);
        if (failure == "readback") pub.ReadBack = path => path == dir.CatalogPath ? read(path) : Task.FromException<ImmutableArray<byte>>(new IOException("injected read-back failure"));
        if (failure == "rename-before") pub.Publish = (_, _) => throw new IOException("injected refusal before rename");
        var result = await catalog.SaveAsync(pub);
        Assert.Equal(DiskLibrarySaveStatus.FailedBeforePublish, result.Status);
        Assert.Equal(prior, File.ReadAllBytes(dir.CatalogPath));
        Assert.Equal(Standard(), File.ReadAllBytes(source));
        Assert.Empty(Directory.GetFiles(dir.Root, "*.stage-*"));
        Assert.Equal(0, pub.InFlight);
        await pub.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RenameThenExceptionIsUnknownEvenWhenIntendedBytesAreVisible(bool existed)
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        if (existed) dir.Write("catalog.json", Utf8(EmptyJson));
        var source = dir.Write("original.dsk", Standard());
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        await catalog.RegisterAsync(source, "fixture");
        var pub = new DiskLibraryPublication
        {
            Publish = (stage, target) =>
            {
                File.Move(stage, target, overwrite: true);
                throw new IOException("acknowledgement lost after rename");
            }
        };
        var result = await catalog.SaveAsync(pub);
        Assert.Equal(DiskLibrarySaveStatus.Unknown, result.Status);
        Assert.Equal(StandardHash, Assert.Single(DiskLibrarySchema.Load(File.ReadAllBytes(dir.CatalogPath)).Entries).Sha256);
        Assert.Equal(Standard(), File.ReadAllBytes(source));
        Assert.Equal(0, pub.InFlight);
        await pub.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        // Rename consumed the stage name; reconciliation still requires fresh catalog facts.
        Assert.Empty(Directory.GetFiles(dir.Root, "*.stage-*"));
        var publishedBytes = File.ReadAllBytes(dir.CatalogPath);
        Assert.Equal(DiskLibrarySaveStatus.Published, (await catalog.ReconcileAsync()).Status);
        Assert.Empty(Directory.GetFiles(dir.Root, "*.stage-*"));
        Assert.Equal(publishedBytes, File.ReadAllBytes(dir.CatalogPath));
        Assert.Equal(Standard(), File.ReadAllBytes(source));
    }

    [Fact]
    public async Task EqualBytesCannotProveAnExceptionOccurredBeforePublication()
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        var prior = Utf8(EmptyJson);
        dir.Write("catalog.json", prior);
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath);
        var renamed = false;
        var pub = new DiskLibraryPublication
        {
            Publish = (stage, target) =>
            {
                File.Move(stage, target, overwrite: true);
                renamed = true;
                throw new IOException("rename completed, acknowledgement failed");
            }
        };
        var result = await catalog.SaveAsync(pub);
        Assert.True(renamed);
        Assert.Equal(prior, File.ReadAllBytes(dir.CatalogPath));
        Assert.Equal(DiskLibrarySaveStatus.Unknown, result.Status);
    }

    [Fact]
    public async Task CompletedStepsWithUnconfirmedFreshBytesAreUnknownAndRetainStage()
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        var prior = Utf8(EmptyJson);
        dir.Write("catalog.json", prior);
        var source = dir.Write("original.dsk", Standard());
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        await catalog.RegisterAsync(source, "fixture");
        var pub = new DiskLibraryPublication { Publish = (_, _) => { } };
        var result = await catalog.SaveAsync(pub);
        Assert.Equal(DiskLibrarySaveStatus.Unknown, result.Status);
        var stage = Assert.Single(Directory.GetFiles(dir.Root, "*.stage-*"));
        Assert.Equal(StandardHash, Assert.Single(DiskLibrarySchema.Load(File.ReadAllBytes(stage)).Entries).Sha256);
        Assert.Equal(prior, File.ReadAllBytes(dir.CatalogPath));
        Assert.Equal(Standard(), File.ReadAllBytes(source));
        Assert.Equal(DiskLibrarySaveStatus.FailedBeforePublish, (await catalog.ReconcileAsync()).Status);
        Assert.Empty(Directory.GetFiles(dir.Root, "*.stage-*"));
        Assert.Equal(prior, File.ReadAllBytes(dir.CatalogPath));
        Assert.Equal(Standard(), File.ReadAllBytes(source));
    }

    [Fact]
    public async Task UnreadableSettlementCannotAssertPriorBytesUntouchedOrDeleteStage()
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        var prior = Utf8(EmptyJson);
        dir.Write("catalog.json", prior);
        var source = dir.Write("original.dsk", Standard());
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        await catalog.RegisterAsync(source, "fixture");
        var pub = new DiskLibraryPublication { Publish = (_, _) => throw new IOException("unknown publication") };
        var read = pub.ReadBack;
        var catalogReads = 0;
        pub.ReadBack = path => path == dir.CatalogPath && ++catalogReads > 1
            ? Task.FromException<ImmutableArray<byte>>(new IOException("fresh facts unavailable")) : read(path);
        Assert.Equal(DiskLibrarySaveStatus.Unknown, (await catalog.SaveAsync(pub)).Status);
        Assert.Single(Directory.GetFiles(dir.Root, "*.stage-*"));
        Assert.Equal(prior, File.ReadAllBytes(dir.CatalogPath));
        Assert.Equal(Standard(), File.ReadAllBytes(source));
    }

    [Fact]
    public async Task UnknownPublicationNeedsReconciliationBeforeAnotherMutation()
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        dir.Write("catalog.json", Utf8(EmptyJson));
        var firstSource = dir.Write("first.dsk", Standard());
        var secondSource = dir.Write("second.dsk", Extended());
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        await catalog.RegisterAsync(firstSource, "fixture");
        var differentCatalog = Utf8(CatalogJson());
        var pub = new DiskLibraryPublication
        {
            Publish = (_, target) =>
            {
                File.WriteAllBytes(target, differentCatalog);
                throw new IOException("publication target no longer matches either known snapshot");
            }
        };
        Assert.Equal(DiskLibrarySaveStatus.Unknown, (await catalog.SaveAsync(pub)).Status);
        await pub.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        // Drain settles the operation, not reconciliation of the conflicting actual catalog.
        var result = await catalog.RegisterAsync(secondSource, "fixture").WaitAsync(TimeSpan.FromSeconds(35));
        Assert.Equal(DiskLibraryRefusal.ReconciliationNeeded, result.Reason);
        Assert.Single(catalog.Entries);
        Assert.Equal(differentCatalog, File.ReadAllBytes(dir.CatalogPath));
        Assert.Single(Directory.GetFiles(dir.Root, "*.stage-*"));
        Assert.Equal(DiskLibrarySaveStatus.Unknown, (await catalog.ReconcileAsync()).Status);
        Assert.Equal(differentCatalog, File.ReadAllBytes(dir.CatalogPath));
        Assert.Single(Directory.GetFiles(dir.Root, "*.stage-*"));
        Assert.Equal(DiskLibraryRefusal.ReconciliationNeeded, (await catalog.RegisterAsync(secondSource, "fixture")).Reason);
        // Explicit private-fixture restoration, not drain or an automatic production retry.
        File.WriteAllBytes(dir.CatalogPath, Utf8(EmptyJson));
        Assert.Equal(DiskLibrarySaveStatus.FailedBeforePublish, (await catalog.ReconcileAsync()).Status);
        Assert.Empty(Directory.GetFiles(dir.Root, "*.stage-*"));
        Assert.Equal(Utf8(EmptyJson), File.ReadAllBytes(dir.CatalogPath));
        Assert.Equal(DiskLibraryRefusal.Accepted, (await catalog.RegisterAsync(secondSource, "fixture")).Reason);
        Assert.Equal(2, catalog.Entries.Count);
        Assert.Equal(Standard(), File.ReadAllBytes(firstSource));
        Assert.Equal(Extended(), File.ReadAllBytes(secondSource));
    }

    [Fact]
    public async Task RegistrationGateTimeoutCannotAdmitASecondMutation()
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        dir.Write("catalog.json", Utf8(EmptyJson));
        var source = dir.Write("new-original.dsk", Standard());
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pub = new DiskLibraryPublication
        {
            WriteStage = async (path, bytes) =>
            {
                await File.WriteAllBytesAsync(path, bytes);
                entered.SetResult();
                await held.Task;
            }
        };
        var saving = catalog.SaveAsync(pub);
        DiskLibraryRegisterResult? result = null;
        Exception? saveFailure = null;
        var pending = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            result = await catalog.RegisterAsync(source, "fixture").WaitAsync(TimeSpan.FromSeconds(35));
            pending = pub.InFlight == 1 && !saving.IsCompleted && !pub.DrainAsync().IsCompleted;
        }
        finally
        {
            held.TrySetResult();
            saveFailure = await Record.ExceptionAsync(async () => await saving.WaitAsync(TimeSpan.FromSeconds(10)));
            await pub.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.NotNull(result);
        Assert.NotEqual(DiskLibraryRefusal.Accepted, result.Reason);
        Assert.Empty(catalog.Entries);
        Assert.True(pending);
        Assert.Null(saveFailure);
        Assert.Equal(Standard(), File.ReadAllBytes(source));
    }

    [Fact]
    public async Task InflightStageHoldsMutationAndDrainUntilRealOperationSettles()
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        var prior = Utf8(EmptyJson);
        dir.Write("catalog.json", prior);
        var source = dir.Write("original.dsk", Standard());
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        await catalog.RegisterAsync(source, "fixture");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stageReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pub = new DiskLibraryPublication
        {
            WriteStage = async (path, bytes) =>
            {
                await File.WriteAllBytesAsync(path, bytes);
                stageReady.SetResult(path);
                await held.Task;
            }
        };
        var first = catalog.SaveAsync(pub);
        Task<DiskLibrarySaveResult>? second = null;
        try
        {
            var stage = await stageReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, pub.InFlight);
            Assert.False(first.IsCompleted);
            Assert.False(pub.DrainAsync().IsCompleted);
            second = catalog.SaveAsync();
            Assert.False(second.IsCompleted);
            Assert.True(File.Exists(stage));
            Assert.Equal(prior, File.ReadAllBytes(dir.CatalogPath));
            Assert.Equal(Standard(), File.ReadAllBytes(source));
        }
        finally
        {
            held.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(10));
            await pub.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(DiskLibrarySaveStatus.Published, (await first).Status);
        Assert.Equal(DiskLibrarySaveStatus.Published, (await second!).Status);
        Assert.Equal(0, pub.InFlight);
        Assert.Empty(Directory.GetFiles(dir.Root, "*.stage-*"));
        Assert.Equal(Standard(), File.ReadAllBytes(source));
    }

    [Fact]
    public void MissingCatalogRemainsAbsentUntilExplicitSuccessfulMutation()
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath);
        Assert.Empty(catalog.Entries);
        Assert.Empty(catalog.Search("").Matches);
        Assert.False(File.Exists(dir.CatalogPath));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Root));
    }

    [Fact]
    public async Task SuccessfulPublicationReloadsMetadataAndPreservesOriginalBytesAndMode()
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        dir.Write("catalog.json", Utf8(EmptyJson));
        var source = dir.Write("original.dsk", Standard());
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var mode = OperatingSystem.IsLinux() ? File.GetUnixFileMode(source) : default;
        var catalog = DiskLibraryCatalog.Load(dir.CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        var registered = await catalog.RegisterAsync(source, "independent fixture", "preserve literally");
        Assert.Equal(DiskLibraryRefusal.Accepted, registered.Reason);
        Assert.Equal(Utf8(EmptyJson), File.ReadAllBytes(dir.CatalogPath));
        Assert.Equal(DiskLibrarySaveStatus.Published, (await catalog.SaveAsync()).Status);
        var loaded = DiskLibraryCatalog.Load(dir.CatalogPath);
        var entry = Assert.Single(loaded.Entries);
        Assert.Equal(registered.Entry!.Id, entry.Id);
        Assert.Equal(StandardHash, entry.Sha256);
        Assert.Equal("preserve literally", entry.Provenance.Note);
        Assert.Equal("not-scanned", entry.Contents.Reason);
        Assert.Empty(Directory.GetFiles(dir.Root, "*.stage-*"));
        Assert.Equal(Standard(), File.ReadAllBytes(source));
        if (OperatingSystem.IsLinux()) Assert.Equal(mode, File.GetUnixFileMode(source));
        Assert.Equal(new[] { "catalog.json", "original.dsk" }, Directory.GetFiles(dir.Root).Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public void InvalidExistingCatalogIsNotMigratedRepairedOrPartiallyLoaded()
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        var invalid = Utf8(CatalogJson(EntryJson + ",null"));
        dir.Write("catalog.json", invalid);
        Assert.Throws<DiskLibrarySchemaException>(() => DiskLibraryCatalog.Load(dir.CatalogPath));
        Assert.Equal(invalid, File.ReadAllBytes(dir.CatalogPath));
        Assert.Equal(new[] { dir.CatalogPath }, Directory.GetFiles(dir.Root));
    }

    [Fact]
    public async Task StageCreationFailureCannotReportPublishedOrChangeSource()
    {
        using var dir = new DiskLibraryCatalogTestDirectory();
        var source = dir.Write("original.dsk", Extended());
        var missingParent = Path.Combine(dir.Root, "missing", "catalog.json");
        var catalog = DiskLibraryCatalog.Load(missingParent, DiskLibraryHostAdmission.StoppedHost);
        Assert.Equal(DiskLibraryRefusal.Accepted, (await catalog.RegisterAsync(source, "fixture")).Reason);
        Assert.Equal(DiskLibrarySaveStatus.FailedBeforePublish, (await catalog.SaveAsync()).Status);
        Assert.False(File.Exists(missingParent));
        Assert.Equal(Extended(), File.ReadAllBytes(source));
        Assert.Equal(new[] { source }, Directory.GetFiles(dir.Root));
    }
}
