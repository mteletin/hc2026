using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Layout;
using Hc2000.App.Library;
using Hc2000.App.Media;
using Hc2000.App.Settings;
using Hc2000.HostFiles;

namespace Hc2000.App.Configuration;

/// <summary>Inline, detached media choices. No scan, mount, writer, hardware switch or persistence.
/// Start still owns the guarded read/admission and native codec validation.</summary>
public sealed class StartupMediaPanel : UserControl
{
    private readonly MachineConfiguration original;
    private readonly string baseDirectory;
    private readonly IReadOnlyList<string> configDirectories;
    private readonly string? assetRoot;
    private readonly TextBox diskA, diskB;
    private FloppyConfiguration? selectedA, selectedB;
    private readonly Dictionary<string, (TextBox Path, CheckBox Protection, TextBox Volume)> hddFields = new();
    private readonly StackPanel rows = new() { Name = "StartupMediaRows", Spacing = 6 };
    private readonly TextBlock status = new() { Name = "StartupMediaStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Dictionary<string, FloppyConfiguration> floppyChoices = new();
    private readonly Dictionary<string, HddConfiguration> hddChoices = new();
    private DiskLibraryCatalog? library;
    private string? catalogPath;
    private string catalogDiagnostic = "Disk library not configured; load an existing catalog or configure local paths below.";
    private bool hddEnabled, selecting, factoryDefaultsConsidered;
    private int selectionRevision;
    private InlineMediaRow? selectedRowA, selectedRowB;
    public ImmutableArray<InlineMediaRow> Listed { get; private set; } = [];
    public string? Status => status.Text;

    public StartupMediaPanel(MachineConfiguration original, SettingsStore store, DiskLibraryCatalog? diskLibrary = null,
        string? assetRoot = null, IReadOnlyList<string>? configDirectories = null, string? baseDirectory = null)
    {
        this.original = original;
        this.assetRoot = assetRoot;
        var settingsDirectory = Path.GetDirectoryName(Path.GetFullPath(store.FilePath))!;
        this.configDirectories = configDirectories ?? new[] { settingsDirectory }.Concat(InstalledLayout.ConfigDirectories()).Distinct().ToArray();
        this.baseDirectory = baseDirectory ?? AppContext.BaseDirectory;
        if (diskLibrary is not null) { library = diskLibrary; catalogPath = diskLibrary.CatalogPath; }
        else
        {
            var path = Path.Combine(settingsDirectory, "disk-library.json");
            if (File.Exists(path))
            {
                try { library = LoadExistingCatalog(path); catalogPath = path; }
                catch (Exception ex) when (Expected(ex)) { catalogDiagnostic = "Disk library unavailable: " + ex.Message + "; original file retained. Load an existing catalog or configure local paths below."; }
            }
        }
        selectedA = original.DiskA; selectedB = original.DiskB;
        var form = new StackPanel { Spacing = 6 };
        form.Children.Add(new TextBlock { Text = "Available media — choices remain drafts until Start", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        form.Children.Add(rows);
        var refresh = new Button { Content = "Refresh available media" };
        refresh.Click += (_, _) => Refresh();
        form.Children.Add(refresh);
        var advanced = new StackPanel { Spacing = 6 };
        advanced.Children.Add(new TextBlock { Text = "Use an existing disk-library JSON catalog, or configure a local DSK path below. Raw images retain their explicitly configured geometry; no geometry is guessed.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var catalog = new TextBox { Name = "PrestartCatalogPath", PlaceholderText = "Existing disk-library catalog JSON path" };
        advanced.Children.Add(catalog);
        var load = new Button { Content = "Load disk library" };
        load.Click += (_, _) => LoadCatalog(catalog.Text ?? "");
        advanced.Children.Add(load);
        diskA = PathField(advanced, "A: configured path", "PrestartDiskA", original.DiskA?.Path);
        diskB = PathField(advanced, "B: configured path", "PrestartDiskB", original.DiskB?.Path);
        advanced.Children.Add(new TextBlock { Text = "Enabling HDD on an empty draft selects D.DSK/E.DSK/F.DSK from one bundled HCVD container (guest-writable private copies; no host writeback). No C: image or HDD boot is supplied. Existing raw 8 MiB inputs use a blank volume name; named volumes require one container and no A/B disks at cold Start.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        foreach (var slot in new[] { "C", "D", "E", "F" })
        {
            var hdd = original.Hdd.FirstOrDefault(h => h.Slot == slot);
            var field = PathField(advanced, slot + ": existing HDD path", "PrestartHdd" + slot, hdd?.Path);
            var volume = PathField(advanced, slot + ": named HCVD volume (blank for raw)", "PrestartHdd" + slot + "Volume", hdd?.Volume);
            var protection = new CheckBox { Name = "PrestartHdd" + slot + "Protected", Content = slot + ": write-protected (guest writes never grant host writeback)", IsChecked = hdd?.WriteProtected ?? true };
            field.IsEnabled = false; protection.IsEnabled = false; volume.IsEnabled = false;
            advanced.Children.Add(protection); hddFields[slot] = (field, protection, volume);
        }
        // Keep the existing path controls in the logical tree (automation/settings compatibility).
        form.Children.Add(new Expander { Header = "Existing library / advanced media configuration", Content = advanced });
        var clear = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var slot in new[] { DiskSlot.A, DiskSlot.B })
        {
            var button = new Button { Content = $"Clear {slot}:" };
            button.Click += (_, _) => Clear(slot);
            clear.Children.Add(button);
        }
        foreach (var slot in new[] { "C", "D", "E", "F" })
        {
            var button = new Button { Content = $"Clear {slot}:" };
            button.Click += (_, _) => ClearHdd(slot);
            clear.Children.Add(button);
        }
        form.Children.Add(clear); form.Children.Add(status); Content = form;
        Refresh();
    }

    private static TextBox PathField(StackPanel parent, string label, string name, string? value)
    {
        parent.Children.Add(new TextBlock { Text = label });
        var field = new TextBox { Name = name, Text = value ?? "" };
        parent.Children.Add(field); return field;
    }

    /// <summary>Consume the same canonical catalog as the running media panel; never mutate it.</summary>
    public void SetDiskLibrary(DiskLibraryCatalog? catalog) { library = catalog; catalogPath = catalog?.CatalogPath; Refresh(); }
    public bool LoadCatalog(string path)
    {
        try
        {
            if (!File.Exists(path)) throw new IOException("Catalog file is missing. Choose an existing disk-library JSON catalog.");
            var loaded = LoadExistingCatalog(path);
            library = loaded; catalogPath = loaded.CatalogPath; Refresh();
            status.Text = "Library loaded read-only; no machine or settings changed."; return true;
        }
        catch (Exception ex) when (Expected(ex)) { status.Text = "Library not loaded: " + ex.Message; return false; }
    }
    private static DiskLibraryCatalog LoadExistingCatalog(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Catalog must be an existing regular local file, not a link.");
        if (OperatingSystem.IsLinux())
        {
            var facts = LinuxFileFacts.Inspect(path);
            if (!facts.Known || !facts.Regular || facts.Link) throw new IOException("Catalog regular-file identity is unavailable.");
        }
        if (info.Length > DiskLibrarySchema.MaxCatalogBytes) throw new IOException("Catalog exceeds the existing disk-library byte limit.");
        return DiskLibraryCatalog.Load(path);
    }
    public void SetHddEnabled(bool enabled)
    {
        hddEnabled = enabled;
        if (enabled && !factoryDefaultsConsidered)
        {
            factoryDefaultsConsidered = true;
            // Never replace an existing selection, a typed draft, or a deliberate
            // clear when hardware is toggled again. Defaults are all-or-nothing.
            if (original.Hdd.IsEmpty && hddFields.Values.All(field => string.IsNullOrWhiteSpace(field.Path.Text)))
            {
                if (LaunchStartup.TryResolveDisk(HddFactoryImages.ContainerName, configDirectories, baseDirectory, out var path, out _))
                    foreach (var slot in new[] { "D", "E", "F" })
                    {
                        hddFields[slot].Path.Text = Path.GetFullPath(path);
                        hddFields[slot].Volume.Text = slot + ".DSK";
                        hddFields[slot].Protection.IsChecked = false;
                    }
            }
            Refresh();
        }
        foreach (var field in hddFields.Values) { field.Path.IsEnabled = enabled; field.Protection.IsEnabled = enabled; field.Volume.IsEnabled = enabled; }
        Render();
    }

    private ImmutableArray<HddConfiguration> HddDraft(bool canonicalPaths = true) => original.Hdd.Select(h => h.Slot)
        .Concat(hddFields.Keys).Distinct().Where(slot => !string.IsNullOrWhiteSpace(hddFields[slot].Path.Text))
        .Select(slot => new HddConfiguration(slot,
            canonicalPaths ? Path.GetFullPath(hddFields[slot].Path.Text!) : hddFields[slot].Path.Text!, hddFields[slot].Protection.IsChecked != false,
            string.IsNullOrWhiteSpace(hddFields[slot].Volume.Text) ? null : hddFields[slot].Volume.Text))
        .ToImmutableArray();

    public void Refresh()
    {
        floppyChoices.Clear(); hddChoices.Clear();
        var listed = new List<InlineMediaRow>();
        void Floppy(string handle, string name, FloppyConfiguration? configuration)
        {
            if (configuration is null || floppyChoices.Values.Any(c => c.Path == configuration.Path
                && c.Descriptor == configuration.Descriptor && c.Writeback == configuration.Writeback)) return;
            floppyChoices[handle] = configuration;
            var row = Describe(handle, "disk", name, configuration.Path, configuration.Descriptor.ToDescriptor(),
                configuration.Writeback ? "explicit writeback requested; admission remains unavailable" : "read-only draft; writeback off");
            listed.Add(configuration.Writeback && row.Selectable
                ? row with { Status = InlineMediaStatus.Refused, Reason = "Prestart writable admission is unavailable; explicit writeback choice retained, never silently disabled." } : row);
        }
        Floppy("configured:A", "Configured A: " + Path.GetFileName(original.DiskA?.Path), original.DiskA);
        Floppy("configured:B", "Configured B: " + Path.GetFileName(original.DiskB?.Path), original.DiskB);
        const string bundled = "CPM22-HC.DSK";
        if (LaunchStartup.TryResolveDisk(bundled, configDirectories, baseDirectory, out var path, out _)
            || (assetRoot is not null && LaunchStartup.TryResolveDisk(Path.Combine(assetRoot, "disks", "cpm", bundled), [], baseDirectory, out path, out _)))
            Floppy("bundled:" + bundled, "Bundled CP/M: " + bundled, new(Path.GetFullPath(path)));
        else listed.Add(InlineMediaRow.Unavailable("disk", "Bundled CPM22-HC.DSK unavailable; use the existing library or advanced configured-path route below (or repair the installed disk assets)."));
        var hddDraft = HddDraft(canonicalPaths: false);
        foreach (var hdd in hddDraft)
        {
            var handle = "configured:hdd:" + hdd.Slot;
            hddChoices[handle] = hdd;
            listed.Add(Describe(handle, "hdd", "Configured HDD " + hdd.Slot + ": " + (hdd.Volume ?? Path.GetFileName(hdd.Path)), hdd.Path,
                hdd.Volume is null ? MediaImageDescriptor.HddRaw8MiB : null,
                (hdd.Volume is null ? "8 MiB raw" : "HCVD named volume; validated at Start") + (hdd.WriteProtected ? " · read-only" : " · guest writable, no host writeback")));
        }
        foreach (var row in InlineMediaListing.Floppies(library?.Entries))
        {
            if (row.Path is null) { if (library is not null) listed.Add(row); continue; }
            if (floppyChoices.Values.Any(c => c.Path == row.Path)) continue;
            floppyChoices[row.Handle] = new(row.Path);
            listed.Add(row with { Detail = row.Detail + " · protected library source; read-only draft" });
        }
        if (hddDraft.IsEmpty)
            listed.Add(InlineMediaRow.Unavailable("hdd", "No HDD media selected; enabling HDD on an empty draft selects bundled D:/E:/F: if installed. Otherwise repair the installed assets or configure existing 8 MiB raw inputs below. Empty-controller Start is not usable-disk proof."));
        if (library is null || library.Entries.Count == 0)
            listed.Add(InlineMediaRow.Unavailable("disk", library is null ? catalogDiagnostic : "Disk library is empty; use the bundled disk or load an existing catalog / configure a local path below."));
        if (listed.Count > InlineMediaListing.MaxRowsPerPage)
        {
            int remaining = listed.Count - InlineMediaListing.MaxRowsPerPage;
            listed = listed.Take(InlineMediaListing.MaxRowsPerPage).ToList();
            listed.Add(InlineMediaRow.Unavailable("disk", $"{remaining} more rows not listed; narrow the existing catalog (page limit {InlineMediaListing.MaxRowsPerPage})."));
        }
        Listed = listed.ToImmutableArray(); Render();
    }

    private static InlineMediaRow Describe(string handle, string kind, string name, string path, MediaImageDescriptor? descriptor, string detail)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Configured media requires an absolute local path.");
            var info = new FileInfo(path);
            if (!info.Exists) return new(handle, kind, name, path, 0, detail, InlineMediaStatus.Unavailable, "Configured media file is missing.");
            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Media must be a regular local file, not a link.");
            if (OperatingSystem.IsLinux())
            {
                var facts = LinuxFileFacts.Inspect(path);
                if (!facts.Known || !facts.Regular || facts.Link) throw new IOException("Media regular-file identity is unavailable.");
            }
            descriptor?.ValidateLength(info.Length);
            var geometry = descriptor?.Geometry is { } g ? $" {g.Cylinders}×{g.Sides}×{g.Sectors}×{g.SectorSize}" : "";
            return new(handle, kind, name, path, info.Length, $"{descriptor?.Kind.ToString() ?? "HCVD"}{geometry} · {info.Length} B · {detail} · {path}", InlineMediaStatus.Ready);
        }
        catch (Exception ex) when (Expected(ex)) { return new(handle, kind, name, path, 0, detail, InlineMediaStatus.Refused, ex.Message); }
    }

    private void Render()
    {
        rows.Children.Clear();
        foreach (var row in Listed)
        {
            var item = new StackPanel { Spacing = 3 };
            item.Children.Add(new TextBlock { Text = MediaPanel.RowText(row), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (row.Kind == "disk" && row.Selectable)
                foreach (var slot in new[] { DiskSlot.A, DiskSlot.B })
                {
                    var button = new Button { Content = $"Use for {slot}:", IsEnabled = !selecting };
                    button.Click += async (_, _) => await SelectFloppyAsync(row, slot);
                    actions.Children.Add(button);
                }
            if (row.Kind == "hdd" && row.Selectable)
                foreach (var slot in new[] { "C", "D", "E", "F" })
                {
                    var button = new Button { Content = $"Use for {slot}:", IsEnabled = hddEnabled && !selecting };
                    button.Click += (_, _) => SelectHdd(row, slot);
                    actions.Children.Add(button);
                }
            item.Children.Add(actions); rows.Children.Add(item);
        }
    }

    public async Task<bool> SelectFloppyAsync(InlineMediaRow row, DiskSlot slot)
    {
        if (selecting) return false;
        try
        {
            selecting = true; Render();
            int revision = selectionRevision;
            string? previousText = slot == DiskSlot.A ? diskA.Text : diskB.Text;
            if (!row.Selectable || !floppyChoices.TryGetValue(row.Handle, out var candidate) || candidate.Path != row.Path)
                throw new ArgumentException("Selection is no longer available; refresh the media list.");
            RevalidateLibrary(row);
            // An explicit existing writable choice cannot be silently disabled by choosing a row.
            var target = slot == DiskSlot.A ? selectedA : selectedB;
            candidate = candidate with { Writeback = candidate.Writeback || target?.Writeback == true };
            // Reuse prestart's regular-file, bounded-read and explicit native descriptor validation.
            await PrestartMedia.ReadAsync(new() { DiskA = candidate });
            if (selectionRevision != revision || previousText != (slot == DiskSlot.A ? diskA.Text : diskB.Text))
                throw new ArgumentException("Draft changed during validation; the pending selection was cancelled.");
            if (slot == DiskSlot.A) { selectedA = candidate; selectedRowA = row; diskA.Text = candidate.Path; }
            else { selectedB = candidate; selectedRowB = row; diskB.Text = candidate.Path; }
            status.Text = $"{slot}: {row.Name} selected read-only; draft only, validated again at Start.";
            return true;
        }
        catch (Exception ex) when (Expected(ex)) { status.Text = "Not selected: " + ex.Message; return false; }
        finally { selecting = false; Render(); }
    }

    private void RevalidateLibrary(InlineMediaRow row)
    {
        if (!row.Handle.StartsWith("disk:", StringComparison.Ordinal)) return;
        var current = catalogPath is null ? library : LoadExistingCatalog(catalogPath);
        if (InlineMediaListing.RevalidateDiskHandle(current?.Entries, row.Handle, row.Path) is { } refusal)
            throw new IOException(refusal);
    }

    public bool SelectHdd(InlineMediaRow row, string slot)
    {
        try
        {
            if (!hddEnabled) throw new ArgumentException("Enable the supported HDD hardware configuration before choosing HDD media.");
            if (slot is not ("C" or "D" or "E" or "F") || !row.Selectable || !hddChoices.TryGetValue(row.Handle, out var candidate) || candidate.Path != row.Path)
                throw new ArgumentException("HDD selection is unavailable; refresh the media list.");
            if (!HddDraft(canonicalPaths: false).Contains(candidate)) throw new ArgumentException("HDD source draft changed; refresh the media list.");
            var fresh = Describe(row.Handle, row.Kind, row.Name, candidate.Path, candidate.Volume is null ? MediaImageDescriptor.HddRaw8MiB : null, "validated at Start");
            if (!fresh.Selectable) throw new IOException(fresh.Reason);
            hddFields[slot].Path.Text = candidate.Path;
            hddFields[slot].Volume.Text = candidate.Volume;
            hddFields[slot].Protection.IsChecked = candidate.WriteProtected;
            status.Text = $"{slot}: {row.Name} selected ({(candidate.WriteProtected ? "read-only" : "guest writable, no host writeback")}); cold Start must admit the image."; return true;
        }
        catch (Exception ex) when (Expected(ex)) { status.Text = "Not selected: " + ex.Message; return false; }
    }

    public void ClearHdd(string slot)
    {
        if (hddFields.TryGetValue(slot, out var field)) { field.Path.Text = ""; field.Volume.Text = ""; }
        status.Text = $"{slot}: draft attachment removed; running machines unchanged.";
    }

    public void Clear(DiskSlot slot)
    {
        selectionRevision++;
        if (slot == DiskSlot.A) { selectedA = null; selectedRowA = null; diskA.Text = ""; }
        else { selectedB = null; selectedRowB = null; diskB.Text = ""; }
    }

    /// <summary>Only media is projected; another machine's identity/preferences and hardware draft remain untouched.</summary>
    public MachineConfiguration ApplyTo(MachineConfiguration draft)
    {
        if (selecting) throw new ArgumentException("Media selection validation is still in progress.");
        if (selectedRowA is { } a && diskA.Text == a.Path) RevalidateLibrary(a);
        if (selectedRowB is { } b && diskB.Text == b.Path) RevalidateLibrary(b);
        FloppyConfiguration? Disk(TextBox field, FloppyConfiguration? selected)
        {
            if (string.IsNullOrWhiteSpace(field.Text)) return null;
            var path = Path.GetFullPath(field.Text);
            return path == selected?.Path ? selected : new(path, selected?.Writeback ?? false);
        }
        return draft with { DiskA = Disk(diskA, selectedA), DiskB = Disk(diskB, selectedB), Hdd = HddDraft() };
    }
    private static bool Expected(Exception ex) => ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException or DiskLibrarySchemaException or NotSupportedException;
}
