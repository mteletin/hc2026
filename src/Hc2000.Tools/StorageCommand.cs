using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Hc2000.Machine.Storage;
using Hc2000.HostFiles;

namespace Hc2000.Tools;

/// <summary>Host I/O adapter. Libraries take bytes only; updates use same-directory atomic replacement.</summary>
public static class StorageCommand
{
    public static int Run(string command, string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            var options = new Options(args);
            if (options.Positional.Count < 2) throw new ArgumentException($"usage: hc {command} <operation> <image> [options]");
            switch (command.ToLowerInvariant())
            {
                case "hdd": return HardDisk(options, output, error);
                case "dsk": Disk(options, output); break;
                case "fs": Filesystem(options, output, error); break;
                case "tap": Tape(options, output); break;
                case "rom": return Rom(options, output);
                default: throw new ArgumentException("Unknown storage command.");
            }
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or InvalidOperationException)
        {
            error.WriteLine($"hc {command}: {ex.Message}"); return 1;
        }
    }
    // Spec 11: flat bytes only, no header, partition table or CP/M formatting.
    private static int HardDisk(Options o, TextWriter output, TextWriter error)
    {
        const int imageBytes = 8_388_608;
        if (o.Positional[0] == "replace-volume")
        {
            if (o.Positional.Count != 4)
                throw new ArgumentException("usage: hc hdd replace-volume <container> <volume-name> <raw-image>");
            o.Allow();
            return HddOfflineReplaceCommand.Run(o.Positional[1], o.Positional[2], o.Positional[3], output, error);
        }
        o.Count(2); o.Allow();
        var path = o.Positional[1];
        switch (o.Positional[0])
        {
            case "create":
                if (File.Exists(path) || Directory.Exists(path))
                    throw new IOException("Refusing to replace an existing path with hdd create.");
                // The final no-overwrite move also refuses a destination created during the write.
                AtomicWrite(path, new byte[imageBytes], false);
                break;
            case "identify":
                using (var image = File.OpenRead(path))
                {
                    if (image.Length != imageBytes)
                        throw new InvalidDataException($"Raw HDD image must be exactly {imageBytes} bytes; found {image.Length}.");
                    var hash = Convert.ToHexStringLower(SHA256.HashData(image));
                    if (image.Length != imageBytes || image.Position != imageBytes)
                        throw new InvalidDataException("Raw HDD image length changed during identification.");
                    output.WriteLine($"raw-hdd bytes=8388608 sectors=16384 sector-size=512 offset=0 sha256={hash}");
                }
                break;
            case "inspect":
                InspectHardDiskContainer(path, output);
                break;
            case "factory":
                FactoryHardDisks(path, output);
                break;
            default: throw new ArgumentException("hdd supports create/identify/inspect/factory/replace-volume; usage: hc hdd replace-volume <container> <volume-name> <raw-image> (local, unbound, caller-owned disposable container only).");
        }
        return 0;
    }
    private static void FactoryHardDisks(string directory, TextWriter output)
    {
        var paths = HddFactoryImages.GenerateInto(directory);
        foreach (var path in paths) InspectHardDiskContainer(path, output);
        output.Write(File.ReadAllText(Path.Combine(directory, HddFactoryImages.ReceiptName)));
    }
    // Spec 11 sibling: read-only inspection of a multivolume HDD container (footer + index only, per the accepted
    // shared catalog metadata). Reads exactly the 64-byte trailing footer and then the validated index; never arena
    // or payload bytes, never the whole image. The input is opened read-only and never mutated; no mount, selection,
    // replacement or repair. Identity is container ID/revision plus per-row name/slot ordinal (no per-volume GUID);
    // integrity is metadata-only (storedImageCrc is stored, not verified). No CAS/snapshot promise: a file changed
    // mid-read may refuse. The complete bounded report is validated, decoded and rendered before any line is emitted.
    private static void InspectHardDiskContainer(string path, TextWriter output)
    {
        // Fixed caller admission (no CLI knobs): 511 is the largest entry count whose arena+index+64-byte footer stays
        // within uint file lengths; the index and metadata maxima derive from that cap, not from separate options.
        var limits = new HddLimits(uint.MaxValue, 511, 12_276, 114_640);
        using var image = File.OpenRead(path);
        var length = image.Length;
        if (length < 76)
            throw new InvalidDataException($"HDD container image must be at least 76 bytes; found {length}.");
        var footer = new byte[64];
        image.Position = length - 64;
        image.ReadExactly(footer);
        var inspected = HddCatalogCodec.InspectHddFooter(footer, (ulong)length, limits);
        if (!inspected.Success)
            throw new InvalidDataException(inspected.Failure!.Detail);
        var view = inspected.Value;
        var index = new byte[view.IndexBytes];
        image.Position = (long)view.IndexStart;
        image.ReadExactly(index);
        var decoded = HddCatalogCodec.DecodeHdd(footer, index, (ulong)length, limits);
        if (!decoded.Success)
            throw new InvalidDataException(decoded.Failure!.Detail);
        if (image.Length != length)
            throw new InvalidDataException("HDD container length changed during inspection.");
        var invariant = CultureInfo.InvariantCulture;
        var entries = decoded.Value.Entries;
        var lines = new string[entries.Length + 1];
        lines[0] = "hdd-container version=1 id=" + Convert.ToHexStringLower(view.ContainerId)
            + " revision=" + view.Revision.ToString(invariant)
            + " entries=" + view.Entries.ToString(invariant)
            + " arenaBytes=" + view.ArenaBytes.ToString(invariant)
            + " indexBytes=" + view.IndexBytes.ToString(invariant)
            + " indexCrc=" + view.IndexCrc.ToString("x8", invariant)
            + " indexStart=" + view.IndexStart.ToString(invariant)
            + " indexEnd=" + view.IndexEnd.ToString(invariant)
            + " fileBytes=" + length.ToString(invariant)
            + " integrity=metadata-only";
        for (var row = 0; row < entries.Length; row++)
        {
            var entry = entries[row];
            var ordinal = entry.SlotOrdinal.Value;
            lines[row + 1] = "hdd-volume row=" + row.ToString(invariant)
                + " ordinal=" + ordinal.ToString(invariant)
                + " name=" + JsonSerializer.Serialize(entry.Name)
                + " protection=" + (entry.ReadOnly ? "ro" : "rw")
                + " offset=" + (ordinal * VolumeCatalogMetadata.SlotBytes).ToString(invariant)
                + " bytes=" + entry.ImageBytes.ToString(invariant)
                + " storedImageCrc=" + entry.ImageCrc.ToString("x8", invariant);
        }
        foreach (var line in lines) output.WriteLine(line);
    }
    private static DiskGeometry? Geometry(Options o)
    {
        if (!o.Values.TryGetValue("--geometry", out var value)) return null;
        var fields = value.Split(',');
        if (fields.Length != 4) throw new ArgumentException("--geometry requires cylinders,sides,sectors,sector-size.");
        var n = fields.Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var geometry = new DiskGeometry(n[0], n[1], n[2], n[3]); geometry.Validate(); return geometry;
    }
    private static Dsk Load(string path, Options o) => Dsk.Load(File.ReadAllBytes(path), Geometry(o));
    private static void Disk(Options o, TextWriter output)
    {
        var verb = o.Positional[0]; o.Count(2);
        if (verb == "info")
        {
            o.Allow("--geometry"); var disk = Load(o.Positional[1], o);
            output.WriteLine($"{disk.Format}: {disk.Cylinders} cylinders, {disk.Sides} sides");
            foreach (var track in disk.Tracks)
                output.WriteLine($"track {track.Cylinder}/{track.Side} gap={track.Gap:X2} filler={track.Filler:X2}: " +
                    string.Join(' ', track.Sectors.Select(s => $"{s.C}/{s.H}/{s.R}/{s.N}:{s.Data.Length}[{s.ST1:X2},{s.ST2:X2}]")));
        }
        else if (verb == "convert")
        {
            o.Allow("--geometry", "-o", "--container"); var disk = Load(o.Positional[1], o); var path = o.Required("-o");
            var format = o.Get("--container", Path.GetExtension(path).Equals(".img", StringComparison.OrdinalIgnoreCase) ? "raw" : "extended");
            AtomicWrite(path, disk.ToContainer(Container(format)));
        }
        else throw new ArgumentException("dsk supports info/convert.");
    }
    private static DiskFormat Container(string value) => value switch
    {
        "extended" => DiskFormat.Extended, "standard" => DiskFormat.Standard, "raw" => DiskFormat.Raw,
        _ => throw new ArgumentException("--container must be extended, standard or raw.")
    };
    private static void Filesystem(Options o, TextWriter output, TextWriter error)
    {
        var verb = o.Positional[0]; var path = o.Positional[1];
        var user = o.Number("--user", 0, 31);
        if (verb == "new")
        {
            o.Count(2); o.Allow("--format", "--geometry", "--container");
            if (File.Exists(path)) throw new IOException("Refusing to replace an existing image with fs new.");
            var format = o.Required("--format"); var geometry = Geometry(o);
            var profiles = Dpb.Presets().Where(p => (p.Name == format || format == (p.HcHeader ? "hcfs" : "cpm-hc")) && (geometry is null || p.Geometry == geometry)).ToArray();
            var profile = profiles.FirstOrDefault() ?? throw new ArgumentException("No preset matches requested format/geometry.");
            AtomicWrite(path, Dsk.Create(profile.Geometry, Container(o.Get("--container", "extended"))).Save(), false); return;
        }
        var disk = Load(path, o); var fs = CpmFs.Open(disk, o.Get("--format", "auto"));
        foreach (var warning in fs.Warnings) error.WriteLine("warning: " + warning);
        var hc = fs.Parameters.HcHeader ? new HcFs(fs) : null;
        switch (verb)
        {
            case "ls":
                o.Count(2); o.Allow("--format", "--geometry", "--user"); output.WriteLine("format " + fs.Parameters.Name);
                var listed = fs.List().Where(f => !o.Values.ContainsKey("--user") || f.User == user).ToArray();
                foreach (var collision in ExportCollisions(listed)) error.WriteLine("warning: " + collision);
                foreach (var file in listed)
                {
                    var inspection = hc?.Inspect(file); var h = inspection?.Header;
                    var metadata = h is null ? "" : $" type={h.Type} length={h.Length} start={h.Start} variables={h.Variables} autostart={h.Autostart}";
                    if (inspection?.IsUntyped == true) metadata += " untyped/headerless";
                    else if (inspection?.Diagnostic is not null) { error.WriteLine($"warning: {JsonSerializer.Serialize(file.Name)}: {inspection.Diagnostic}"); metadata += " invalid-header"; }
                    var presentedName = file.Name.TrimEnd(' ');
                    var displayName = CatalogName(presentedName);
                    if (displayName != presentedName || (presentedName.Length == 0 && file.Name.Length > 0)) metadata += " literal=" + JsonSerializer.Serialize(file.Name);
                    output.WriteLine($"{file.User}\t{displayName}\t{file.Records * 128}\tallocated={file.AllocatedBytes}\t{(file.ReadOnly ? 'R' : '-')}{(file.System ? 'S' : '-')}" + metadata);
                }
                break;
            case "get":
                o.Count(3); o.Allow("--format", "--geometry", "--user", "-o", "--tap");
                var name = o.Positional[2]; var bytes = hc?.Get(name, user) ?? fs.Get(name, user);
                if (o.Values.ContainsKey("--tap"))
                {
                    if (hc is null) throw new ArgumentException("--tap export requires HCFS metadata.");
                    var h = hc.Header(name, user); var tape = Tap.Load([]); tape.Put(FromHc(name, h, bytes)); bytes = tape.Save();
                }
                if (!o.Values.ContainsKey("-o"))
                {
                    var collisions = ExportCollisions(fs.List().Where(f => f.User == user && HostComparer.Equals(HostName(f.Name), HostName(name)))).ToArray();
                    if (collisions.Length > 0) throw new InvalidDataException(collisions[0] + "; specify -o explicitly.");
                }
                Export(o, name, bytes); break;
            case "put":
                o.Count(3); o.Allow("--format", "--geometry", "--user", "--name", "--type", "--start", "--variables", "--autostart", "--tap-name");
                var data = File.ReadAllBytes(o.Positional[2]); var targetName = o.Get("--name", Path.GetFileName(o.Positional[2]));
                if (o.Values.TryGetValue("--tap-name", out var tapeName))
                {
                    if (hc is null) throw new ArgumentException("Tape metadata import requires HCFS.");
                    var file = Tap.Load(data).Get(tapeName); var h = file.HcHeader;
                    targetName = o.Get("--name", tapeName); hc.Put(targetName, file.Data, user, h.Type, h.Start, h.Variables, h.Autostart);
                }
                else if (hc is not null) hc.Put(targetName, data, user, (byte)o.Number("--type", 3, 255), (ushort)o.Number("--start", 0, 65535),
                    (ushort)o.Number("--variables", 0, 65535), (ushort)o.Number("--autostart", 0x8000, 65535));
                else
                {
                    if (o.Values.Keys.Any(k => k is "--type" or "--start" or "--variables" or "--autostart")) throw new ArgumentException("Header options require HCFS.");
                    fs.Put(targetName.ToUpperInvariant(), data, user);
                }
                AtomicWrite(path, disk.Save()); break;
            case "rm":
                o.Count(3); o.Allow("--format", "--geometry", "--user"); fs.Delete(o.Positional[2], user); AtomicWrite(path, disk.Save()); break;
            default: throw new ArgumentException("fs supports ls/get/put/rm/new.");
        }
    }
    private static TapeFile FromHc(string name, HcFileHeader header, byte[] bytes)
        => new(name.TrimEnd(' '), header.Type <= 3 ? header.Type : throw new InvalidDataException("HC file type cannot be represented on tape."),
            header.Type == 0 ? header.Autostart : header.Start, header.Variables, bytes);
    private static void Tape(Options o, TextWriter output)
    {
        var verb = o.Positional[0]; var path = o.Positional[1];
        if (verb == "create")
        {
            // Shared blank tape (KB hc2000-media-home-scan-admission-v1 sec 4): zero bytes, never a spec-13 guest format.
            o.Count(2); o.Allow();
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException("Refusing to replace an existing path with tap create.");
            AtomicWrite(path, MediaBlanks.TapeBlank(), false);
            return;
        }
        var tape = Tap.Load(File.Exists(path) ? File.ReadAllBytes(path) : verb == "put" ? [] : throw new FileNotFoundException("Tape not found.", path));
        switch (verb)
        {
            case "ls":
                o.Count(2); o.Allow();
                foreach (var f in tape.List()) output.WriteLine($"{f.Name}\t{f.Data.Length}\ttype={f.Type} start={f.Start} variables={f.Variables}"); break;
            case "get":
                o.Count(3); o.Allow("-o"); Export(o, o.Positional[2], tape.Get(o.Positional[2]).Data); break;
            case "put":
                o.Count(3); o.Allow("--name", "--type", "--start", "--variables", "--disk-name", "--format", "--geometry", "--user");
                if (o.Values.TryGetValue("--disk-name", out var name))
                {
                    var hc = new HcFs(CpmFs.Open(Load(o.Positional[2], o), o.Get("--format", "hcfs"))); var user = o.Number("--user", 0, 31);
                    tape.Put(FromHc(o.Get("--name", name), hc.Header(name, user), hc.Get(name, user)));
                }
                else tape.Put(new(o.Get("--name", Path.GetFileNameWithoutExtension(o.Positional[2])), (byte)o.Number("--type", 3, 3),
                    (ushort)o.Number("--start", 0, 65535), (ushort)o.Number("--variables", 0, 65535), File.ReadAllBytes(o.Positional[2])));
                AtomicWrite(path, tape.Save()); break;
            default: throw new ArgumentException("tap supports create/ls/get/put.");
        }
    }
    private static int Rom(Options o, TextWriter output)
    {
        o.Count(2); var verb = o.Positional[0]; var tools = new RomTools(); var bytes = File.ReadAllBytes(o.Positional[1]);
        if (verb == "id")
        {
            o.Allow("--manifest"); var manifestPath = o.Get("--manifest", FindManifest(o.Positional[1]));
            var manifest = File.ReadAllText(manifestPath); var matches = tools.Identify(bytes, manifest);
            foreach (var match in matches) output.WriteLine(match);
            if (matches.Count != 0) return 0;
            if (bytes.Length == 65536)
                foreach (var slice in tools.Split(bytes)) output.WriteLine($"{slice.Offset:X4} {slice.Name}: " + string.Join(",", tools.Identify(slice.Data, manifest)));
            output.WriteLine("unknown sha256=" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()); return 1;
        }
        if (verb == "split")
        {
            o.Allow("-o"); var slices = tools.Split(bytes); var folder = o.Get("-o", Path.GetFileNameWithoutExtension(o.Positional[1]) + "-split");
            if (slices.Any(s => File.Exists(Path.Combine(folder, s.Name)))) throw new IOException("Refusing to overwrite existing ROM slices.");
            Directory.CreateDirectory(folder);
            foreach (var slice in slices) { AtomicWrite(Path.Combine(folder, slice.Name), slice.Data, false); output.WriteLine($"{slice.Offset:X4} {slice.Data.Length} {slice.Name}"); }
            return 0;
        }
        throw new ArgumentException("rom supports id/split.");
    }
    private static string FindManifest(string input)
    {
        foreach (var start in new[] { Path.GetDirectoryName(Path.GetFullPath(input))!, Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var path = Path.Combine(dir.FullName, "MANIFEST.sha256"); if (File.Exists(path)) return path; dir = dir.Parent;
            }
        }
        return "MANIFEST.sha256";
    }
    private static StringComparer HostComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static string CatalogName(string name) => new(name.Select(c => char.IsControl(c) || "/\\\\*?".Contains(c) ? '_' : c).ToArray());
    private static IEnumerable<string> ExportCollisions(IEnumerable<CpmFile> files) => files.GroupBy(f => HostName(f.Name), HostComparer)
        .Where(g => g.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() > 1)
        .Select(g => "Export name collision " + JsonSerializer.Serialize(g.Key) + ": " + string.Join(", ", g.Select(f => JsonSerializer.Serialize(f.Name))));
    private static void Export(Options options, string name, byte[] bytes)
    {
        var path = options.Get("-o", HostName(name));
        if (!options.Values.ContainsKey("-o") && File.Exists(path)) throw new IOException("Refusing to overwrite implicit export path; specify -o explicitly.");
        AtomicWrite(path, bytes, options.Values.ContainsKey("-o"));
    }
    private static string HostName(string name)
    {
        name = name.TrimEnd(' '); // presentation only; disk identity stays the full field
        var safe = new string(name.Select(c => char.IsControl(c) || "<>/\\:*?\"|".Contains(c) ? '_' : c).ToArray());
        return safe.Length == 0 || safe is "." or ".." ? "hc-file.bin" : safe;
    }
    private static void AtomicWrite(string path, byte[] bytes, bool overwrite = true)
    {
        var full = Path.GetFullPath(path); var temporary = full + ".hc-" + Guid.NewGuid().ToString("N");
        var admission = NativeImageAdmission.BeginExistingPublication(full);
        bool moveStarted = false, moved = false;
        try
        {
            File.WriteAllBytes(temporary, bytes);
            admission?.ValidatePublicationTargetStillCurrent();
            moveStarted = true;
            File.Move(temporary, full, overwrite);
            moved = true;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally
            {
                if (moved || !moveStarted) admission?.CompleteKnownPublicationAfterDrain(
                    moved ? KnownImagePublication.KnownNew : KnownImagePublication.Unpublished);
                else admission?.RetainUnknownPublication();
            }
        }
    }
    // Compatibility facade: the implementation and its admission/quarantine state live in HostShared.
    public enum OfflineOutcome { Refused, KnownOld, KnownNew, Unknown }
    public enum OfflineError {
        None, InvalidRequest, UnsupportedStore, Busy, AbandonedLease,
        ReadOnly, Protected, StaleIdentity, InvalidImageSize,
        InvalidContainer, ResourceLimit, Cancelled, IoFailure,
        VerificationFailed, ReconciliationFailed,
        AlreadyInFlight, OwnerAbandoned, QuarantinePoisoned, ReconcileCapExhausted
    }
    public enum OfflineIoPoint {
        SourceOpen, FooterRead, IndexRead, SourceValidationRead,
        TempCreate, TempWrite, TempVerifyRead, SourceRecheckRead,
        BeforeMove, AfterMove, TargetReadback, TempDelete
    }
    public readonly record struct OfflineIoEvent(OfflineIoPoint Point, ulong Offset, int Count);
    public sealed record OfflineOwnership(bool LocalDisposable, bool ExclusivelyOwned, bool Unbound,
        bool SameHostSecurityDomain);
    public sealed record OfflineReplaceRequest(string TargetPath, HddVolumeIdentity ExpectedVolume,
        Guid OwnerEpoch, Guid AttemptId, OfflineOwnership Ownership, HddSelectionLimits Limits, HddSelectionUsage Usage);
    public sealed record OfflineCharge(ulong MetadataBytes, ulong CopyWorkBytes, ulong TotalLiveBytes,
        ulong TransactionBytes, ulong ReservedIoBytes, ulong ActualIoBytes, ulong TransferBufferBytes);
    public sealed record OfflineReceipt(string TargetPath, Guid OwnerEpoch, Guid AttemptId,
        HddVolumeIdentity ExpectedVolume, HddVolumeIdentity? IntendedVolume,
        ImmutableArray<byte> ExpectedFileSha256, ImmutableArray<byte> IntendedFileSha256,
        bool RenameMayHaveStarted, bool WriterDrained, OfflineOutcome Outcome, OfflineError Error,
        OfflineCharge Charge, Guid? BlockingAttemptId);
    public sealed record OfflineAbandonReceipt(Guid AttemptId, bool Abandoned, OfflineError Fault, DateTimeOffset Utc);

    public sealed class OfflineAttempt : IDisposable
    {
        private readonly HddContainerPublication.OfflineAttempt attempt;
        internal OfflineAttempt(HddContainerPublication.OfflineAttempt attempt) => this.attempt = attempt;
        public OfflineReceipt Receipt => FromPublication(attempt.Receipt);
        public OfflineReceipt Reconcile() => FromPublication(attempt.Reconcile());
        public OfflineAbandonReceipt Abandon(OfflineError fault)
        {
            var receipt = attempt.Abandon((HddContainerPublication.OfflineError)fault);
            return new(receipt.AttemptId, receipt.Abandoned, (OfflineError)receipt.Fault, receipt.Utc);
        }
        public void Dispose() => attempt.Dispose();
    }

    public static OfflineAttempt ReplaceOffline(OfflineReplaceRequest request, ReadOnlySpan<byte> replacement,
        CancellationToken cancellationToken = default, Action<OfflineIoEvent>? fault = null)
    {
        var limits = request?.Limits;
        var usage = request?.Usage;
        var ownership = request?.Ownership;
        var converted = request is null ? null! : new HddContainerPublication.OfflineReplaceRequest(request.TargetPath,
            ToPublication(request.ExpectedVolume), request.OwnerEpoch, request.AttemptId,
            ownership is null ? null! : new(ownership.LocalDisposable, ownership.ExclusivelyOwned, ownership.Unbound, ownership.SameHostSecurityDomain),
            limits is null ? null! : new(limits.MaxFileBytes, limits.MaxEntries, limits.MaxIndexBytes, limits.MaxMetadataBytes,
                limits.MaxSelectedBytes, limits.MaxResidentBytes, limits.MaxSnapshotBytes, limits.MaxCopyWorkBytes,
                limits.MaxTotalLiveBytes, limits.MaxTransactionBytes, limits.MaxIoBytes),
            usage is null ? null! : new(usage.ConcurrentSelectedImages, usage.ResidentBytes, usage.SnapshotBytes, usage.TransferBufferBytes));
        return new(HddContainerPublication.ReplaceOffline(converted, replacement, cancellationToken,
            fault is null ? null : e => fault(new((OfflineIoPoint)e.Point, e.Offset, e.Count))));
    }
    private static HddContainerPublication.VolumeIdentity ToPublication(HddVolumeIdentity? identity) => identity is null ? null!
        : new(identity.Store is null ? null! : new(identity.Store.ContainerId, identity.Store.Revision, identity.Store.MetadataSha256),
            identity.Name, identity.PayloadSha256);
    private static HddVolumeIdentity FromPublication(HddContainerPublication.VolumeIdentity identity) => new(
        new(identity.Store.ContainerId, identity.Store.Revision, identity.Store.MetadataSha256), identity.Name, identity.PayloadSha256);
    private static OfflineReceipt FromPublication(HddContainerPublication.OfflineReceipt r) => new(r.TargetPath,
        r.OwnerEpoch, r.AttemptId, FromPublication(r.ExpectedVolume), r.IntendedVolume is null ? null : FromPublication(r.IntendedVolume),
        r.ExpectedFileSha256, r.IntendedFileSha256, r.RenameMayHaveStarted, r.WriterDrained,
        (OfflineOutcome)r.Outcome, (OfflineError)r.Error, new(r.Charge.MetadataBytes, r.Charge.CopyWorkBytes,
            r.Charge.TotalLiveBytes, r.Charge.TransactionBytes, r.Charge.ReservedIoBytes, r.Charge.ActualIoBytes,
            r.Charge.TransferBufferBytes), r.BlockingAttemptId);

    private sealed class Options
    {
        public List<string> Positional { get; } = new();
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public Options(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith('-')) { Positional.Add(args[i]); continue; }
                var name = args[i];
                if (Values.ContainsKey(name)) throw new ArgumentException("Duplicate option " + name);
                if (name == "--tap") { Values[name] = "true"; continue; }
                if (name is not ("-o" or "--format" or "--geometry" or "--container" or "--user" or "--name" or "--type" or "--start" or "--variables" or "--autostart" or "--tap-name" or "--disk-name" or "--manifest"))
                    throw new ArgumentException("Unknown option " + name);
                if (++i >= args.Length || args[i].StartsWith('-')) throw new ArgumentException("Missing value for " + name);
                Values[name] = args[i];
            }
        }
        public void Count(int count) { if (Positional.Count != count) throw new ArgumentException("Wrong number of operands."); }
        public void Allow(params string[] names)
        {
            foreach (var key in Values.Keys) if (!names.Contains(key)) throw new ArgumentException("Option is not valid for this operation: " + key);
        }
        public string Get(string name, string fallback) => Values.GetValueOrDefault(name, fallback);
        public string Required(string name) => Values.TryGetValue(name, out var value) ? value : throw new ArgumentException("Missing " + name);
        public int Number(string name, int fallback, int max)
        {
            var text = Get(name, fallback.ToString(CultureInfo.InvariantCulture));
            var value = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? int.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : int.Parse(text, CultureInfo.InvariantCulture);
            return value is >= 0 && value <= max ? value : throw new ArgumentException(name + " out of range.");
        }
    }
}
