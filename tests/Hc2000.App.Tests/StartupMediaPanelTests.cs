using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Hc2000.App.Configuration;
using Hc2000.App.Library;
using Hc2000.App.Media;
using Hc2000.App.Settings;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class StartupMediaPanelTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hc2000-media-choices-" + Guid.NewGuid().ToString("N"));
    private StartupMediaPanel Panel(MachineConfiguration? original = null) => new(original ?? new(), new SettingsStore(root), configDirectories: [], baseDirectory: root);
    private string Disk(string name = "test.dsk")
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, MediaSessionTests.Disk().ToArray());
        return path;
    }
    private DiskLibraryCatalog Catalog(string path)
    {
        Directory.CreateDirectory(root);
        var entry = new DiskLibraryEntry("22222222-2222-4222-8222-222222222222", "Library disk", path,
            new FileInfo(path).Length, new string('1', 64), null, DiskLibraryFormat.StandardDsk,
            new("unit test", "fixture"), new(DiskLibraryContentState.Unknown, null, [], "not-scanned"));
        var catalogPath = Path.Combine(root, "catalog.json");
        File.WriteAllBytes(catalogPath, DiskLibrarySchema.Encode(new(1, [entry], [])));
        return DiskLibraryCatalog.Load(catalogPath);
    }
    private static TextBox Field(StartupMediaPanel panel, string name) => panel.GetLogicalDescendants().OfType<TextBox>().Single(c => c.Name == name);

    [AvaloniaFact]
    public async Task NamedInstalledBundleIsSelectableWithoutScanningOrTypingAndDoesNotWriteSource()
    {
        Directory.CreateDirectory(Path.Combine(root, "disks"));
        var path = Path.Combine(root, "disks", "CPM22-HC.DSK");
        var originalBytes = MediaSessionTests.Disk().ToArray();
        File.WriteAllBytes(path, originalBytes);
        File.WriteAllBytes(Path.Combine(root, "disks", "not-in-catalog.dsk"), originalBytes);
        var panel = Panel();
        var row = Assert.Single(panel.Listed.Where(r => r.Selectable));
        Assert.Equal(path, row.Path);
        Assert.Contains("read-only", row.Detail);
        Assert.Null(panel.ApplyTo(new()).DiskA);
        Assert.True(await panel.SelectFloppyAsync(row, DiskSlot.A), panel.Status);
        var draft = panel.ApplyTo(new());
        Assert.Equal(path, draft.DiskA!.Path);
        Assert.False(draft.DiskA.Writeback);
        Assert.Null(draft.DiskB);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        Assert.DoesNotContain(panel.Listed, r => r.Path?.EndsWith("not-in-catalog.dsk") == true);
    }

    [AvaloniaFact]
    public void EmptyUnavailableCatalogExplainsExistingConfigurationRouteAndDoesNotGuessRawGeometry()
    {
        Directory.CreateDirectory(root);
        var panel = Panel();
        Assert.All(panel.Listed, r => Assert.False(r.Selectable));
        Assert.Contains(panel.Listed, r => r.Reason!.Contains("configured-path route"));
        Assert.Contains(panel.Listed, r => r.Reason!.Contains("not configured"));
        Assert.Contains(panel.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "Load disk library"));
        var raw = Path.Combine(root, "unconfigured.raw");
        File.WriteAllBytes(raw, new byte[737280]);
        panel.Refresh();
        Assert.DoesNotContain(panel.Listed, r => r.Path == raw);
    }

    [AvaloniaFact]
    public async Task MissingInvalidOrStaleSelectionLeavesPriorDraftUnchanged()
    {
        var path = Disk();
        var prior = new MachineConfiguration { DiskB = new(path) };
        var panel = Panel(prior);
        var row = panel.Listed.First(r => r.Selectable);
        File.WriteAllText(path, "not a DSK");
        Assert.False(await panel.SelectFloppyAsync(row, DiskSlot.A));
        Assert.Null(panel.ApplyTo(prior).DiskA);
        Assert.Equal(prior.DiskB, panel.ApplyTo(prior).DiskB);
        File.Delete(path);
        Assert.False(await panel.SelectFloppyAsync(row, DiskSlot.B));
        Assert.Equal(prior.DiskB, panel.ApplyTo(prior).DiskB);
        Assert.Contains("Not selected:", panel.Status);
    }

    [AvaloniaFact]
    public async Task LibraryHandleIsRevalidatedAtSelectionAndBuildWithoutChangingCatalog()
    {
        var path = Disk();
        var catalog = Catalog(path);
        var before = File.ReadAllBytes(catalog.CatalogPath);
        var panel = Panel(); panel.SetDiskLibrary(catalog);
        var row = panel.Listed.Single(r => r.Selectable && r.Handle.StartsWith("disk:", StringComparison.Ordinal));
        Assert.True(await panel.SelectFloppyAsync(row, DiskSlot.B), panel.Status);
        Assert.Equal(before, File.ReadAllBytes(catalog.CatalogPath));
        File.WriteAllBytes(catalog.CatalogPath, DiskLibrarySchema.Encode(new(1, [], [])));
        Assert.Throws<IOException>(() => panel.ApplyTo(new()));
        Assert.False(await panel.SelectFloppyAsync(row, DiskSlot.A));
    }

    [AvaloniaFact]
    public async Task LibraryLoadRefusalPreservesChoicesAndSelection()
    {
        var path = Disk(); var panel = Panel(); panel.SetDiskLibrary(Catalog(path));
        var row = panel.Listed.First(r => r.Selectable);
        Assert.True(await panel.SelectFloppyAsync(row, DiskSlot.A));
        File.WriteAllText(Path.Combine(root, "broken.json"), "{broken");
        Assert.False(panel.LoadCatalog(Path.Combine(root, "broken.json")));
        Assert.Equal(path, panel.ApplyTo(new()).DiskA!.Path);
        Assert.Equal(row, panel.Listed.First(r => r.Selectable));
    }

    [AvaloniaFact]
    public async Task SelectionIsPanelLocalAndClearCancelsPendingChoice()
    {
        var path = Disk(); var configured = new MachineConfiguration { DiskB = new(path) };
        var a = Panel(configured); var b = Panel(configured);
        var pending = a.SelectFloppyAsync(a.Listed.First(r => r.Selectable), DiskSlot.A);
        a.Clear(DiskSlot.A);
        await pending;
        Assert.Null(a.ApplyTo(configured).DiskA);
        Assert.Null(b.ApplyTo(configured).DiskA);
        Assert.True(await a.SelectFloppyAsync(a.Listed.First(r => r.Selectable), DiskSlot.A));
        Assert.NotNull(a.ApplyTo(configured).DiskA);
        Assert.Null(b.ApplyTo(configured).DiskA);
        Assert.Null(configured.DiskA);
    }

    [AvaloniaFact]
    public async Task RawDescriptorAndUntouchedWritebackArePreservedWithoutGrantingWriteAuthority()
    {
        Directory.CreateDirectory(root);
        var raw = Path.Combine(root, "geometry.raw"); File.WriteAllBytes(raw, new byte[737280]);
        var configured = new FloppyConfiguration(raw, false, new("RawFloppy", 80, 2, 9, 512));
        var original = new MachineConfiguration { DiskB = configured };
        var panel = Panel(original);
        Assert.Equal(configured, panel.ApplyTo(original).DiskB);
        Assert.True(await panel.SelectFloppyAsync(panel.Listed.First(r => r.Selectable), DiskSlot.A));
        Assert.Equal(configured, panel.ApplyTo(original).DiskA);
        Assert.Equal(configured, panel.ApplyTo(original).DiskB);
        Field(panel, "PrestartDiskA").Text = Path.Combine(root, "other.dsk");
        Assert.Equal("DskContainer", panel.ApplyTo(original).DiskA!.Descriptor.Kind);
        var writable = original with { DiskB = configured with { Writeback = true } };
        var refused = Panel(writable);
        Assert.DoesNotContain(refused.Listed, r => r.Selectable);
        Assert.Equal(writable.DiskB, refused.ApplyTo(writable).DiskB);
        var catalog = Catalog(Disk()); refused.SetDiskLibrary(catalog);
        Assert.False(await refused.SelectFloppyAsync(refused.Listed.Single(r => r.Selectable), DiskSlot.B));
        Assert.True(refused.ApplyTo(writable).DiskB!.Writeback);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SamePathCandidatesWithDistinctWritebackIntentRemainAvailable(bool writebackA)
    {
        var path = Disk();
        var before = File.ReadAllBytes(path);
        var original = new MachineConfiguration { DiskA = new(path, writebackA), DiskB = new(path, !writebackA) };
        var panel = Panel(original);
        var rows = panel.Listed.Where(r => r.Path == path).ToArray();
        Assert.Equal(2, rows.Length);
        var readOnly = Assert.Single(rows, r => r.Selectable);
        Assert.Equal(writebackA ? "configured:B" : "configured:A", readOnly.Handle);
        Assert.Contains("writeback", Assert.Single(rows, r => !r.Selectable).Reason!);
        var writableSlot = writebackA ? DiskSlot.A : DiskSlot.B;
        Assert.False(await panel.SelectFloppyAsync(readOnly, writableSlot));
        Assert.Equal(original.DiskA, panel.ApplyTo(original).DiskA);
        Assert.Equal(original.DiskB, panel.ApplyTo(original).DiskB);
        panel.Clear(writableSlot); // explicit removal, not silently revoking an existing writeback request
        Assert.True(await panel.SelectFloppyAsync(readOnly, writableSlot));
        Assert.False(panel.ApplyTo(original).DiskA!.Writeback);
        Assert.False(panel.ApplyTo(original).DiskB!.Writeback);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [AvaloniaFact]
    public void HddChoiceUsesExplicitEightMiBDescriptorAndHardwareGateAndNeverWrites()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "data.raw");
        using (var file = File.Create(path)) file.SetLength(HddMediaAdapter.ImageLength);
        var original = new MachineConfiguration { Hdd = [new("C", path)] };
        var panel = Panel(original);
        var row = Assert.Single(panel.Listed.Where(r => r.Kind == "hdd"));
        Assert.False(panel.SelectHdd(row, "D"));
        Assert.Single(panel.ApplyTo(original).Hdd);
        panel.SetHddEnabled(true);
        Assert.True(panel.SelectHdd(row, "D"));
        Assert.Equal(new[] { "C", "D" }, panel.ApplyTo(original).Hdd.Select(h => h.Slot));
        Assert.All(panel.ApplyTo(original).Hdd, h => Assert.True(h.WriteProtected));
        using (var file = File.OpenWrite(path)) file.SetLength(10);
        Assert.False(panel.SelectHdd(row, "E"));
        Assert.Equal(2, panel.ApplyTo(original).Hdd.Length);
        Assert.Single(original.Hdd);
    }

    [AvaloniaFact]
    public void BootstrapLoadsOnlyNamedCatalogAndExplicitProviderWinsWithoutOverwritingInvalidFile()
    {
        var path = Disk(); var catalog = Catalog(path);
        var bootstrap = Path.Combine(root, "disk-library.json");
        File.Copy(catalog.CatalogPath, bootstrap);
        var panel = Panel();
        Assert.Contains(panel.Listed, r => r.Selectable && r.Path == path);
        const string invalid = "{not a catalog";
        File.WriteAllText(bootstrap, invalid);
        var refused = Panel();
        Assert.Contains(refused.Listed, r => r.Reason?.Contains("original file retained") == true);
        var supplied = new StartupMediaPanel(new(), new SettingsStore(root), catalog, configDirectories: [], baseDirectory: root);
        Assert.Contains(supplied.Listed, r => r.Selectable && r.Path == path);
        Assert.Equal(invalid, File.ReadAllText(bootstrap));
    }

    [AvaloniaFact]
    public void HddDeselectRemainsAvailableWhileDisabledAndExplicitProtectionIsRetained()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "data.raw");
        using (var file = File.Create(path)) file.SetLength(HddMediaAdapter.ImageLength);
        var original = new MachineConfiguration { Hdd = [new("C", path, false)] };
        var panel = Panel(original); panel.SetHddEnabled(true);
        var row = panel.Listed.Single(r => r.Kind == "hdd");
        Assert.True(panel.SelectHdd(row, "D"));
        Assert.False(panel.ApplyTo(original).Hdd[1].WriteProtected);
        panel.SetHddEnabled(false);
        Assert.Equal(2, panel.ApplyTo(original).Hdd.Length); // the hardware toggle never silently clears choices
        panel.ClearHdd("C"); panel.ClearHdd("D");
        Assert.Empty(panel.ApplyTo(original).Hdd);
        Assert.Single(original.Hdd);
    }

    [AvaloniaFact]
    public void CombinedInventoryHasFiniteRowsAndNoImplicitSaveOrCatalogCreation()
    {
        Directory.CreateDirectory(root);
        var entries = ImmutableArray.CreateBuilder<DiskLibraryEntry>();
        for (int i = 0; i < 130; i++)
        {
            var path = Path.Combine(root, $"disk{i}.dsk"); File.WriteAllBytes(path, new byte[16]);
            entries.Add(new(Guid.NewGuid().ToString(), "Disk " + i, path, 16, new string('1', 64), null,
                DiskLibraryFormat.StandardDsk, new("unit test", "fixture"), new(DiskLibraryContentState.Unknown, null, [], "not-scanned")));
        }
        var catalogPath = Path.Combine(root, "other-catalog.json");
        File.WriteAllBytes(catalogPath, DiskLibrarySchema.Encode(new(1, entries.ToImmutable(), [])));
        var panel = new StartupMediaPanel(new(), new SettingsStore(root), DiskLibraryCatalog.Load(catalogPath), configDirectories: [], baseDirectory: root);
        Assert.Equal(InlineMediaListing.MaxRowsPerPage + 1, panel.Listed.Length);
        Assert.Contains("page limit", panel.Listed[^1].Reason!);
        panel.Refresh();
        Assert.False(File.Exists(Path.Combine(root, "disk-library.json")));
        Assert.False(File.Exists(Path.Combine(root, "settings.json")));
    }

    [AvaloniaFact]
    public void SupplementalRawHddConfigurationFeedsInlineChoicesWithoutEditingSettingsOrStartingHardware()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "existing.raw");
        using (var file = File.Create(path)) file.SetLength(HddMediaAdapter.ImageLength);
        var original = new MachineConfiguration(); var panel = Panel(original);
        var field = Field(panel, "PrestartHddC");
        Assert.False(field.IsEnabled);
        panel.SetHddEnabled(true);
        Assert.True(field.IsEnabled);
        field.Text = path; panel.Refresh();
        var row = panel.Listed.Single(r => r.Kind == "hdd" && r.Selectable);
        Assert.Equal(path, row.Path);
        var protection = panel.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Name == "PrestartHddCProtected");
        protection.IsChecked = false;
        Assert.False(panel.SelectHdd(row, "D")); // stale rows cannot overwrite an explicit protection change
        Assert.Empty(Field(panel, "PrestartHddD").Text!);
        protection.IsChecked = true; panel.Refresh();
        Assert.True(panel.SelectHdd(row, "D"));
        var draft = panel.ApplyTo(original);
        Assert.Equal(original.Profile, draft.Profile);
        Assert.Equal(new[] { "C", "D" }, draft.Hdd.Select(h => h.Slot));
        Assert.All(draft.Hdd, h => Assert.True(h.WriteProtected));
        panel.SetHddEnabled(false); panel.ClearHdd("C");
        Assert.Equal("D", Assert.Single(panel.ApplyTo(original).Hdd).Slot);
        Assert.False(File.Exists(new SettingsStore(root).FilePath));
        Assert.Empty(original.Hdd);
        Assert.Equal(HddMediaAdapter.ImageLength, new FileInfo(path).Length);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
