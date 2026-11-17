using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Hc2000.Machine;
using Hc2000.Machine.Expansion;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Video;

namespace Hc2000.App.Runtime;

/// <summary>Runs the shipped X2 VGA visual benchmark ROM on a cold X5 + X2 machine inside an
/// <see cref="EmulationWorker"/>, captures the eight VGA phase screenshots and every port-level
/// command measurement, builds the per-phase and per-command tables matching <c>X2Benchmark</c>,
/// and saves the results and eight VGA screenshots to a chosen directory (hc2000-x2-benchmark-in-app).</summary>
public static class X2BenchmarkRunner
{
    public const string EmbeddedRomResourceName = "Hc2000.App.Examples.X2Benchmark.benchmark.bin";
    public const ushort CommandPort = 0x759d;
    public const ushort DataPort = 0x769d;
    public const ushort PhaseAddress = 0x9000;
    public const ushort AckAddress = 0x9001;
    public const ushort FailOpcodeAddress = 0x9002;
    public const ushort FailStatusAddress = 0x9003;
    public const byte FinishedMarker = 0xff;
    public const byte FailedMarker = 0xee;
    public const double TicksPerTState = 5.0;
    public const int DefaultFrameLimit = 1000;
    public const int DefaultInteractivePhaseHoldMs = 250;

    public static readonly ImmutableArray<string> PhaseNames =
        ["palette", "clear", "fill", "copy", "glyph", "scroll", "cycle", "done"];

    public static readonly ImmutableArray<byte> OpcodeOrder = [1, 2, 3, 4, 5, 6, 7];

    public static byte[] LoadEmbeddedRom()
    {
        var assembly = typeof(X2BenchmarkRunner).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedRomResourceName);
        if (stream is not null)
        {
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        var diskPath = Path.Combine(AppContext.BaseDirectory, "examples", "x2-benchmark", "benchmark.bin");
        if (File.Exists(diskPath))
            return File.ReadAllBytes(diskPath);
        throw new InvalidOperationException($"Missing embedded X2 benchmark ROM '{EmbeddedRomResourceName}'.");
    }

    /// <summary>Creates an <see cref="EmulationWorker"/> that runs the X2 VGA benchmark and reports
    /// phase progress and final results via callbacks. After finishing (or failing a phase), the
    /// worker holds the machine on the final frame so the VGA and main screens remain visible.</summary>
    public static EmulationWorker CreateWorker(
        Action<int, string>? onPhaseProgress = null,
        Action<X2BenchmarkResult>? onCompleted = null,
        int phaseHoldMilliseconds = DefaultInteractivePhaseHoldMs,
        SpeedMode initialSpeed = SpeedMode.Normal,
        int frameLimit = DefaultFrameLimit,
        byte[]? romOverride = null)
    {
        var program = romOverride ?? LoadEmbeddedRom();
        if (program.Length is 0 or > 0x4000)
            throw new ArgumentException($"X2 benchmark ROM length {program.Length} is outside 1..16384 bytes.", nameof(romOverride));

        var basicRom = new byte[0x4000];
        Array.Copy(program, basicRom, program.Length);
        var prepared = new PreparedBasicMachine(
            (MachineProfile.Hc2000V2PagedExpansionX5V1, basicRom, new byte[0x4000], new byte[0x4000]),
            x2Vga: true);

        HcMemory? memory = null;
        PortDecoder? ports = null;
        var commands = new List<X2BenchmarkCommandSample>();
        var frameTimes = new List<X2BenchmarkFrameSample>();
        var phases = new List<X2BenchmarkPhaseScreenshot>();
        var phase = 1;
        byte acked = 0;
        byte seen = 0;
        var phaseFrames = 0;
        var totalFrames = 0;
        var stopped = false;
        var announcedFirstPhase = false;
        List<byte>? currentCommandFrame = null;
        long begin = 0, go = 0, idle = 0;

        void ResetState()
        {
            commands.Clear();
            frameTimes.Clear();
            phases.Clear();
            phase = 1;
            acked = 0;
            seen = 0;
            phaseFrames = 0;
            totalFrames = 0;
            stopped = false;
            announcedFirstPhase = false;
            currentCommandFrame = null;
            begin = go = idle = 0;
        }

        void FinishCompleted()
        {
            if (stopped) return;
            stopped = true;
            var result = X2BenchmarkResult.CreateCompleted(
                [.. phases],
                [.. commands],
                [.. frameTimes],
                totalFrames);
            onCompleted?.Invoke(result);
        }

        void FinishIncomplete(int incompletePhase, string reason)
        {
            if (stopped) return;
            stopped = true;
            var clampedPhase = Math.Clamp(incompletePhase, 1, PhaseNames.Length);
            var phaseName = PhaseNames[clampedPhase - 1];
            var result = X2BenchmarkResult.CreateIncomplete(
                clampedPhase,
                phaseName,
                reason,
                [.. phases],
                [.. commands],
                [.. frameTimes],
                totalFrames);
            onCompleted?.Invoke(result);
        }

        return new EmulationWorker(
            factory: () =>
            {
                ResetState();
                var machine = BasicMachineComposition.CreateColdNewMachine(
                    prepared,
                    new ComposedFloppySlot(),
                    new FloppyDrive(new()),
                    new FloppyDrive(new()),
                    _ => { },
                    _ => { },
                    observeCpu: (_, m) => memory = m,
                    observePorts: p => ports = p);

                memory!.Write(AckAddress, 0);
                ports!.Observer = access =>
                {
                    if (access.Origin.Role != CpuPortRole.Main) return;
                    switch (access.Port, access.Access, access.Value)
                    {
                        case (CommandPort, PortAccess.Write, 1):
                            currentCommandFrame = [];
                            begin = access.MasterTick;
                            go = idle = 0;
                            break;
                        case (DataPort, PortAccess.Write, _) when currentCommandFrame is not null && go == 0:
                            currentCommandFrame.Add(access.Value);
                            break;
                        case (CommandPort, PortAccess.Write, 2) when currentCommandFrame is not null:
                            go = access.MasterTick;
                            break;
                        case (CommandPort, PortAccess.Read, _) when currentCommandFrame is not null && go != 0:
                            if (idle == 0)
                            {
                                if ((access.Value & 1) == 0) idle = access.MasterTick;
                                break;
                            }
                            commands.Add(new X2BenchmarkCommandSample(
                                phase,
                                currentCommandFrame[1],
                                Work(currentCommandFrame[1], i => currentCommandFrame[i]),
                                begin,
                                go,
                                idle,
                                access.MasterTick));
                            currentCommandFrame = null;
                            break;
                    }
                };
                return machine;
            },
            boundary: context =>
            {
                if (!announcedFirstPhase)
                {
                    announcedFirstPhase = true;
                    context.SetSpeed(initialSpeed);
                    onPhaseProgress?.Invoke(1, PhaseNames[0]);
                }
            },
            // The benchmark machine opens nothing outside (no CP/NET, no X6 session), so the machine a reset or the stop
            // retires is disposed at once and its RAM goes back to the pool (hc2000-ram-one-buffer).
            cleanup: context => context.Machine.Dispose(),
            executionAllowed: () => !stopped,
            afterFrame: (_, result, elapsedMs, cancellationToken) =>
            {
                if (stopped || memory is not { } ram) return false;

                totalFrames++;
                var marker = ram.Read(PhaseAddress);
                if (marker == FailedMarker)
                {
                    var incompletePhase = Math.Clamp(acked + 1, 1, PhaseNames.Length);
                    var failOp = ram.Read(FailOpcodeAddress);
                    var failCode = ram.Read(FailStatusAddress);
                    FinishIncomplete(
                        incompletePhase,
                        $"Phase {incompletePhase} ({PhaseNames[incompletePhase - 1]}) did not finish: the X2 card refused command 0x{failOp:X2} with status 0x{failCode:X2}.");
                    return false;
                }
                if (marker == FinishedMarker)
                {
                    if (phases.Count < PhaseNames.Length)
                    {
                        var missingPhase = phases.Count + 1;
                        FinishIncomplete(
                            missingPhase,
                            $"Phase {missingPhase} ({PhaseNames[missingPhase - 1]}) did not finish before the benchmark stopped.");
                        return false;
                    }
                    FinishCompleted();
                    return false;
                }

                var held = false;
                if (marker == acked || marker == 0)
                {
                    phaseFrames++;
                    frameTimes.Add(new X2BenchmarkFrameSample(acked + 1, elapsedMs));
                }
                else if (seen != marker)
                {
                    seen = marker;
                    phaseFrames++;
                    frameTimes.Add(new X2BenchmarkFrameSample(acked + 1, elapsedMs));
                }
                else if (marker <= PhaseNames.Length)
                {
                    // The phase's screenshots outlive this frame: once the UI has drawn a frame it hands the buffer back
                    // for reuse (hc2000-app-frame-recycling), so keep owned copies, taken here while the pixels are still
                    // this frame's.
                    phases.Add(new X2BenchmarkPhaseScreenshot(
                        marker,
                        PhaseNames[marker - 1],
                        Owned(result.ExpansionVideo!),
                        Owned(result.Video),
                        phaseFrames));

                    if (phaseHoldMilliseconds > 0)
                    {
                        cancellationToken.WaitHandle.WaitOne(phaseHoldMilliseconds);
                        held = true;
                    }

                    ram.Write(AckAddress, marker);
                    acked = marker;
                    phase = marker + 1;
                    phaseFrames = 0;

                    if (phase <= PhaseNames.Length)
                        onPhaseProgress?.Invoke(phase, PhaseNames[phase - 1]);
                }

                if (!stopped && totalFrames >= frameLimit)
                {
                    var incompletePhase = Math.Clamp(acked + 1, 1, PhaseNames.Length);
                    FinishIncomplete(
                        incompletePhase,
                        $"Phase {incompletePhase} ({PhaseNames[incompletePhase - 1]}) did not finish within {frameLimit.ToString("N0", CultureInfo.InvariantCulture)} frames.");
                }

                return held;
            });
    }

    public static string OpcodeName(byte opcode) => opcode switch
    {
        1 => "Clear",
        2 => "FillRectangle",
        3 => "CopyRectangle",
        4 => "SetPalette",
        5 => "Present",
        6 => "DrawGlyph",
        7 => "ScrollUp",
        _ => $"0x{opcode:x2}",
    };

    public static long Work(byte opcode, Func<int, byte> frame)
    {
        int Word(int offset) => frame(offset) | (frame(offset + 1) << 8);
        return opcode switch
        {
            1 => 320 * 240,
            2 or 3 or 7 => (long)Word(12) * Word(14),
            6 => 32,
            _ => 0,
        };
    }

    internal static string FormatTStates(double ticks)
        => (ticks / TicksPerTState).ToString("N0", CultureInfo.InvariantCulture);

    internal static string FormatRangeTStates(IEnumerable<double> tickValues)
    {
        var all = tickValues.ToArray();
        if (all.Length == 0) return "-";
        var min = FormatTStates(all.Min());
        var max = FormatTStates(all.Max());
        return min == max ? min : $"{min} - {max}";
    }

    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    /// <summary>A copy of <paramref name="frame"/> with pixels of its own, for data kept after the frame's buffer may be reused.</summary>
    internal static VideoFrame Owned(VideoFrame frame) => frame with { Pixels = frame.Pixels.ToArray() };

    /// <summary>Encodes a <see cref="VideoFrame"/> as an 8-bit RGB PNG (filter 0), matching
    /// <c>X2BenchmarkTests.Png.Encode</c>.</summary>
    public static byte[] EncodeRgbPng(VideoFrame frame)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        var rgb = new byte[frame.Width * frame.Height * 3];
        var pixels = frame.Pixels.Span;
        for (var i = 0; i < pixels.Length; i++)
        {
            rgb[i * 3] = (byte)(pixels[i] >> 16);
            rgb[i * 3 + 1] = (byte)(pixels[i] >> 8);
            rgb[i * 3 + 2] = (byte)pixels[i];
        }

        using var raw = new MemoryStream();
        for (var y = 0; y < frame.Height; y++)
        {
            raw.WriteByte(0);
            raw.Write(rgb, y * frame.Width * 3, frame.Width * 3);
        }

        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw.ToArray());

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, frame.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), frame.Height);
        header[8] = 8;
        header[9] = 2;

        using var png = new MemoryStream();
        png.Write(signature);
        WritePngChunk(png, "IHDR", header);
        WritePngChunk(png, "IDAT", packed.ToArray());
        WritePngChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WritePngChunk(Stream png, string type, byte[] body)
    {
        var typed = new byte[4 + body.Length];
        Encoding.ASCII.GetBytes(type, typed);
        body.CopyTo(typed, 4);
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, body.Length);
        png.Write(number);
        png.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typed));
        png.Write(number);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xffffffffu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}

public sealed record X2BenchmarkCommandSample(
    int Phase,
    byte Opcode,
    long Work,
    long Begin,
    long Go,
    long Idle,
    long Done)
{
    public string CommandName => X2BenchmarkRunner.OpcodeName(Opcode);
    public long CardTicks => 1 + Work;
    public long SendTicks => Go - Begin;
    public long WaitTicks => Idle - Go;
    public long TotalTicks => Done - Begin;
}

public sealed record X2BenchmarkFrameSample(int Phase, double HostMilliseconds);

public sealed record X2BenchmarkPhaseScreenshot(
    int Phase,
    string Name,
    VideoFrame Vga,
    VideoFrame Main,
    int Frames)
{
    public string VgaFileName => $"phase-{Phase}-{Name}-vga.png";
}

public sealed record X2BenchmarkPhaseRow(
    int Phase,
    string Name,
    string CommandsSummary,
    string CardPerCommandText,
    string WaitPerCommandText,
    string SendPerCommandText,
    string PhaseTimeText,
    int Frames,
    double HostMsPerFrame)
{
    public string PhaseLabel => $"{Phase} {Name}";
    public string FramesText => Frames.ToString(CultureInfo.InvariantCulture);
    public string HostMsText => HostMsPerFrame.ToString("0.000", CultureInfo.InvariantCulture);
}

public sealed record X2BenchmarkCommandRow(
    byte Opcode,
    string Command,
    int Count,
    string CardMeanText,
    string WaitMeanText,
    string WaitMaxText,
    string SendMeanText)
{
    public string CountText => Count.ToString(CultureInfo.InvariantCulture);
}

public sealed class X2BenchmarkResult
{
    private X2BenchmarkResult(
        bool completed,
        int? incompletePhaseNumber,
        string? incompletePhaseName,
        string? failureMessage,
        ImmutableArray<X2BenchmarkPhaseScreenshot> phases,
        ImmutableArray<X2BenchmarkCommandSample> commands,
        ImmutableArray<X2BenchmarkFrameSample> frameTimes,
        ImmutableArray<X2BenchmarkPhaseRow> phaseRows,
        ImmutableArray<X2BenchmarkCommandRow> commandRows,
        int totalFrames)
    {
        Completed = completed;
        IncompletePhaseNumber = incompletePhaseNumber;
        IncompletePhaseName = incompletePhaseName;
        FailureMessage = failureMessage;
        Phases = phases;
        Commands = commands;
        FrameTimes = frameTimes;
        PhaseRows = phaseRows;
        CommandRows = commandRows;
        TotalFrames = totalFrames;
        MedianHostMsPerFrame = frameTimes.IsEmpty ? 0 : X2BenchmarkRunner.Median(frameTimes.Select(f => f.HostMilliseconds));
    }

    public bool Completed { get; }
    public int? IncompletePhaseNumber { get; }
    public string? IncompletePhaseName { get; }
    public string? IncompletePhaseLabel => IncompletePhaseNumber is { } num && IncompletePhaseName is { } name
        ? $"{num} {name}"
        : null;
    public string? FailureMessage { get; }
    public ImmutableArray<X2BenchmarkPhaseScreenshot> Phases { get; }
    public ImmutableArray<X2BenchmarkCommandSample> Commands { get; }
    public ImmutableArray<X2BenchmarkFrameSample> FrameTimes { get; }
    public ImmutableArray<X2BenchmarkPhaseRow> PhaseRows { get; }
    public ImmutableArray<X2BenchmarkCommandRow> CommandRows { get; }
    public int TotalFrames { get; }
    public double MedianHostMsPerFrame { get; }

    public static X2BenchmarkResult CreateCompleted(
        ImmutableArray<X2BenchmarkPhaseScreenshot> phases,
        ImmutableArray<X2BenchmarkCommandSample> commands,
        ImmutableArray<X2BenchmarkFrameSample> frameTimes,
        int totalFrames)
    {
        var phaseRows = ImmutableArray.CreateBuilder<X2BenchmarkPhaseRow>(phases.Length);
        foreach (var shot in phases)
        {
            var cmds = commands.Where(c => c.Phase == shot.Phase).ToArray();
            if (cmds.Length == 0)
            {
                return CreateIncomplete(
                    shot.Phase,
                    shot.Name,
                    $"Phase {shot.Phase} ({shot.Name}) recorded no X2 commands.",
                    phases,
                    commands,
                    frameTimes,
                    totalFrames);
            }
            var kinds = string.Join(", ",
                cmds.GroupBy(c => c.Opcode)
                    .OrderBy(g => g.Key)
                    .Select(g => $"{X2BenchmarkRunner.OpcodeName(g.Key)} x{g.Count()}"));
            var host = frameTimes.Where(f => f.Phase == shot.Phase).Select(f => f.HostMilliseconds).Order().ToArray();
            var phaseTimeTicks = cmds.Max(c => c.Done) - cmds.Min(c => c.Begin);
            phaseRows.Add(new X2BenchmarkPhaseRow(
                shot.Phase,
                shot.Name,
                kinds,
                X2BenchmarkRunner.FormatRangeTStates(cmds.Select(c => (double)c.CardTicks)),
                X2BenchmarkRunner.FormatRangeTStates(cmds.Select(c => (double)c.WaitTicks)),
                X2BenchmarkRunner.FormatRangeTStates(cmds.Select(c => (double)c.SendTicks)),
                X2BenchmarkRunner.FormatTStates(phaseTimeTicks),
                shot.Frames,
                host.Length == 0 ? 0 : host[host.Length / 2]));
        }

        var commandRows = ImmutableArray.CreateBuilder<X2BenchmarkCommandRow>();
        foreach (var group in commands.GroupBy(c => c.Opcode).OrderBy(g => g.Key))
        {
            commandRows.Add(new X2BenchmarkCommandRow(
                group.Key,
                X2BenchmarkRunner.OpcodeName(group.Key),
                group.Count(),
                X2BenchmarkRunner.FormatTStates(group.Average(c => c.CardTicks)),
                X2BenchmarkRunner.FormatTStates(group.Average(c => c.WaitTicks)),
                X2BenchmarkRunner.FormatTStates(group.Max(c => c.WaitTicks)),
                X2BenchmarkRunner.FormatTStates(group.Average(c => c.SendTicks))));
        }

        return new X2BenchmarkResult(
            completed: true,
            incompletePhaseNumber: null,
            incompletePhaseName: null,
            failureMessage: null,
            phases,
            commands,
            frameTimes,
            phaseRows.ToImmutable(),
            commandRows.ToImmutable(),
            totalFrames);
    }

    public static X2BenchmarkResult CreateIncomplete(
        int phaseNumber,
        string phaseName,
        string failureMessage,
        ImmutableArray<X2BenchmarkPhaseScreenshot> phases,
        ImmutableArray<X2BenchmarkCommandSample> commands,
        ImmutableArray<X2BenchmarkFrameSample> frameTimes,
        int totalFrames)
        => new(
            completed: false,
            incompletePhaseNumber: phaseNumber,
            incompletePhaseName: phaseName,
            failureMessage: failureMessage,
            phases,
            commands,
            frameTimes,
            [],
            [],
            totalFrames);

    public string FormatCommandsTsv()
    {
        if (Commands.IsEmpty)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var cmd in Commands)
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"{cmd.Phase} {cmd.CommandName} {cmd.Work} {cmd.SendTicks} {cmd.WaitTicks} {cmd.TotalTicks}\n"));
        return sb.ToString();
    }

    public string FormatMarkdownReport()
    {
        var text = new StringBuilder();
        if (!Completed)
        {
            text.AppendLine($"Incomplete X2 benchmark: {FailureMessage}");
            return text.ToString().ReplaceLineEndings("\n");
        }
        text.AppendLine("Times are Z80 T-states (3.546895 MHz; 5 master ticks each). Card = the card's modelled busy time per command (ExpansionX2Engine: 1 master tick per pixel touched, 32 per glyph). Wait = from the go to the first status poll that found the card idle, as the card's ports saw it: the main CPU's wait. Send = from opening the frame to the go: the main CPU's cost to pass the frame through the ports. Host = the emulator's wall time per emulated frame, median.");
        text.AppendLine();
        text.AppendLine("| Phase | Commands | Card per command | Main CPU wait per command | Send per command | Phase time | Frames | Host ms/frame |");
        text.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var row in PhaseRows)
        {
            text.AppendLine($"| {row.PhaseLabel} | {row.CommandsSummary} | {row.CardPerCommandText} | {row.WaitPerCommandText} | {row.SendPerCommandText} | {row.PhaseTimeText} | {row.FramesText} | {row.HostMsText} |");
        }
        text.AppendLine();
        text.AppendLine("| Command | Count | Card (mean) | Main CPU wait (mean) | Wait max | Send (mean) |");
        text.AppendLine("|---|---|---|---|---|---|");
        foreach (var row in CommandRows)
        {
            text.AppendLine($"| {row.Command} | {row.CountText} | {row.CardMeanText} | {row.WaitMeanText} | {row.WaitMaxText} | {row.SendMeanText} |");
        }
        text.AppendLine();
#if DEBUG
        const string build = "Debug";
#else
        const string build = "Release";
#endif
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{Commands.Length} commands, {TotalFrames} emulated frames; host median {MedianHostMsPerFrame:0.000} ms/frame (an emulated frame is 20.06 ms); {build} build on {Environment.MachineName}, {Environment.ProcessorCount} logical CPUs."));
        return text.ToString().ReplaceLineEndings("\n");
    }

    public string FormatJsonReport()
    {
        var payload = new
        {
            ticksPerTState = X2BenchmarkRunner.TicksPerTState,
            phases = Phases.Select(s => new
            {
                phase = s.Phase,
                name = s.Name,
                frames = s.Frames,
                hostMsPerFrame = FrameTimes.Where(f => f.Phase == s.Phase).Select(f => f.HostMilliseconds).ToArray(),
            }),
            commands = Commands.Select(c => new
            {
                c.Phase,
                command = c.CommandName,
                c.Work,
                cardTicks = c.CardTicks,
                sendTicks = c.SendTicks,
                waitTicks = c.WaitTicks,
                totalTicks = c.TotalTicks,
            }),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>Writes the numbers (<c>commands.txt</c>, <c>benchmark.md</c>, <c>benchmark.json</c>)
    /// and the eight VGA phase screenshots (<c>phase-1-palette-vga.png</c> .. <c>phase-8-done-vga.png</c>)
    /// to <paramref name="directory"/>.</summary>
    public IReadOnlyList<string> SaveToDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Completed)
            throw new InvalidOperationException("Cannot save a complete benchmark table from an incomplete benchmark run.");

        Directory.CreateDirectory(directory);
        var written = new List<string>(Phases.Length + 3);
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        foreach (var shot in Phases)
        {
            var pngPath = Path.Combine(directory, shot.VgaFileName);
            File.WriteAllBytes(pngPath, X2BenchmarkRunner.EncodeRgbPng(shot.Vga));
            written.Add(pngPath);
        }

        var commandsPath = Path.Combine(directory, "commands.txt");
        File.WriteAllText(commandsPath, FormatCommandsTsv(), utf8NoBom);
        written.Add(commandsPath);

        var markdownPath = Path.Combine(directory, "benchmark.md");
        File.WriteAllText(markdownPath, FormatMarkdownReport(), utf8NoBom);
        written.Add(markdownPath);

        var jsonPath = Path.Combine(directory, "benchmark.json");
        File.WriteAllText(jsonPath, FormatJsonReport(), utf8NoBom);
        written.Add(jsonPath);

        return written;
    }
}
