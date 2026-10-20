using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Hc2000.App.Configuration;
using Hc2000.App.Explorer;
using Hc2000.App.Library;
using Hc2000.App.Media;
using Hc2000.App.Workspace;
using Hc2000.HostFiles;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Storage;

namespace Hc2000.App;

public partial class MainWindow
{
    private bool twoPaneWired;

    private void BindExplorer(MachineSession session, MachineConfiguration? configuration)
    {
        var state = session.Explorer;
        if (state.Generation != session.RuntimeGeneration)
        {
            state.Generation = session.RuntimeGeneration;
            state.Request++;
            state.Image = null;
            state.Refresh = null;
            state.Listing = null;
            state.ListingTitle = null;
            state.Notice = "Choose Browse files on a drive or an existing disk-library row.";
        }
        state.MachineName = configuration?.Name ?? "HC2000";
        var media = this.FindControl<MediaPanel>("MediaControls")!;
        media.BrowseFiles = slot => BrowseExplorerDriveAsync(session, slot, configuration);
        media.Hdd.BrowseFiles = drive => BrowseExplorerHddAsync(session, drive, configuration);
        media.BrowseLibraryFiles = row => BrowseExplorerLibraryAsync(session, media, row);
        if (!twoPaneWired)
        {
            twoPaneWired = true;
            media.TwoPane.Scan.Click += async (_, _) => await ScanTwoPaneHomeAsync();
            media.TwoPane.Choose.Click += async (_, _) => await ChooseTwoPaneHomeAsync();
            media.TwoPane.NewFloppy.Click += (_, _) => media.TwoPane.BeginNewImage("floppy-720", "NEW720.DSK");
            media.TwoPane.NewHardDrive.Click += (_, _) => media.TwoPane.BeginNewImage("hdd-volume-8m", "NEW8M.HDD");
            media.TwoPane.NewTape.Click += (_, _) => media.TwoPane.BeginNewImage("tape-blank", "NEW.TAP");
            media.TwoPane.CreateImage.Click += (_, _) =>
            {
                if (media.TwoPane.NewImageKind is { } kind)
                    CreateNewImage(kind, media.TwoPane.NewImageName.Text ?? "");
            };
            media.TwoPane.OpenCopy.Click += async (_, _) =>
            {
                var current = this.FindControl<MediaPanel>("MediaControls")!;
                if (current.TwoPane.Rows.SelectedItem is ExplorerCopyRow row) await OpenTwoPaneCopyAsync(row);
            };
        }
        RenderExplorer(session);
    }

    /// <summary>Creates a new image in the matching media home through the shared writer
    /// (Hc2000.App.Media.MediaHomeNewImage -> MediaBlanks + MediaHomeCreate): the disk actions in the
    /// scanned home (override, else the configured disk home), the tape in the configured tape home.
    /// The new file is never registered; the next scan lists it. The two-pane Notice shows the
    /// outcome; a refusal leaves the name prompt open for correction.</summary>
    private void CreateNewImage(string kind, string name)
    {
        var settings = configurationStore.Load().Settings;
        var media = this.FindControl<MediaPanel>("MediaControls")!;
        var home = kind == "tape-blank" ? settings.TapeHome
            : media.TwoPaneHomeOverride;
        if (kind != "tape-blank" && string.IsNullOrWhiteSpace(home)) home = settings.DiskHome;
        var result = MediaHomeNewImage.NewImage(kind, home, name);
        if (result.Created)
        {
            media.TwoPane.CancelNewImage();
            media.TwoPane.Notice.Text = "Created " + result.Path + " (not registered; the next scan lists it).";
        }
        else media.TwoPane.Notice.Text = "New image refused: " + result.Refusal;
    }

    /// <summary>Scans the disk home (override, else the configured setting) into the two-pane list.
    /// Reads only; the scanned path is never a write target.</summary>
    internal async Task ScanTwoPaneHomeAsync()
    {
        var session = CurrentSession;
        if (session is null || !ExplorerCurrent(session)) return;
        var media = this.FindControl<MediaPanel>("MediaControls")!;
        var home = media.TwoPaneHomeOverride;
        if (string.IsNullOrWhiteSpace(home)) home = configurationStore.Load().Settings.DiskHome;
        var result = await Task.Run(() => TwoPaneCopyListing.Scan(home));
        if (!ExplorerCurrent(session)) return;
        media.TwoPane.Bind(result.Rows, session.Explorer.Image?.Path, result.Notice);
        session.Explorer.Notice = "Disk home: " + result.Notice;
        RenderExplorer(session);
    }

    /// <summary>Picks a disk-home folder and rescans it. The choice is an override for this window only.</summary>
    internal async Task ChooseTwoPaneHomeAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose disk home" });
        var chosen = false;
        try
        {
            if (folders.Count == 0) return;
            var path = folders[0].TryGetLocalPath();
            if (path is null) return;
            this.FindControl<MediaPanel>("MediaControls")!.TwoPaneHomeOverride = path;
            chosen = true;
        }
        finally { foreach (var folder in folders) folder.Dispose(); }
        if (chosen) await ScanTwoPaneHomeAsync();
    }

    /// <summary>Opens one scanned floppy row through the existing library lane: RegisterAsync, then the
    /// managed working copy, then the existing explorer reader. The scanned path is the original and is
    /// never browsed or written; HDD rows are refused (hc2000-disk-explorer-hdd-copy-lane).</summary>
    internal async Task OpenTwoPaneCopyAsync(ExplorerCopyRow row)
    {
        var session = CurrentSession;
        if (session is null || !ExplorerCurrent(session)) return;
        var state = session.Explorer;
        if (row.Kind != "floppy-dsk")
        {
            state.Notice = "Open refused: only a floppy DSK opens a managed copy.";
            RenderExplorer(session);
            return;
        }
        var catalog = this.FindControl<MediaPanel>("MediaControls")!.DiskLibrary;
        if (catalog is null)
        {
            state.Notice = "Disk library catalog is not configured; refusing to open.";
            RenderExplorer(session);
            return;
        }
        if (catalog.AdmissionMissing)
        {
            state.Notice = "Open refused: no host admission was provided; inject the host admission snapshot before registering.";
            RenderExplorer(session);
            return;
        }
        try
        {
            var full = Path.GetFullPath(row.Path);
            var entry = catalog.Entries.FirstOrDefault(e => string.Equals(Path.GetFullPath(e.SourcePath), full, StringComparison.Ordinal));
            if (entry is null)
            {
                var registered = await catalog.RegisterAsync(full, "media-home-scan");
                if (registered.Reason != DiskLibraryRefusal.Accepted || registered.Entry is null)
                {
                    state.Notice = "Open refused: " + registered.Message;
                    RenderExplorer(session);
                    return;
                }
                entry = registered.Entry;
            }
            if (!ExplorerCurrent(session)) return;
            var copy = catalog.Copies.FirstOrDefault(c => c.EntryId == entry.Id);
            if (copy is null)
            {
                var root = Path.GetDirectoryName(catalog.CatalogPath)
                    ?? throw new InvalidOperationException("catalog root unknown");
                var created = await catalog.CreateWorkingCopyAsync(entry.Id, root);
                if (created.Status != DiskLibraryCopyStatus.Created || created.Copy is null)
                {
                    state.Notice = "Open refused: working copy not created (" + created.Message + ").";
                    RenderExplorer(session);
                    return;
                }
                copy = created.Copy;
            }
            if (!ExplorerCurrent(session)) return;
            var root2 = Path.GetDirectoryName(catalog.CatalogPath)!;
            var copyPath = Path.GetFullPath(Path.Combine(root2, copy.RelativePath));
            if (string.Equals(copyPath, full, StringComparison.Ordinal))
                throw new InvalidOperationException("managed copy resolved to the scanned path");
            if (!File.Exists(copyPath))
            {
                state.Notice = "Open refused: managed copy file is missing.";
                RenderExplorer(session);
                return;
            }
            var opened = copyPath;
            await BrowseExplorerImageAsync(session, "Managed copy of " + row.Name, opened, MediaImageDescriptor.DskContainer,
                () => Task.FromResult(ReadExplorerImage(opened, MediaImageDescriptor.DskContainer)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            if (ExplorerCurrent(session)) { state.Notice = "Open refused: " + ex.Message; RenderExplorer(session); }
        }
    }

    private void RenderExplorer(MachineSession session)
    {
        if (!ReferenceEquals(CurrentSession, session) || session.Closed || closing) return;
        this.FindControl<MediaPanel>("MediaControls")!.Explorer.Bind(session.Explorer,
            image => PickExplorerImportAsync(session, image), (image, entry) => PickExplorerExportAsync(session, image, entry));
    }

    private bool ExplorerCurrent(MachineSession session, ExplorerImage? image = null)
        => !closing && !session.Closed && ReferenceEquals(CurrentSession, session)
            && session.Explorer.Generation == session.RuntimeGeneration
            && (image is null || ReferenceEquals(session.Explorer.Image, image));

    // Production browse composition seam: a frozen reader belongs to one session, not the shared view.
    internal async Task BrowseExplorerImageAsync(MachineSession session, string label, string? path,
        MediaImageDescriptor descriptor, Func<Task<ImmutableArray<byte>>> read)
    {
        if (!ExplorerCurrent(session)) return;
        var state = session.Explorer;
        var generation = session.RuntimeGeneration;
        var request = ++state.Request;
        state.Image = null;
        state.Listing = null;
        state.ListingTitle = null;
        state.Notice = "Reading " + label;
        state.Refresh = () => BrowseExplorerImageAsync(session, label, path, descriptor, read);
        RenderExplorer(session);
        try
        {
            var bytes = await read();
            if (session.Closed || session.RuntimeGeneration != generation || state.Request != request) return;
            state.Image = ExplorerImage.Parse(label, path, descriptor, bytes);
            state.Notice = $"{state.Image.Entries.Count} files / refused entries. Copy-out never overwrites an existing host path.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            if (session.Closed || session.RuntimeGeneration != generation || state.Request != request) return;
            state.Notice = "Explorer refused: " + ex.Message;
        }
        RenderExplorer(session);
    }

    internal Task BrowseExplorerDriveAsync(MachineSession session, DiskSlot slot, MachineConfiguration? configuration)
    {
        if (!ExplorerCurrent(session)) return Task.CompletedTask;
        if (session.Worker?.Completion.IsCompleted == false && session.MediaBridge is { } bridge)
            return BrowseExplorerImageAsync(session, $"{slot}: live boundary snapshot (read-only)", null,
                MediaImageDescriptor.DskContainer, async () =>
                {
                    var reply = await bridge.RequestAsync(new(MediaAction.ExportDisk, slot));
                    if (reply.Error is not null) throw new InvalidOperationException(reply.Error);
                    return reply.Export;
                });
        var configured = slot == DiskSlot.A ? configuration?.DiskA : configuration?.DiskB;
        if (session.HasRuntime || session.ReplacingRuntime || session.StartingConfiguration is not null)
            return ExplorerRefusalAsync(session, "Runtime media is unavailable; refusing to substitute the host image for live bytes.");
        if (configured is null) return ExplorerRefusalAsync(session, $"{slot}: no configured image or live drive.");
        var descriptor = configured.Descriptor.ToDescriptor();
        return BrowseExplorerImageAsync(session, $"{slot}: {configured.Path}", configured.Path, descriptor,
            () => Task.FromResult(ReadExplorerImage(configured.Path, descriptor)));
    }

    private Task BrowseExplorerLibraryAsync(MachineSession session, MediaPanel media, InlineMediaRow row)
    {
        if (!ExplorerCurrent(session)) return Task.CompletedTask;
        if (row.Path is not { } path || InlineMediaListing.RevalidateDiskHandle(media.DiskLibrary?.Entries, row.Handle, path) is { })
            return ExplorerRefusalAsync(session, "Disk library selection changed; refresh the inline list.");
        return BrowseExplorerImageAsync(session, row.Name + " · " + path, path, MediaImageDescriptor.DskContainer,
            () => Task.FromResult(ReadExplorerImage(path, MediaImageDescriptor.DskContainer)));
    }

    internal Task BrowseExplorerHddAsync(MachineSession session, byte drive, MachineConfiguration? configuration)
    {
        if (!ExplorerCurrent(session)) return Task.CompletedTask;
        if (drive is < 2 or > 5) return ExplorerRefusalAsync(session, "HDD drives are C:–F:.");
        if (session.Worker?.Completion.IsCompleted == false && session.MediaBridge is { } bridge)
            return BrowseExplorerImageAsync(session, $"{(char)('A' + drive)}: live boundary snapshot (read-only)", null,
                MediaImageDescriptor.HddRaw8MiB, async () =>
                {
                    var reply = await bridge.RequestAsync(new(MediaAction.Hdd, Hdd: new(HddAction.CaptureSave, drive)));
                    var hdd = reply.Hdd ?? throw new InvalidOperationException(reply.Error ?? "HDD profile unavailable.");
                    if (hdd.Error is not null) throw new InvalidOperationException(hdd.Error);
                    var capture = hdd.Save ?? throw new InvalidOperationException("HDD capture unavailable.");
                    // Browsing is not a save acknowledgement: cancel only the captured token; retain dirty bytes.
                    var cancelled = await bridge.RequestAsync(new(MediaAction.Hdd, Hdd: new(HddAction.CancelSave, drive, capture.Id)));
                    if (cancelled.Error is not null || cancelled.Hdd?.Error is not null)
                        throw new InvalidOperationException(cancelled.Error ?? cancelled.Hdd!.Error);
                    return capture.Bytes;
                });
        if (session.HasRuntime || session.ReplacingRuntime || session.StartingConfiguration is not null)
            return ExplorerRefusalAsync(session, "Runtime HDD media is unavailable; refusing to substitute a host file.");
        var configured = configuration?.Hdd.FirstOrDefault(h => h.Slot == ((char)('A' + drive)).ToString());
        if (configured is null) return ExplorerRefusalAsync(session, $"{(char)('A' + drive)}: no configured image or live drive.");
        return BrowseExplorerImageAsync(session, $"{configured.Slot}: {configured.Path}", configured.Path, MediaImageDescriptor.HddRaw8MiB,
            () => Task.FromResult(ReadExplorerImage(configured.Path, MediaImageDescriptor.HddRaw8MiB)));
    }

    // Production double-click seam for a scanned media home row (contract section 3): the kind
    // comes from the scan row (MediaHomeRow), never a suffix or length guess. A floppy-dsk and an
    // hdd-raw row open through the current explorer composition - the same cold-read path the
    // drive and library rows use. A tape and an hdd-container row browse through the existing
    // readers (Tap.List, the footer/index container read) and the pane shows that listing
    // read-only - the disk file-view composition with copy in/out is for disks and volumes.
    // A snapshot row is not a CP/M browse; an unknown row has no browse action. Browsing
    // writes nothing.
    internal Task BrowseMediaHomeRowAsync(MachineSession session, MediaHomeRow row)
    {
        if (!ExplorerCurrent(session)) return Task.CompletedTask;
        var label = "Scanned row: " + row.Name;
        switch (MediaHomeBrowseLane.ParseKind(row.Kind))
        {
            case MediaHomeRowKind.FloppyDsk:
                return BrowseExplorerImageAsync(session, label, row.Path, MediaImageDescriptor.DskContainer,
                    () => Task.FromResult(ReadExplorerImage(row.Path, MediaImageDescriptor.DskContainer)));
            case MediaHomeRowKind.HddRaw:
                return BrowseExplorerImageAsync(session, label, row.Path, MediaImageDescriptor.HddRaw8MiB,
                    () => Task.FromResult(ReadExplorerImage(row.Path, MediaImageDescriptor.HddRaw8MiB)));
            case MediaHomeRowKind.Tape:
            case MediaHomeRowKind.HddContainer:
                return BrowseMediaHomeListingAsync(session, label, row);
            case MediaHomeRowKind.Snapshot:
                return ExplorerRefusalAsync(session, "a snapshot row is not a CP/M browse; the scan does not decode it");
            case MediaHomeRowKind.Unknown:
            default:
                return ExplorerRefusalAsync(session, "an unknown row has no browse action");
        }
    }

    private async Task BrowseMediaHomeListingAsync(MachineSession session, string label, MediaHomeRow row)
    {
        // The existing readers list the row: Tap.List for a tape, the existing footer/index
        // container read for an hdd-container. No new parser, no arena or payload read.
        var result = await new MediaHomeBrowseLane().OpenRowAsync(row);
        if (result.Outcome != MediaHomeBrowseOutcome.Browsable)
        {
            await ExplorerRefusalAsync(session, result.Message);
            return;
        }
        session.Explorer.Request++;
        session.Explorer.Image = null;
        session.Explorer.Refresh = null;
        session.Explorer.Notice = label + " \u2014 " + result.Message;
        session.Explorer.ListingTitle = label;
        session.Explorer.Listing = result.Tape is { } tape
            ? tape.Select(f => $"{f.Name} \u00b7 {f.Data.Length} bytes").ToList()
            : result.Container?.Entries.Select((e, i) =>
                $"volume {i + 1} \u00b7 slot {e.SlotOrdinal.Value} \u00b7 {e.Name} \u00b7 {e.ImageBytes:N0} bytes"
                + (e.ReadOnly ? " \u00b7 R/O" : "")).ToList() ?? new List<string>();
        RenderExplorer(session);
    }

    private Task ExplorerRefusalAsync(MachineSession session, string message)
    {
        session.Explorer.Request++;
        session.Explorer.Image = null;
        session.Explorer.Refresh = null;
        session.Explorer.Listing = null;
        session.Explorer.ListingTitle = null;
        session.Explorer.Notice = "Explorer refused: " + message;
        RenderExplorer(session);
        return Task.CompletedTask;
    }

    private static ImmutableArray<byte> ReadExplorerImage(string path, MediaImageDescriptor descriptor)
    {
        var acquired = NativeImageAdmission.TryAcquireColdRead(default);
        using var admission = acquired.Admission ?? throw new IOException($"Native image admission: {acquired.Refusal}.");
        using var source = admission.OpenColdSource(path);
        var facts = admission.ValidateOpenedSource(source, path);
        descriptor.ValidateLength(facts.Length);
        var bytes = new byte[checked((int)facts.Length)];
        source.ReadExactly(bytes);
        if (source.ReadByte() != -1) throw new InvalidDataException("Image changed size while reading.");
        admission.ValidateSourceStillCurrent(source, facts);
        return ImmutableArray.CreateRange(bytes);
    }

    internal bool CopyExplorerIn(MachineSession session, ExplorerImage image, string name, int user, bool replace, ImmutableArray<byte> data)
    {
        if (!ExplorerCurrent(session, image)) return false;
        try
        {
            if (!image.CanImport) throw new InvalidOperationException("Live snapshots and flat HDD volumes are read-only in the copy-in lane.");
            ExplorerImage? updated = null;
            var result = WriteExplorerImage(image.Path!, bytes =>
            {
                var disk = RawFloppyAdapter.Load(bytes.AsSpan(), image.Descriptor, new DriveProfile(80, 2));
                new ExplorerCopyIn(CpmFs.Open(disk)).CopyIn(name, data.AsSpan(), user, replace);
                var output = ImmutableArray.CreateRange(RawFloppyAdapter.Save(disk, image.Descriptor, new DriveProfile(80, 2)));
                updated = ExplorerImage.Parse(image.Label, image.Path, image.Descriptor, output);
                return output;
            });
            session.Explorer.Notice = result.Message;
            if (result.Published) session.Explorer.Image = updated;
            RenderExplorer(session);
            return result.Published;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            session.Explorer.Notice = "Copy-in refused: " + ex.Message;
            RenderExplorer(session);
            return false;
        }
    }

    /// <summary>Flat 8 MiB volume lane (amendment 1): one explicit one-file copy-in onto the file
    /// the user opened. Volume-shaped writer: CpmFs.OpenHddVolume + volume bytes export beside the
    /// §4 formatter sibling; published only by WriteExplorerImage under the unchanged fence.</summary>
    internal bool CopyExplorerVolumeIn(MachineSession session, ExplorerImage image, string name, int user, bool replace, ImmutableArray<byte> data)
    {
        if (!ExplorerCurrent(session, image)) return false;
        try
        {
            if (!image.CanImportVolume) throw new InvalidOperationException("Only an opened flat 8 MiB volume can take a volume copy-in.");
            ExplorerImage? updated = null;
            var result = WriteExplorerImage(image.Path!, bytes =>
            {
                var fs = CpmFs.OpenHddVolume(bytes.AsSpan());
                new ExplorerCopyIn(fs).CopyIn(name, data.AsSpan(), user, replace);
                var output = ImmutableArray.CreateRange(fs.ExportVolumeBytes());
                updated = ExplorerImage.Parse(image.Label, image.Path, image.Descriptor, output);
                return output;
            });
            session.Explorer.Notice = result.Message;
            if (result.Published) session.Explorer.Image = updated;
            RenderExplorer(session);
            return result.Published;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            session.Explorer.Notice = "Copy-in refused: " + ex.Message;
            RenderExplorer(session);
            return false;
        }
    }

    internal CpmExportMapping? CopyExplorerOut(MachineSession session, ExplorerImage image, CpmEntry entry, string target)
    {
        if (!ExplorerCurrent(session, image)) return null;
        try
        {
            if (entry.Refused || !image.Entries.Contains(entry)) throw new InvalidOperationException("Refused or stale directory entry.");
            var mapping = ExplorerCopyOutHost.CopyOut(image.Open(), entry.Name, entry.User, target);
            session.Explorer.Notice = $"user {mapping.User} · {mapping.CpmName} → {mapping.HostPath} · {mapping.Records} records / {mapping.Bytes} bytes (untrimmed).";
            RenderExplorer(session);
            return mapping;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            session.Explorer.Notice = "Copy-out refused: " + ex.Message;
            RenderExplorer(session);
            return null;
        }
    }

    private async Task PickExplorerImportAsync(MachineSession session, ExplorerImage image)
    {
        if (!ExplorerCurrent(session, image)) return;
        var name = session.Explorer.Name;
        int user = session.Explorer.User;
        bool replace = session.Explorer.Replace;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Copy host file into unmounted CP/M image", AllowMultiple = false });
            try
            {
                if (files.Count == 0 || !ExplorerCurrent(session, image)) return;
                var path = files[0].TryGetLocalPath() ?? throw new InvalidOperationException("Local files only.");
                var bytes = await MediaFiles.ReadAsync(path, MediaFiles.MaxDiskBytes);
                if (image.Descriptor.Kind == MediaImageKind.HddRaw8MiB)
                    CopyExplorerVolumeIn(session, image, name, user, replace, bytes);
                else
                    CopyExplorerIn(session, image, name, user, replace, bytes);
            }
            finally { foreach (var file in files) file.Dispose(); }
        }
        catch (Exception ex)
        {
            if (ExplorerCurrent(session, image)) { session.Explorer.Notice = "Copy-in refused: " + ex.Message; RenderExplorer(session); }
        }
    }

    private async Task PickExplorerExportAsync(MachineSession session, ExplorerImage image, CpmEntry entry)
    {
        if (!ExplorerCurrent(session, image) || entry.Refused) return;
        try
        {
            using var file = await StorageProvider.SaveFilePickerAsync(new()
            {
                Title = $"Copy out user {entry.User} {entry.Name} (no host overwrite)", SuggestedFileName = ExplorerCopyOut.HostName(entry.Name)
            });
            if (file is not null && ExplorerCurrent(session, image))
                CopyExplorerOut(session, image, entry, file.TryGetLocalPath() ?? throw new InvalidOperationException("Local output only."));
        }
        catch (Exception ex)
        {
            if (ExplorerCurrent(session, image)) { session.Explorer.Notice = "Copy-out refused: " + ex.Message; RenderExplorer(session); }
        }
    }
}
