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
    /// <summary>Shown when the draft names C:-F: images but the chosen machine has no hard disk controller: the images
    /// are not attached or saved with this machine, and the user may remove them here instead of meeting a refusal at Start.</summary>
    private readonly Border hddBanner = new() { Name = "HddNotAttachedBanner", IsVisible = false, Classes = { "blockingBanner" }, Padding = new Avalonia.Thickness(8) };
    private readonly Dictionary<string, FloppyConfiguration> floppyChoices = new();
    private readonly Dictionary<string, HddConfiguration> hddChoices = new();
    private DiskLibraryCatalog? library;
    private string? catalogPath;
    private readonly string hddCatalogPath;
    private string catalogDiagnostic = "Disk library not configured; load an existing catalog or configure local paths below.";
    private bool hddEnabled, selecting, factoryDefaultsConsidered;
    private string? selectedProfile;
    private int selectionRevision;
    private InlineMediaRow? selectedRowA, selectedRowB;
    public ImmutableArray<InlineMediaRow> Listed { get; private set; } = [];
    public string? Status => status.Text;
    private Func<InlineMediaRow, string, Task<string>>? libraryHddAction;
    private bool librarySelecting;
    public Func<InlineMediaRow, string, Task<string>>? LibraryHddAction
    {
        get => libraryHddAction;
        set { libraryHddAction = value; Render(); }
    }

    public void ApplyLibraryHdd(ImmutableArray<HddConfiguration> selections)
    {
        foreach (var (slot, field) in hddFields)
        {
            var selected = selections.SingleOrDefault(h => h.Slot == slot);
            field.Path.Text = selected?.Path ?? "";
            field.Volume.Text = selected?.Volume ?? "";
            field.Protection.IsChecked = selected?.WriteProtected ?? true;
        }
        Refresh();
    }

    public async Task SelectLibraryHddAsync(InlineMediaRow row, string slot)
    {
        if (librarySelecting || !hddEnabled || LibraryHddAction is not { } action) return;
        librarySelecting = true;
        try { status.Text = await action(row, slot); }
        catch (Exception ex) when (Expected(ex)) { status.Text = "Library HDD selection refused: " + ex.Message; }
        finally { librarySelecting = false; Render(); }
    }

    public StartupMediaPanel(MachineConfiguration original, SettingsStore store, DiskLibraryCatalog? diskLibrary = null,
        string? assetRoot = null, IReadOnlyList<string>? configDirectories = null, string? baseDirectory = null)
    {
        this.original = original;
        this.selectedProfile = original.Profile;
        this.assetRoot = assetRoot;
        var settingsDirectory = Path.GetDirectoryName(Path.GetFullPath(store.FilePath))!;
        hddCatalogPath = Path.Combine(settingsDirectory, "hdd-library", "catalog.json");
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
        var bannerText = new StackPanel { Spacing = 6 };
        bannerText.Children.Add(new TextBlock
        {
            Text = "This machine has no hard-disk controller: the C: to F: disks you chose won't be used with it.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var removeHdd = new Button { Name = "RemoveHddFromDraft", Content = "Remove them", Classes = { "secondaryAction" } };
        removeHdd.Click += (_, _) => { foreach (var slot in hddFields.Keys) ClearHdd(slot); };
        bannerText.Children.Add(removeHdd);
        hddBanner.Child = bannerText;
        form.Children.Add(hddBanner);
        form.Children.Add(new TextBlock
        {
            Text = "To search or browse CP/M floppy images without starting, click Open Explorer workspace above.",
            Classes = { "caption" },
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        form.Children.Add(new TextBlock { Text = "Available media — the choices take effect when you start", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
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
        diskA = PathField(advanced, "Floppy drive A (image path)", "PrestartDiskA", original.DiskA?.Path);
        diskB = PathField(advanced, "Floppy drive B (image path)", "PrestartDiskB", original.DiskB?.Path);
        advanced.Children.Add(new TextBlock { Text = "Turning on the hard disk picks C.DSK, D.DSK, E.DSK and F.DSK from one bundled drive container. The machine works on private copies, so your disk files are never changed; C: contains utilities. CP/M boot is not included. Drives given as plain 8 MiB files use a blank name; named drives all come from one container and cannot be mixed with floppy A or B disks.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        foreach (var slot in new[] { "C", "D", "E", "F" })
        {
            var hdd = original.Hdd.FirstOrDefault(h => h.Slot == slot);
            var field = PathField(advanced, "Hard drive " + slot + " (image path)", "PrestartHdd" + slot, hdd?.Path);
            var volume = PathField(advanced, "Hard drive " + slot + " volume (HCVD name; blank for raw 8 MiB image)", "PrestartHdd" + slot + "Volume", hdd?.Volume);
            var protection = new CheckBox { Name = "PrestartHdd" + slot + "Protected", Content = new TextBlock { Text = slot + ": write-protected (guest writes never grant host writeback)", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, IsChecked = hdd?.WriteProtected ?? true };
            field.IsEnabled = false; protection.IsEnabled = false; volume.IsEnabled = false;
            advanced.Children.Add(protection); hddFields[slot] = (field, protection, volume);
            field.TextChanged += (_, _) => UpdateHddBanner();
        }
        // Keep the existing path controls in the logical tree (automation/settings compatibility).
        form.Children.Add(new Expander { Header = new TextBlock { Text = "Existing library / advanced media configuration", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, Content = advanced });
        var clear = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var slot in new[] { DiskSlot.A, DiskSlot.B })
        {
            var button = new Button { Content = $"Clear {slot}:", Margin = new Avalonia.Thickness(0, 0, 6, 6) };
            button.Click += (_, _) => Clear(slot);
            clear.Children.Add(button);
        }
        foreach (var slot in new[] { "C", "D", "E", "F" })
        {
            var button = new Button { Content = $"Clear {slot}:", Margin = new Avalonia.Thickness(0, 0, 6, 6) };
            button.Click += (_, _) => ClearHdd(slot);
            clear.Children.Add(button);
        }
        form.Children.Add(clear); form.Children.Add(status); Content = form;
        Refresh();
        UpdateHddBanner();
    }

    private static TextBox PathField(StackPanel parent, string label, string name, string? value)
    {
        parent.Children.Add(new TextBlock { Text = label, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
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
    public void SetSelectedProfile(string? profile)
    {
        var before = selectedProfile;
        selectedProfile = profile;
        if (!hddEnabled) return;
        if (!factoryDefaultsConsidered)
        {
            SetHddEnabled(true, profile);
            return;
        }
        // Moving between machines with different default disks swaps the defaults, never a selection of the user's own.
        if (profile != before && SelectedDefault() is { } current && current != DefaultFor(profile))
        {
            ApplyDefault(profile);
            Refresh();
        }
    }

    public void SetHddEnabled(bool enabled, string? profile = null)
    {
        if (profile is not null) selectedProfile = profile;
        hddEnabled = enabled;
        if (enabled && !factoryDefaultsConsidered)
        {
            factoryDefaultsConsidered = true;
            // Never replace an existing selection, a typed draft, or a deliberate
            // clear when hardware is toggled again. Defaults are all-or-nothing.
            if (original.Hdd.IsEmpty && hddFields.Values.All(field => string.IsNullOrWhiteSpace(field.Path.Text)))
                ApplyDefault(selectedProfile ?? original.Profile);
            Refresh();
        }
        foreach (var field in hddFields.Values) { field.Path.IsEnabled = enabled; field.Protection.IsEnabled = enabled; field.Volume.IsEnabled = enabled; }
        UpdateHddBanner();
        Render();
    }

    // The default disks of each HDD machine: the C:-boot machine its bootable C: alone, the full workstation
    // (hc2000-full-workstation-default) C:-F: of its own container, every other one C:-F: of the factory container.
    private static string DefaultFor(string? profile) => profile switch
    {
        "hc2000-v2-hdd-cboot-v1" => HddFactoryImages.CBootVolumeName,
        "hc2000-v2-full-workstation-v1" => HddFactoryImages.WorkstationContainerName,
        _ => HddFactoryImages.ContainerName
    };

    private void ApplyDefault(string? profile)
    {
        var name = DefaultFor(profile);
        if (name == HddFactoryImages.CBootVolumeName) ApplyCBootDefault();
        else ApplyContainerDefaults(name);
    }

    /// <summary>Which default the draft's C: holds, or null for a selection of the user's own.</summary>
    private string? SelectedDefault() =>
        new[] { HddFactoryImages.ContainerName, HddFactoryImages.WorkstationContainerName }.FirstOrDefault(IsContainerDefaultSelected)
            ?? (IsCBootDefaultSelected() ? HddFactoryImages.CBootVolumeName : null);

    private bool IsContainerDefaultSelected(string containerName)
    {
        if (!LaunchStartup.TryResolveDisk(containerName, [], baseDirectory, out var path, out _)
            && !LaunchStartup.TryResolveDisk(containerName, configDirectories, baseDirectory, out path, out _))
            return false;
        var full = Path.GetFullPath(path);
        return hddFields.TryGetValue("C", out var c) && c.Path.Text == full && c.Volume.Text == "C.DSK";
    }

    private bool IsCBootDefaultSelected()
    {
        if (!LaunchStartup.TryResolveDisk(HddFactoryImages.CBootVolumeName, [], baseDirectory, out var path, out _)
            && !LaunchStartup.TryResolveDisk(HddFactoryImages.CBootVolumeName, configDirectories, baseDirectory, out path, out _))
            return false;
        var full = Path.GetFullPath(path);
        return hddFields.TryGetValue("C", out var c) && c.Path.Text == full && string.IsNullOrWhiteSpace(c.Volume.Text);
    }

    private void ApplyCBootDefault()
    {
        if (LaunchStartup.TryResolveDisk(HddFactoryImages.CBootVolumeName, [], baseDirectory, out var cbootPath, out _)
            || LaunchStartup.TryResolveDisk(HddFactoryImages.CBootVolumeName, configDirectories, baseDirectory, out cbootPath, out _))
        {
            hddFields["C"].Path.Text = Path.GetFullPath(cbootPath);
            hddFields["C"].Volume.Text = "";
            hddFields["C"].Protection.IsChecked = false;
            foreach (var slot in new[] { "D", "E", "F" })
            {
                hddFields[slot].Path.Text = "";
                hddFields[slot].Volume.Text = "";
                hddFields[slot].Protection.IsChecked = false;
            }
        }
    }

    private void ApplyContainerDefaults(string containerName)
    {
        if (LaunchStartup.TryResolveDisk(containerName, [], baseDirectory, out var path, out _)
            || LaunchStartup.TryResolveDisk(containerName, configDirectories, baseDirectory, out path, out _))
        {
            foreach (var slot in new[] { "C", "D", "E", "F" })
            {
                hddFields[slot].Path.Text = Path.GetFullPath(path);
                hddFields[slot].Volume.Text = slot + ".DSK";
                hddFields[slot].Protection.IsChecked = false;
            }
        }
    }

    /// <summary>True when C:-F: images are named in the draft but the machine cannot attach them.</summary>
    public bool HddNotAttached => hddBanner.IsVisible;

    private void UpdateHddBanner() =>
        hddBanner.IsVisible = !hddEnabled && hddFields.Values.Any(field => !string.IsNullOrWhiteSpace(field.Path.Text));

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
        if (File.Exists(hddCatalogPath))
        {
            try
            {
                var hddLibrary = LoadExistingCatalog(hddCatalogPath);
                foreach (var entry in hddLibrary.Entries.Where(e => e.Kind == DiskLibraryKind.Hdd && e.Format == DiskLibraryFormat.Flat512))
                    listed.Add(new("library-hdd:" + entry.Id, "library-hdd", entry.Name, entry.SourcePath, entry.Length,
                        "flat512 library · saved working medium reused when unique; fresh admission on selection, original is provenance only", InlineMediaStatus.Ready));
            }
            catch (Exception ex) when (Expected(ex))
            { listed.Add(InlineMediaRow.Unavailable("library-hdd", "HDD library unavailable: " + ex.Message)); }
        }
        if (hddDraft.IsEmpty)
            listed.Add(InlineMediaRow.Unavailable("hdd", "No HDD media selected. Enabling HDD may select bundled drives when available; otherwise choose existing images below."));
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
                    if (LibraryHddAction is not null && hddChoices.TryGetValue(row.Handle, out var candidate) && candidate.Volume is null)
                    {
                        var libraryButton = new Button { Content = $"Library flat512 as {slot}:", IsEnabled = hddEnabled && !librarySelecting && !selecting };
                        libraryButton.Click += async (_, _) => await SelectLibraryHddAsync(row, slot);
                        actions.Children.Add(libraryButton);
                    }
                }
            if (row.Kind == "library-hdd" && row.Selectable && LibraryHddAction is not null)
                foreach (var slot in new[] { "C", "D", "E", "F" })
                {
                    var button = new Button { Content = $"Library flat512 as {slot}:", IsEnabled = hddEnabled && !librarySelecting && !selecting };
                    button.Click += async (_, _) => await SelectLibraryHddAsync(row, slot);
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
            status.Text = $"{slot}: {row.Name} selected read-only; it is checked again when you start.";
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
            status.Text = $"{slot}: {row.Name} selected ({(candidate.WriteProtected ? "read-only" : "changes stay in the machine until saved")}); checked when you start."; return true;
        }
        catch (Exception ex) when (Expected(ex)) { status.Text = "Not selected: " + ex.Message; return false; }
    }

    public void ClearHdd(string slot)
    {
        if (hddFields.TryGetValue(slot, out var field)) { field.Path.Text = ""; field.Volume.Text = ""; }
        status.Text = $"{slot}: removed from this setup; running machines unchanged.";
        UpdateHddBanner();
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
