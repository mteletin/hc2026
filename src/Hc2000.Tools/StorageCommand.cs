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
                case "hdd": HardDisk(options, output); break;
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
    private static void HardDisk(Options o, TextWriter output)
    {
        const int imageBytes = 8_388_608;
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
            default: throw new ArgumentException("hdd supports create/identify/inspect.");
        }
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
            default: throw new ArgumentException("tap supports ls/get/put.");
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
    // Unused, synchronous host-only publication seam. Run/CLI deliberately do not call it.
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

    private const int OfflineBufferBytes = 32768;
    private const int MaxQuarantined = 4;
    // Before bounded shape admission there is no validated identity snapshot to report.
    private static readonly HddVolumeIdentity OfflineUnvalidatedIdentity = new(new([], 0, []), "", []);
    private static OfflineAttempt? offlineAdmission;
    private static readonly object offlineState = new();
    private static readonly OfflineAttempt?[] offlineQuarantine = new OfflineAttempt?[MaxQuarantined];
    private static bool offlineTerminalPoison;
    private static long offlineSequence;
    private static long offlineUtcTicks;

    private static DateTimeOffset OfflineStamp()
    {
        // Called under offlineState. Wall time is evidence only, never lease expiry.
        offlineUtcTicks = Math.Max(DateTimeOffset.UtcNow.Ticks, checked(offlineUtcTicks + 1));
        return new DateTimeOffset(offlineUtcTicks, TimeSpan.Zero);
    }
    private sealed class OfflineRefusal(OfflineError error) : Exception
    { public OfflineError Error => error; }
    private static void OfflineRequire([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, OfflineError error)
    { if (!condition) throw new OfflineRefusal(error); }
    private static bool OfflineHash(ImmutableArray<byte> value) => !value.IsDefault && value.Length == 32;
    private static bool OfflineId(ImmutableArray<byte> value) => !value.IsDefault && value.Length == 16 && value.Any(b => b != 0);
    private static ImmutableArray<byte> OfflineDigest(ReadOnlySpan<byte> value) => ImmutableArray.CreateRange(SHA256.HashData(value));
    private static bool OfflineEqual(ImmutableArray<byte> a, ImmutableArray<byte> b) => a.AsSpan().SequenceEqual(b.AsSpan());

    public sealed class OfflineAttempt : IDisposable
    {
        internal readonly OfflineReplaceRequest Request;
        internal readonly Action<OfflineIoEvent>? Fault;
        internal readonly long Sequence;
        internal byte[] Buffer = [];
        internal ulong FileBytes;
        internal bool OwnsAdmission;
        internal bool Retained;
        internal bool Completed;
        internal bool WriterActive = true;
        internal NativeImageAdmission? NativeAdmission;
        internal OfflineAbandonReceipt? Abandonment;
        private OfflineReceipt receipt;
        public OfflineReceipt Receipt { get { lock (offlineState) return receipt; } }

        internal OfflineAttempt(OfflineReplaceRequest request, Action<OfflineIoEvent>? fault)
        {
            Request = request;
            Fault = fault;
            Sequence = Interlocked.Increment(ref offlineSequence);
            receipt = new(request?.TargetPath ?? "", request?.OwnerEpoch ?? Guid.Empty, request?.AttemptId ?? Guid.Empty,
                OfflineUnvalidatedIdentity, null, [], [], false, true, OfflineOutcome.Refused, OfflineError.None,
                new(0, 0, 0, 0, 0, 0, 0), null);
        }
        internal FileStream OpenSource(string path) => NativeAdmission?.OpenPublicationSource(path) ?? OfflineOpen(path);
        internal void Set(OfflineReceipt value) { lock (offlineState) receipt = value; }
        internal void Observe(OfflineIoPoint point, ulong offset = 0, int count = 0) => Fault?.Invoke(new(point, offset, count));
        internal void Charge(int count)
        {
            var r = Receipt;
            var actual = checked(r.Charge.ActualIoBytes + (ulong)count);
            OfflineRequire(actual <= r.Charge.ReservedIoBytes, OfflineError.ResourceLimit);
            Set(r with { Charge = r.Charge with { ActualIoBytes = actual } });
        }
        internal void Read(FileStream stream, Span<byte> bytes, OfflineIoPoint point)
        {
            Observe(point, (ulong)stream.Position, bytes.Length);
            Charge(bytes.Length); // Partial/uncertain reads conservatively charge the requested amount.
            stream.ReadExactly(bytes);
        }
        internal void Write(FileStream stream, ReadOnlySpan<byte> bytes)
        {
            Observe(OfflineIoPoint.TempWrite, (ulong)stream.Position, bytes.Length);
            Charge(bytes.Length);
            stream.Write(bytes);
        }
        internal void Finish(OfflineOutcome outcome, OfflineError error)
        {
            lock (offlineState)
            {
                receipt = receipt with { Outcome = outcome, Error = error, WriterDrained = true };
                if (outcome == OfflineOutcome.Unknown)
                {
                    Retained = true;
                    if (!offlineQuarantine.Contains(this))
                    {
                        var slot = Array.IndexOf(offlineQuarantine, null);
                        if (slot < 0)
                        {
                            // Defensive guard, unreachable by supported count<=1 transitions.
                            offlineTerminalPoison = true;
                            receipt = receipt with { Error = OfflineError.ReconcileCapExhausted };
                        }
                        else offlineQuarantine[slot] = this;
                    }
                    return; // Retain the token; neither Dispose nor another invocation can free it.
                }
                Completed = true;
                Retained = false;
                for (var i = 0; i < MaxQuarantined; i++)
                    if (ReferenceEquals(offlineQuarantine[i], this)) offlineQuarantine[i] = null;
                if (OwnsAdmission && !offlineTerminalPoison && offlineQuarantine.All(a => a is null))
                    Interlocked.CompareExchange(ref offlineAdmission, null, this);
                OwnsAdmission = false;
                Buffer = [];
            }
        }
        public OfflineAbandonReceipt Abandon(OfflineError fault)
        {
            lock (offlineState)
            {
                if (Completed || !OwnsAdmission || Sequence <= 0 || !ReferenceEquals(offlineAdmission, this))
                    return new(Receipt.AttemptId, false, OfflineError.InvalidRequest, OfflineStamp());
                if (Abandonment is not null) return Abandonment;
                Abandonment = new(Receipt.AttemptId, true, fault, OfflineStamp());
                Finish(OfflineOutcome.Unknown, Receipt.Error);
                return Abandonment;
            }
        }
        public OfflineReceipt Reconcile()
        {
            lock (offlineState)
            {
                if (WriterActive || Completed || !OwnsAdmission || !Retained) return receipt;
            }
            // Only this owned attempt reads; no detached writer or cap reset. Serialize same-handle readbacks.
            lock (this)
            {
                if (Completed) return Receipt;
                Resolve(Receipt.Error);
                DrainNative();
                return Receipt;
            }
        }
        internal void DrainNative()
        {
            lock (offlineState)
            {
                if (WriterActive || NativeAdmission is null) return;
                try
                {
                    if (receipt.Outcome == OfflineOutcome.Unknown) NativeAdmission.RetainUnknownPublication();
                    else
                    {
                        NativeAdmission.CompleteKnownPublicationAfterDrain(receipt.Outcome switch
                        {
                            OfflineOutcome.KnownNew => KnownImagePublication.KnownNew,
                            OfflineOutcome.KnownOld => KnownImagePublication.KnownOld,
                            _ => KnownImagePublication.Unpublished
                        });
                        NativeAdmission = null;
                    }
                }
                catch (IOException) { receipt = receipt with { Error = OfflineError.IoFailure }; }
            }
        }
        internal void Resolve(OfflineError cause)
        {
            try
            {
                var r = Receipt;
                OfflineRequire(OfflineHash(r.ExpectedFileSha256), OfflineError.ReconciliationFailed);
                using var stream = OpenSource(r.TargetPath);
                var facts = NativeAdmission?.ValidateOpenedSource(stream, r.TargetPath);
                OfflineRequire((ulong)stream.Length == FileBytes, OfflineError.ReconciliationFailed);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                while (stream.Position < stream.Length)
                {
                    var count = (int)Math.Min(Buffer.Length, stream.Length - stream.Position);
                    OfflineRequire(count > 0, OfflineError.ReconciliationFailed);
                    Read(stream, Buffer.AsSpan(0, count), OfflineIoPoint.TargetReadback);
                    hash.AppendData(Buffer, 0, count);
                }
                var actual = ImmutableArray.CreateRange(hash.GetHashAndReset());
                if (facts is not null) NativeAdmission!.ValidateSourceStillCurrent(stream, facts);
                if (OfflineEqual(actual, r.IntendedFileSha256)) Finish(OfflineOutcome.KnownNew, cause);
                else if (OfflineEqual(actual, r.ExpectedFileSha256)) Finish(OfflineOutcome.KnownOld, cause);
                else Finish(r.RenameMayHaveStarted ? OfflineOutcome.Unknown : OfflineOutcome.Refused, OfflineError.ReconciliationFailed);
            }
            catch (Exception ex)
            {
                var error = ex is OfflineRefusal refusal ? refusal.Error : OfflineError.ReconciliationFailed;
                Finish(Receipt.RenameMayHaveStarted ? OfflineOutcome.Unknown : OfflineOutcome.Refused, error);
            }
        }
        public void Dispose()
        {
            // No ownership release for Unknown, no throwing Dispose strategy, no background cleanup.
            lock (offlineState) { if (Completed || !OwnsAdmission) Buffer = []; }
        }
    }

    private static FileStream OfflineOpen(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);

    public static OfflineAttempt ReplaceOffline(OfflineReplaceRequest request, ReadOnlySpan<byte> replacement,
        CancellationToken cancellationToken = default, Action<OfflineIoEvent>? fault = null)
    {
        var attempt = new OfflineAttempt(request, fault);
        string? temporary = null;
        try
        {
            OfflineShape(request, replacement.Length);
            var supplied = request.ExpectedVolume;
            var expected = new HddVolumeIdentity(new(ImmutableArray.Create<byte>(supplied.Store.ContainerId.AsSpan()),
                supplied.Store.Revision, ImmutableArray.Create<byte>(supplied.Store.MetadataSha256.AsSpan())),
                supplied.Name, ImmutableArray.Create<byte>(supplied.PayloadSha256.AsSpan()));
            attempt.Set(attempt.Receipt with { ExpectedVolume = expected,
                Charge = attempt.Receipt.Charge with { MetadataBytes = 128 } });
            var prior = Interlocked.CompareExchange(ref offlineAdmission, attempt, null);
            if (prior is not null)
            {
                lock (offlineState)
                    attempt.Set(attempt.Receipt with { Error = prior.Retained || offlineTerminalPoison
                        ? OfflineError.QuarantinePoisoned : OfflineError.AlreadyInFlight,
                        BlockingAttemptId = prior.Receipt.AttemptId });
                attempt.Completed = true;
                return attempt;
            }
            attempt.OwnsAdmission = true;
            OfflineRequire(!offlineTerminalPoison, OfflineError.QuarantinePoisoned);
            var full = Path.GetFullPath(request.TargetPath);
            attempt.Set(attempt.Receipt with { TargetPath = full });
            if (NativeImageAdmission.PlatformMode == ImageAdmissionPlatformMode.GuardedLinux)
            {
                var native = NativeImageAdmission.TryBeginPublication(full, request.AttemptId, cancellationToken);
                if (native.Admission is null)
                    throw new OfflineRefusal(native.Refusal switch
                    {
                        ImageAdmissionRefusal.Busy => OfflineError.Busy,
                        ImageAdmissionRefusal.Quarantined => OfflineError.QuarantinePoisoned,
                        ImageAdmissionRefusal.Cancelled => OfflineError.Cancelled,
                        ImageAdmissionRefusal.Unsupported or ImageAdmissionRefusal.InvalidDomain => OfflineError.UnsupportedStore,
                        _ => OfflineError.IoFailure
                    });
                attempt.NativeAdmission = native.Admission;
            }
            // All ancestors must be local ordinary directories, not reparse/symlink entry paths.
            for (FileSystemInfo? item = new FileInfo(full); item is not null;
                 item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
                OfflineRequire((item.Attributes & FileAttributes.ReparsePoint) == 0, OfflineError.UnsupportedStore);
            OfflineRequire((File.GetAttributes(full) & FileAttributes.ReadOnly) == 0, OfflineError.ReadOnly);
            if (!OperatingSystem.IsWindows())
                OfflineRequire((File.GetUnixFileMode(full) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0,
                    OfflineError.ReadOnly);
            attempt.Observe(OfflineIoPoint.SourceOpen);
            using (var source = attempt.OpenSource(full))
            {
                var sourceFacts = attempt.NativeAdmission?.ValidateOpenedSource(source, full);
                var length = checked((ulong)source.Length);
                attempt.FileBytes = length;
                OfflineRequire(length >= 64 && length <= request.Limits.MaxFileBytes && length <= uint.MaxValue
                    && checked(2 * length) <= request.Limits.MaxTransactionBytes
                    && checked(7 * length + 64) <= request.Limits.MaxIoBytes, OfflineError.ResourceLimit);
                var codecLimits = OfflineCodecLimits(request.Limits);
                // The bounded enclosing footer is admitted before any variable-size allocation.
                attempt.Set(attempt.Receipt with { Charge = attempt.Receipt.Charge with { ReservedIoBytes = request.Limits.MaxIoBytes } });
                byte[] footer = new byte[64];
                source.Position = (long)length - 64;
                attempt.Read(source, footer, OfflineIoPoint.FooterRead);
                var inspected = HddCatalogCodec.InspectHddFooter(footer, length, codecLimits);
                OfflineRequire(inspected.Success, OfflineError.InvalidContainer);
                var view = inspected.Value!;
                OfflineBudget(attempt, view);
                var index = new byte[checked((int)view.IndexBytes)];
                source.Position = checked((long)view.IndexStart);
                attempt.Read(source, index, OfflineIoPoint.IndexRead);
                var decoded = HddCatalogCodec.DecodeHdd(footer, index, length, codecLimits);
                OfflineRequire(decoded.Success, OfflineError.InvalidContainer);
                var descriptor = decoded.Value!;
                using var tokenHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                tokenHash.AppendData(index); tokenHash.AppendData(footer);
                var token = ImmutableArray.CreateRange(tokenHash.GetHashAndReset());
                OfflineRequire(view.ContainerId.AsSpan().SequenceEqual(expected.Store.ContainerId.AsSpan())
                    && view.Revision == expected.Store.Revision && OfflineEqual(token, expected.Store.MetadataSha256), OfflineError.StaleIdentity);
                var selected = descriptor.Entries.FirstOrDefault(e => StringComparer.OrdinalIgnoreCase.Equals(e.Name, expected.Name));
                OfflineRequire(selected is not null, OfflineError.StaleIdentity);
                OfflineRequire(!selected!.ReadOnly, OfflineError.Protected);
                OfflineRequire(view.Revision < ulong.MaxValue, OfflineError.ResourceLimit);
                attempt.Buffer = new byte[OfflineBufferBytes];
                var oldHash = OfflineValidate(attempt, source, descriptor.Entries, index, footer,
                    selected.SlotOrdinal, expected.PayloadSha256, OfflineIoPoint.SourceValidationRead);
                attempt.Set(attempt.Receipt with { ExpectedFileSha256 = oldHash });
                var replacementCrc = VolumeCatalogMetadata.Crc32(replacement);
                var entries = descriptor.Entries.Select(e => e.SlotOrdinal == selected.SlotOrdinal
                    ? e with { ImageCrc = replacementCrc } : e).ToImmutableArray();
                var encoded = HddCatalogCodec.EncodeHdd(view.ContainerId, checked(view.Revision + 1), entries, codecLimits);
                OfflineRequire(encoded.Success, OfflineError.InvalidContainer);
                var metadata = encoded.Value!;
                using var newTokenHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                newTokenHash.AppendData(metadata.Index.AsSpan()); newTokenHash.AppendData(metadata.Footer.AsSpan());
                var intended = new HddVolumeIdentity(new(ImmutableArray.CreateRange(view.ContainerId), view.Revision + 1,
                    ImmutableArray.CreateRange(newTokenHash.GetHashAndReset())), selected.Name, OfflineDigest(replacement));
                attempt.Set(attempt.Receipt with { IntendedVolume = intended });
                cancellationToken.ThrowIfCancellationRequested();
                temporary = full + ".hc-" + Guid.NewGuid().ToString("N");
                attempt.Observe(OfflineIoPoint.TempCreate);
                using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
                {
                    source.Position = 0;
                    for (ulong offset = 0; offset < view.ArenaBytes;)
                    {
                        var count = (int)Math.Min((ulong)OfflineBufferBytes, view.ArenaBytes - offset);
                        attempt.Read(source, attempt.Buffer.AsSpan(0, count), OfflineIoPoint.SourceValidationRead);
                        var selectedStart = (ulong)selected.SlotOrdinal.Value * VolumeCatalogMetadata.SlotBytes;
                        if (offset >= selectedStart && offset < selectedStart + VolumeCatalogMetadata.SlotBytes)
                            attempt.Write(target, replacement.Slice(checked((int)(offset - selectedStart)), count));
                        else attempt.Write(target, attempt.Buffer.AsSpan(0, count));
                        offset += (ulong)count;
                    }
                    attempt.Write(target, metadata.Index.AsSpan());
                    attempt.Write(target, metadata.Footer.AsSpan());
                }
                using (var verify = OfflineOpen(temporary))
                {
                    var newHash = OfflineValidate(attempt, verify, entries, metadata.Index.AsSpan(), metadata.Footer.AsSpan(),
                        selected.SlotOrdinal, intended.PayloadSha256, OfflineIoPoint.TempVerifyRead);
                    attempt.Set(attempt.Receipt with { IntendedFileSha256 = newHash });
                }
                // Reopen the path: rechecking only an old open inode would miss replaced-path races.
                using (var recheck = attempt.OpenSource(full))
                {
                    if (sourceFacts is not null) attempt.NativeAdmission!.ValidateSourceStillCurrent(recheck, sourceFacts);
                    var again = OfflineValidate(attempt, recheck, descriptor.Entries, index, footer, selected.SlotOrdinal,
                        expected.PayloadSha256, OfflineIoPoint.SourceRecheckRead);
                    OfflineRequire(OfflineEqual(again, oldHash), OfflineError.StaleIdentity);
                }
            }
            attempt.Observe(OfflineIoPoint.BeforeMove);
            cancellationToken.ThrowIfCancellationRequested();
            attempt.NativeAdmission?.ValidatePublicationTargetStillCurrent();
            attempt.Set(attempt.Receipt with { RenameMayHaveStarted = true });
            File.Move(temporary!, full, true);
            attempt.Observe(OfflineIoPoint.AfterMove);
            attempt.Resolve(OfflineError.None);
        }
        catch (Exception ex)
        {
            var error = ex switch {
                OfflineRefusal refusal => refusal.Error,
                OperationCanceledException => OfflineError.Cancelled,
                OverflowException or OutOfMemoryException => OfflineError.ResourceLimit,
                UnauthorizedAccessException => OfflineError.ReadOnly,
                _ => OfflineError.IoFailure
            };
            if (attempt.OwnsAdmission && OfflineHash(attempt.Receipt.ExpectedFileSha256)) attempt.Resolve(error);
            else attempt.Finish(OfflineOutcome.Refused, error);
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    if (File.Exists(temporary))
                    {
                        attempt.Observe(OfflineIoPoint.TempDelete);
                        File.Delete(temporary);
                    }
                }
                catch (Exception) { /* Cleanup cannot rewrite the publication outcome or release Unknown. */ }
            }
            lock (offlineState) attempt.WriterActive = false;
            attempt.DrainNative();
        }
        return attempt;
    }

    private static void OfflineShape(OfflineReplaceRequest request, int size)
    {
        OfflineRequire(request is not null && request.Ownership is not null && request.Limits is not null
            && request.Usage is not null && request.ExpectedVolume is not null && request.ExpectedVolume.Store is not null,
            OfflineError.InvalidRequest);
        OfflineRequire(size == VolumeCatalogMetadata.SlotBytes, OfflineError.InvalidImageSize);
        // Reserve the fixed enclosing metadata before name allocation or the first footer read.
        OfflineRequire(request.Limits.MaxMetadataBytes >= 128, OfflineError.ResourceLimit);
        OfflineRequire(!string.IsNullOrWhiteSpace(request!.TargetPath) && !request.TargetPath.StartsWith("\\\\", StringComparison.Ordinal)
            && !request.TargetPath.StartsWith("//", StringComparison.Ordinal), OfflineError.UnsupportedStore);
        OfflineRequire(request.OwnerEpoch != Guid.Empty && request.AttemptId != Guid.Empty
            && OfflineId(request.ExpectedVolume.Store.ContainerId) && request.ExpectedVolume.Store.Revision > 0
            && OfflineHash(request.ExpectedVolume.Store.MetadataSha256) && OfflineHash(request.ExpectedVolume.PayloadSha256)
            && VolumeCatalogMetadata.CanonicalName83(request.ExpectedVolume.Name) is not null, OfflineError.InvalidRequest);
        OfflineRequire(request.Ownership is { LocalDisposable: true, ExclusivelyOwned: true, Unbound: true, SameHostSecurityDomain: true },
            OfflineError.UnsupportedStore);
        var l = request.Limits; var u = request.Usage;
        OfflineRequire(l.MaxFileBytes > 0 && l.MaxEntries > 0 && l.MaxIndexBytes > 0 && l.MaxMetadataBytes > 0
            && l.MaxSelectedBytes > 0 && l.MaxSnapshotBytes > 0 && l.MaxCopyWorkBytes > 0
            && l.MaxTotalLiveBytes > 0 && l.MaxTransactionBytes > 0 && l.MaxIoBytes >= 64
            && u.ConcurrentSelectedImages is >= 1 and <= 4 && u.SnapshotBytes >= VolumeCatalogMetadata.SlotBytes
            && u.ResidentBytes <= 4UL * VolumeCatalogMetadata.SlotBytes
            && u.TransferBufferBytes <= 65536 - OfflineBufferBytes, OfflineError.ResourceLimit);
        checked
        {
            var selected = (ulong)u.ConcurrentSelectedImages * VolumeCatalogMetadata.SlotBytes;
            var copies = 3 * selected + u.TransferBufferBytes + OfflineBufferBytes;
            OfflineRequire(selected <= l.MaxSelectedBytes && u.ResidentBytes <= l.MaxResidentBytes
                && u.SnapshotBytes <= l.MaxSnapshotBytes && copies <= l.MaxCopyWorkBytes
                && u.ResidentBytes + u.SnapshotBytes + copies <= l.MaxTotalLiveBytes, OfflineError.ResourceLimit);
        }
    }
    private static HddLimits OfflineCodecLimits(HddSelectionLimits limits) => new(
        (uint)Math.Min(limits.MaxFileBytes, uint.MaxValue), (uint)Math.Min(limits.MaxEntries, 65535),
        (uint)Math.Min(limits.MaxIndexBytes, int.MaxValue), (uint)Math.Min(limits.MaxMetadataBytes, uint.MaxValue));
    private static void OfflineBudget(OfflineAttempt a, HddFooterView view)
    {
        var l = a.Request.Limits; var u = a.Request.Usage;
        checked
        {
            var n = (ulong)view.Entries; var i = (ulong)view.IndexBytes; var f = view.FileLength;
            var metadata = 4 * i + 128 * n + 128;
            var selected = (ulong)u.ConcurrentSelectedImages * VolumeCatalogMetadata.SlotBytes;
            var q = u.TransferBufferBytes + OfflineBufferBytes;
            var copies = 3 * selected + q;
            var total = u.ResidentBytes + u.SnapshotBytes + metadata + copies;
            var transaction = 2 * f;
            // All six normal full-file passes plus at least one reconciliation, preliminary metadata included.
            var io = 7 * f + i + 64;
            OfflineRequire(n > 0 && n <= 65535 && n <= l.MaxEntries && f <= l.MaxFileBytes && i <= l.MaxIndexBytes
                && metadata <= l.MaxMetadataBytes && selected <= l.MaxSelectedBytes
                && u.ResidentBytes <= l.MaxResidentBytes && u.SnapshotBytes <= l.MaxSnapshotBytes
                && copies <= l.MaxCopyWorkBytes && total <= l.MaxTotalLiveBytes && transaction <= l.MaxTransactionBytes
                && io <= l.MaxIoBytes && q <= 65536, OfflineError.ResourceLimit);
            a.Set(a.Receipt with { Charge = new(metadata, copies, total, transaction, l.MaxIoBytes,
                a.Receipt.Charge.ActualIoBytes, q) });
        }
    }
    private static ImmutableArray<byte> OfflineValidate(OfflineAttempt a, FileStream stream,
        ImmutableArray<HddCatalogEntry> entries, ReadOnlySpan<byte> index, ReadOnlySpan<byte> footer,
        HddSlotOrdinal selected, ImmutableArray<byte> selectedHash, OfflineIoPoint point)
    {
        OfflineRequire((ulong)stream.Length == a.FileBytes, OfflineError.VerificationFailed);
        stream.Position = 0;
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (uint slot = 0; slot < entries.Length; slot++)
        {
            var row = entries.First(e => e.SlotOrdinal.Value == slot);
            var crc = VolumeCatalogMetadata.Crc32InitialState;
            using var imageHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var remaining = (int)VolumeCatalogMetadata.SlotBytes; remaining > 0;)
            {
                var count = Math.Min(remaining, a.Buffer.Length);
                a.Read(stream, a.Buffer.AsSpan(0, count), point);
                crc = VolumeCatalogMetadata.Crc32Update(crc, a.Buffer.AsSpan(0, count));
                whole.AppendData(a.Buffer, 0, count);
                imageHash.AppendData(a.Buffer, 0, count);
                remaining -= count;
            }
            OfflineRequire(VolumeCatalogMetadata.Crc32Finish(crc) == row.ImageCrc, OfflineError.VerificationFailed);
            var hash = imageHash.GetHashAndReset();
            if (slot == selected.Value)
                OfflineRequire(hash.AsSpan().SequenceEqual(selectedHash.AsSpan()), OfflineError.StaleIdentity);
        }
        // Metadata is small but still streamed/charged; no second index-sized transfer buffer.
        for (var position = 0; position < index.Length;)
        {
            var count = Math.Min(index.Length - position, a.Buffer.Length);
            a.Read(stream, a.Buffer.AsSpan(0, count), point);
            OfflineRequire(a.Buffer.AsSpan(0, count).SequenceEqual(index.Slice(position, count)), OfflineError.VerificationFailed);
            whole.AppendData(a.Buffer, 0, count); position += count;
        }
        a.Read(stream, a.Buffer.AsSpan(0, footer.Length), point);
        OfflineRequire(a.Buffer.AsSpan(0, footer.Length).SequenceEqual(footer), OfflineError.VerificationFailed);
        whole.AppendData(a.Buffer, 0, footer.Length);
        return ImmutableArray.CreateRange(whole.GetHashAndReset());
    }

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
