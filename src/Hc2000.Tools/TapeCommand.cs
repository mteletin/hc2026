using System.Globalization;
using System.Text.Json;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Codecs;

namespace Hc2000.Tools;

/// <summary>Host-only adapter over the common detached codec registry. No deck, decoder or clock.</summary>
public static class TapeCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error, ITapeCodecRegistry registry)
    {
        try
        {
            var options = Options.Parse(args);
            if (options.Capabilities)
            {
                var capabilities = Enum.GetValues<TapeFormat>().Where(f => f != TapeFormat.Blank).Select(f =>
                {
                    var found = registry.Find(f);
                    return new { Format = f.ToString(), CanRead = found.Success && found.Value.Capabilities.CanRead,
                        CanWrite = found.Success && found.Value.Capabilities.CanWrite,
                        Reason = found.Success ? found.Value.Capabilities.UnavailableReason : found.Failure!.Detail };
                }).ToArray();
                if (options.Json) WriteJson(output, capabilities);
                else foreach (var c in capabilities) output.WriteLine($"{c.Format}: read={c.CanRead} write={c.CanWrite} {c.Reason}");
                return 0;
            }

            var source = Require(registry.Find(options.InputFormat));
            RequireCapability(source, false);
            ITapeCodec? target = null;
            if (options.Operation == "convert")
            {
                target = Require(registry.Find(options.Target!.Value));
                RequireCapability(target, true);
                if (SamePath(options.Input!, options.Output!))
                    throw new ArgumentException("Input and output must be different paths.");
                if (File.Exists(options.Output) && !options.Force)
                    throw new IOException("Output already exists; use --force to replace it.");
            }
            var read = Require(source.Read(ReadBounded(options.Input!, options.Import), options.Import));
            foreach (var warning in read.Warnings) error.WriteLine($"hc tape: warning {warning.Code}: {warning.Detail}");
            if (target is not null)
            {
                // Writer consumes exactly the imported core image, including its route and trailing blank.
                var written = Require(target.Write(read.Image, options.Export!));
                AtomicWrite(options.Output!, written.Bytes.AsSpan(), options.Force);
                var report = written.Report;
                if (options.Json) WriteJson(output, new { Target = report.Target.ToString(), Signal = report.Signal.ToString(),
                    Bytes = report.Bytes.ToString(), Amplitude = report.Amplitude.ToString(), report.Rate,
                    MaxEdgeDisplacement = Rational(report.MaxEdgeDisplacement), report.EliminatedTransitions,
                    ChangedAnnotations = report.ChangedAnnotations.Select(Annotation) });
                else
                {
                    output.WriteLine($"{report.Target}: signal={report.Signal} bytes={report.Bytes} amplitude={report.Amplitude} rate={report.Rate?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
                    output.WriteLine($"max-edge-displacement={Time(report.MaxEdgeDisplacement)}s eliminated-transitions={report.EliminatedTransitions}");
                    foreach (var annotation in report.ChangedAnnotations) output.WriteLine($"changed: {Time(annotation.Position)}s {annotation.Kind} {annotation.Label}");
                }
            }
            else if (options.Operation == "blocks")
            {
                // This is materialized route/metadata inspection, never a parallel container decoder.
                if (options.Json) WriteJson(output, new { SourceRoute = read.Image.SourceRoute,
                    Markers = read.Image.Markers.Select(m => new { Position = Rational(m.Position), m.Ordinal, m.Label, m.SourceId, m.SourceBlock }),
                    Annotations = read.Image.Annotations.Select(Annotation) });
                else
                {
                    foreach (var visit in read.Image.SourceRoute) output.WriteLine($"source={visit.SourceId} block={visit.BlockIndex} visit={visit.Visit}");
                    foreach (var marker in read.Image.Markers) output.WriteLine($"stop {Time(marker.Position)}s #{marker.Ordinal} {marker.Label}");
                    foreach (var annotation in read.Image.Annotations) output.WriteLine($"{Time(annotation.Position)}s #{annotation.Ordinal} {annotation.Kind} {annotation.Label}");
                }
            }
            else
            {
                var image = read.Image;
                if (options.Json) WriteJson(output, new { Format = options.InputFormat.ToString(), Side = options.Import.Side.ToString(),
                    Capacity = options.Import.Capacity.ToString(), Duration = Rational(image.Duration), SourceDuration = Rational(image.SourceDuration),
                    Chunks = image.Chunks.Length, RouteVisits = image.SourceRoute.Length,
                    Comparator = image.ImportOptions.ComparatorModel, Channel = image.ImportOptions.Channel?.ToString(),
                    image.ImportOptions.Target48K, Selections = image.ImportOptions.Selections,
                    Warnings = read.Warnings });
                else
                {
                    output.WriteLine($"{options.InputFormat}: side={options.Import.Side} capacity={options.Import.Capacity}");
                    output.WriteLine($"duration={Time(image.Duration)}s source-duration={Time(image.SourceDuration)}s chunks={image.Chunks.Length} route-visits={image.SourceRoute.Length}");
                    output.WriteLine($"channel={image.ImportOptions.Channel?.ToString() ?? "not-applicable"} comparator={image.ImportOptions.ComparatorModel ?? "not-applicable"} target48k={image.ImportOptions.Target48K}");
                    foreach (var selection in image.ImportOptions.Selections) output.WriteLine($"selection occurrence={selection.Occurrence} option={selection.Option}");
                }
            }
            return 0;
        }
        catch (TapeCommandFailure ex) { error.WriteLine($"hc tape: {ex.Code}: {ex.Message}"); return 1; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or OverflowException)
        { error.WriteLine($"hc tape: {ex.Message}"); return 1; }
    }

    private static T Require<T>(TapeResult<T> result) where T : notnull => result.Success ? result.Value
        : throw new TapeCommandFailure(result.Failure!.Code, result.Failure.Detail);
    private static void RequireCapability(ITapeCodec codec, bool write)
    {
        if (write ? !codec.Capabilities.CanWrite : !codec.Capabilities.CanRead)
            throw new TapeCommandFailure(TapeError.RuntimeUnavailable, codec.Capabilities.UnavailableReason ?? $"{codec.Format} {(write ? "writer" : "reader")} unavailable.");
    }
    private sealed class TapeCommandFailure(TapeError code, string detail) : Exception(detail)
    { public TapeError Code { get; } = code; }
    private static string Time(TapeTime t) => $"{t.Numerator.ToString(CultureInfo.InvariantCulture)}/{t.Denominator.ToString(CultureInfo.InvariantCulture)}";
    private static object Rational(TapeTime t) => new { Num = t.Numerator.ToString(CultureInfo.InvariantCulture), Den = t.Denominator.ToString(CultureInfo.InvariantCulture) };
    private static object Annotation(TapeAnnotation a) => new { Position = Rational(a.Position), a.Ordinal, Kind = a.Kind.ToString(), a.Label, a.SourceId, a.SourceBlock };
    private static void WriteJson(TextWriter output, object value) => output.WriteLine(JsonSerializer.Serialize(value));
    private static bool SamePath(string a, string b)
    {
        static string Canonical(string path)
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full)!;
            var current = root;
            var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                FileSystemInfo entry = i == parts.Length - 1 ? new FileInfo(current) : new DirectoryInfo(current);
                if (entry.Exists) current = entry.ResolveLinkTarget(true)?.FullName ?? current;
            }
            return current;
        }
        return string.Equals(Canonical(a), Canonical(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
    private static byte[] ReadBounded(string path, TapeImportOptions options)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = stream.Length;
        var limit = options.Limits.InputBytes;
        if (length > limit) throw new TapeCommandFailure(TapeError.BoundsExceeded, $"Input exceeds {limit} bytes.");
        if (options.Format == TapeFormat.Wav)
        {
            var preflight = new WavPcmPreflight(length, options);
            Span<byte> header = stackalloc byte[18];
            while (preflight.ReadLength != 0)
            {
                stream.Position = preflight.ReadOffset;
                var window = header[..preflight.ReadLength];
                stream.ReadExactly(window);
                preflight.Feed(window);
            }
            if (preflight.Failure is { } failure) throw new TapeCommandFailure(failure.Code, failure.Detail);
            if (stream.Length != length) throw new TapeCommandFailure(TapeError.BoundsExceeded, "Input changed during preflight.");
            stream.Position = 0;
        }
        var bytes = new byte[checked((int)length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new TapeCommandFailure(TapeError.BoundsExceeded, "Input grew while reading.");
        return bytes;
    }
    private static void AtomicWrite(string path, ReadOnlySpan<byte> bytes, bool overwrite)
    {
        var destination = Path.GetFullPath(path);
        var temporary = destination + ".hc-" + Guid.NewGuid().ToString("N");
        var admission = Hc2000.HostFiles.NativeImageAdmission.BeginExistingPublication(destination);
        bool moveStarted = false, moved = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            admission?.ValidatePublicationTargetStillCurrent();
            moveStarted = true;
            File.Move(temporary, destination, overwrite);
            moved = true;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally
            {
                if (moved || !moveStarted) admission?.CompleteKnownPublicationAfterDrain(moved
                    ? Hc2000.HostFiles.KnownImagePublication.KnownNew : Hc2000.HostFiles.KnownImagePublication.Unpublished);
                else admission?.RetainUnknownPublication();
            }
        }
    }

    private sealed record Options(string Operation, string? Input, string? Output, TapeFormat InputFormat,
        TapeFormat? Target, TapeImportOptions Import, TapeExportOptions? Export, bool Json, bool Force, bool Capabilities)
    {
        public static Options Parse(string[] args)
        {
            if (args.Length == 0 || args[0] is not ("info" or "blocks" or "convert"))
                throw new ArgumentException("usage: hc tape <info|blocks|convert> <input> [--format tap|tzx|pzx|csw|wav] [options]; convert requires -o <output>; info --capabilities lists availability");
            var positional = new List<string>(); var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var flags = new HashSet<string>(StringComparer.Ordinal); var selections = new List<TapeSelection>();
            for (int i = 1; i < args.Length; i++)
            {
                var key = args[i];
                if (key == "--") { positional.AddRange(args[(i + 1)..]); break; }
                if (!key.StartsWith('-')) { positional.Add(key); continue; }
                if (key is "--json" or "--force" or "--quantize" or "--zrle" or "--capabilities")
                { if (!flags.Add(key)) throw new ArgumentException($"Duplicate option {key}."); continue; }
                if (key is not ("--format" or "--to" or "-o" or "--side" or "--capacity" or "--channel" or "--target48k" or "--select" or "--rate" or "--bits" or "--max-input"))
                    throw new ArgumentException($"Unknown option {key}.");
                if (++i == args.Length) throw new ArgumentException($"Missing value for {key}.");
                if (key == "--select")
                {
                    var parts = args[i].Split(':');
                    if (parts.Length != 2) throw new ArgumentException("--select requires occurrence:option (zero-based).");
                    var selection = new TapeSelection(Number(parts[0]), Number(parts[1]));
                    if (selections.Any(s => s.Occurrence == selection.Occurrence)) throw new ArgumentException("Duplicate selection occurrence.");
                    selections.Add(selection);
                }
                else if (!values.TryAdd(key, args[i])) throw new ArgumentException($"Duplicate option {key}.");
            }
            bool capabilities = flags.Contains("--capabilities");
            if (capabilities && (args[0] != "info" || positional.Count != 0 || values.Count != 0 || selections.Count != 0 || flags.Any(f => f is not ("--json" or "--capabilities"))))
                throw new ArgumentException("--capabilities is used only with info [--json], without an input.");
            if (!capabilities && positional.Count != 1) throw new ArgumentException("Exactly one input path is required.");
            bool convert = args[0] == "convert";
            if (!convert && (values.Keys.Any(k => k is "-o" or "--to" or "--rate" or "--bits") || flags.Any(f => f is "--force" or "--quantize" or "--zrle")))
                throw new ArgumentException("Export options require convert.");
            string? input = positional.FirstOrDefault(); string? output = values.GetValueOrDefault("-o");
            if (convert && string.IsNullOrWhiteSpace(output)) throw new ArgumentException("convert requires -o <output>.");
            var format = capabilities ? TapeFormat.Tap : Format(values.GetValueOrDefault("--format") ?? Path.GetExtension(input!));
            TapeFormat? target = convert ? Format(values.GetValueOrDefault("--to") ?? Path.GetExtension(output!)) : null;
            var side = Choice(values.GetValueOrDefault("--side") ?? "A", new Dictionary<string, TapeSide>(StringComparer.OrdinalIgnoreCase) { ["A"] = TapeSide.A, ["B"] = TapeSide.B });
            var capacity = Choice(values.GetValueOrDefault("--capacity") ?? "C60", new Dictionary<string, TapeCapacity>(StringComparer.OrdinalIgnoreCase) { ["C60"] = TapeCapacity.C60, ["C90"] = TapeCapacity.C90 });
            var channel = Choice(values.GetValueOrDefault("--channel") ?? "left", new Dictionary<string, TapeChannel>(StringComparer.OrdinalIgnoreCase) { ["left"] = TapeChannel.Left, ["right"] = TapeChannel.Right });
            var target48k = Choice(values.GetValueOrDefault("--target48k") ?? "true", new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["true"] = true, ["false"] = false });
            uint? rate = values.TryGetValue("--rate", out var r) ? Number(r) : null;
            if (rate == 0) throw new ArgumentException("Rate must be positive.");
            uint bits = values.TryGetValue("--bits", out var b) ? Number(b) : 16;
            if (bits is not (8 or 16 or 24 or 32)) throw new ArgumentException("WAV bits must be 8, 16, 24 or 32.");
            if (values.ContainsKey("--bits") && target != TapeFormat.Wav) throw new ArgumentException("--bits requires WAV output.");
            if (flags.Contains("--zrle") && target is not (TapeFormat.Csw or TapeFormat.Tzx)) throw new ArgumentException("--zrle requires CSW or TZX output.");
            if ((rate is not null || flags.Contains("--quantize")) && target is not (TapeFormat.Wav or TapeFormat.Csw or TapeFormat.Tzx)) throw new ArgumentException("Rate/quantize requires WAV, CSW or TZX output.");
            if (values.ContainsKey("--channel") && format != TapeFormat.Wav) throw new ArgumentException("--channel requires WAV input.");
            if ((selections.Count != 0 || values.ContainsKey("--target48k")) && format != TapeFormat.Tzx) throw new ArgumentException("Route selections/target48k require TZX input.");
            var limits = TapeInputPolicy.DefaultLimits(format);
            if (values.TryGetValue("--max-input", out var maximum))
            {
                uint n = Number(maximum);
                if (n == 0 || n > limits.InputBytes) throw new ArgumentException("--max-input must be positive and cannot raise the default bound.");
                limits = limits with { InputBytes = (int)n };
            }
            var import = new TapeImportOptions(format, side, capacity) { Channel = channel, Target48K = target48k, Selections = [.. selections], Limits = limits };
            // WAV's input allowance must not leak into the output budget of other formats.
            var exportLimits = limits with { InputBytes = Math.Min(limits.InputBytes, new TapeLimits().InputBytes) };
            var export = target is null ? null : new TapeExportOptions(target.Value, side) { Rate = rate, AllowQuantize = flags.Contains("--quantize"), UseZrle = flags.Contains("--zrle"), WavBitsPerSample = bits, Limits = exportLimits };
            return new(args[0], input, output, format, target, import, export, flags.Contains("--json"), flags.Contains("--force"), capabilities);
        }
        private static uint Number(string value) => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException($"Invalid unsigned integer '{value}'.");
        private static T Choice<T>(string value, Dictionary<string, T> choices) => choices.TryGetValue(value, out var choice) ? choice : throw new ArgumentException($"Invalid value '{value}'.");
        private static TapeFormat Format(string value) => value.TrimStart('.').ToLowerInvariant() switch
        { "tap" => TapeFormat.Tap, "tzx" => TapeFormat.Tzx, "pzx" => TapeFormat.Pzx, "csw" => TapeFormat.Csw, "wav" => TapeFormat.Wav, _ => throw new ArgumentException($"Unknown tape format '{value}'; use --format/--to.") };
    }
}
