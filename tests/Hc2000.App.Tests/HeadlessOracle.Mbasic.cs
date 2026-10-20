using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.App.Tests;

public sealed partial class HeadlessOracle
{
    [WholeChipMbasicFact]
    public Task OriginalMbasicOnI04WholeChipRunsSameImageSqrAndReturns() => CpmWarmBoot(proveMbasic: true);

    // Pinned SQR.BAS line10: inclusive0..100; line50 conditionally suppresses
    // exact-zero errors. Therefore <=101 rows, NOT an expected57-row set.
    internal const int MbasicSourceIterationCount = 101;
    internal const double MbasicFunctionalTolerance = 0.0001;
    private const string MbasicNumber = @"[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[Ee][+-]?\d+)?";

    // Index only: reuse the existing raw queue, independently of normalized parser text.
    internal readonly struct MbasicRawSlice(ConcurrentQueue<byte> console)
    {
        internal int Start { get; } = console.Count;
        internal ReadOnlySpan<byte> From(byte[] raw) =>
            Start <= raw.Length ? raw.AsSpan(Start) : ReadOnlySpan<byte>.Empty;
    }

    private async Task ProveOriginalMbasic(OracleContext ctx, string diskPath)
    {
        // Oracle pinned BEFORE execution: item source-oracle-before-tests.
        // mbasic.pdf PDF137/D.1: MBASIC filename = RUN filename, default .BAS.
        // PDF140/D.7: SYSTEM (not Ctrl-C) returns to CP/M via warm start.
        // PDF49/2.22 and PDF125/3.36: NEXT increments before testing; SQR is sqrt.
        // For n=0..100: integer n*n<=10000, sqrt(n*n)=n, ideal E=EMAX=0.
        // 1e-4 is a conservative preselected functional tolerance, not an exact
        // Rev5.2 transcript/error-bound claim. No emulator result defines it.
        const double tolerance = MbasicFunctionalTolerance;
        var original = await File.ReadAllBytesAsync(diskPath);
        var sourceHash = Hash(original);
        Assert.Equal("ff5a1c541c8aa80778a0f44d3ece398013084aafd9be1d3f32d2f68b774bb008", sourceHash);
        var root = RomSelectionFixture.RepositoryRoot();
        var rom = await File.ReadAllBytesAsync(Path.Combine(root, ProfileRomSelection.WholeChipSourcePath));
        Assert.Equal(65536, rom.Length);
        Assert.Equal("83e3213b049c5b849ee32e04da901eba06992d5aa1f44f76564f312c79748d2b", Hash(rom));
        var before = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.ExportDisk)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(before.Error);
        Assert.Equal(0, before.A.DirtySectors);
        var disk = Dsk.Load(before.Export.AsSpan());
        var fs = CpmFs.Open(disk, "cpm-hc");
        var interpreter = fs.Get("MBASIC.COM", 0);
        var sample = fs.Get("SQR.BAS", 0);
        Assert.Equal(24320, interpreter.Length);
        Assert.Equal("e6412bb1d8fcb2638b169dcde79741a4346975dae2de46272ce394d233ee2ada", Hash(interpreter));
        Assert.Equal(256, sample.Length);
        Assert.Equal("c3613ec7e8b6b35e84ecbd8c27643273bef7179cc7f0bcc2c90ccb7fdbbc84d5", Hash(sample));
        var logical = ReadLogical(disk, fs.Parameters);
        Assert.Equal(ReadLogical(Dsk.Load(original), fs.Parameters), logical);
        output.WriteLine($"I04 image={sourceHash}; whole M27C512={Hash(rom)}; MBASIC.COM user0={Hash(interpreter)} bytes={interpreter.Length}; same-image SQR.BAS user0={Hash(sample)} bytes={sample.Length}; CP/M EOF offset={Array.IndexOf(sample, (byte)0x1a)}; DB=none");
        output.WriteLine("REFERENCE GAP: generic BASIC-80 release5.0+ manual supports semantics, not a real-HC Rev5.2 numeric/text transcript. Banner wording comes from pinned original binary only; no physical/installed/full-epic or other-image PASS.");
        while (ctx.Trace.Console.TryDequeue(out _)) { }
        string Text() => ConsoleText(ctx).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        VideoFrame? finalFrame = null;
        async Task<string> Send(string command, params string[] expected)
        {
            var start = Text().Length;
            var rawSlice = new MbasicRawSlice(ctx.Trace.Console);
            var fetchStart = ctx.Trace.EntryFetches.Count;
            var overflowStart = Volatile.Read(ref ctx.Trace.EntryFetchOverflow);
            output.WriteLine($"input={command.Replace("\n", "<CR>", StringComparison.Ordinal)}; expected={string.Join(" + ", expected)}; bound=40s including paste; cadence=16frames/edge; maxCONOUT=8192");
            ctx.Queue.Enqueue(new PasteText(command, 16 * 355680L));
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(40);
            // DIP HeadlessOracle.cs raw-hex pattern, this Send's new queue suffix only.
            // Labels stay out of the masked string the parser returns.
            void RetainRawAndFetches()
            {
                var raw = ctx.Trace.Console.ToArray();
                var fresh = rawSlice.From(raw);
                output.WriteLine($"raw pre-mask pre-Step CONOUT hex={Convert.ToHexString(fresh)}; bytes={fresh.Length}; queue={raw.Length}; sliceStart={rawSlice.Start}; sliceMatchesQueue={rawSlice.Start <= raw.Length}; not masked ConsoleText; not completed output");
                var fetches = ctx.Trace.EntryFetches.ToArray();
                var from = Math.Min(fetchStart, fetches.Length);
                var overflow = Volatile.Read(ref ctx.Trace.EntryFetchOverflow);
                output.WriteLine($"entry-fetch records since Send start={fetches.Length - from}; index={from}..{fetches.Length}; overflowDelta={overflow - overflowStart}; totalOverflow={overflow}; entry fetch is not completed CONOUT, glyph, or malformed-token cause");
                for (var i = from; i < fetches.Length; i++)
                {
                    var record = fetches[i];
                    output.WriteLine($"entry-fetch[{i}] C={record.C:X2} tstates={record.CpuTStates} opcode={record.Opcode:X2} mapping={record.Mapping.Store}:{record.Mapping.Offset}:writable={record.Mapping.Writable}");
                }
            }
            while (DateTime.UtcNow < until)
            {
                if (ctx.Worker.TakeLatestFrame() is { } frame) finalFrame = frame;
                if (ctx.Worker.Completion.IsCompleted) await ctx.Worker.Completion;
                Assert.True(ctx.Trace.Console.Count < 8192, "Bounded CONOUT observation overflow");
                var seen = Text()[start..];
                if (expected.All(value => seen.Contains(value, StringComparison.Ordinal)))
                {
                    RetainRawAndFetches();
                    output.WriteLine($"seen:\n{seen}");
                    Assert.DoesNotContain("error", seen, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("Break", seen, StringComparison.Ordinal);
                    return seen;
                }
                await Task.Delay(20);
            }
            RetainRawAndFetches();
            Assert.Fail($"Expected {string.Join(" + ", expected)} within40s; PC={ctx.Cpu.R.PC:X4}; seen:\n{Text()[start..]}");
            return "";
        }

        var run = await Send("MBASIC SQR\n", "emax =", "Ok");
        Assert.Contains("BASIC-80 Rev. 5.2", run); // pinned binary text, NOT real-HC parity
        Assert.Contains("[CP/M Version]", run);
        var parsed = ParseMbasicSample(run);
        output.WriteLine($"sample coverage: sourceIterations={MbasicSourceIterationCount}; conditionalRowBound=0..{MbasicSourceIterationCount}; candidates={parsed.CandidateRows}; parsed={parsed.Rows.Count}; rejected={parsed.RejectedRows}; summary={parsed.Emax}; complete={parsed.Complete}");
        foreach (var error in parsed.Errors) output.WriteLine("REJECTED: " + error);
        // Never let a valid-match collector hide a malformed or unclassified line.
        Assert.Equal(parsed.CandidateRows, parsed.Rows.Count + parsed.RejectedRows);
        Assert.True(parsed.Errors.Count == 0, string.Join('\n', parsed.Errors));
        Assert.True(parsed.Complete);
        Assert.NotNull(parsed.Emax);
        var emax = parsed.Emax ?? throw new FormatException("Missing sample summary");
        var direct = await Send("?X%;P%;S;EMAX\n", "Ok");
        var fields = ParseMbasicDirect(direct);
        Assert.Equal(MbasicSourceIterationCount, fields[0]);
        Assert.Equal(10000, fields[1]);
        Assert.InRange(Math.Abs(fields[2] - 100), 0, tolerance);
        Assert.Equal(emax, fields[3]);
        output.WriteLine($"why=independent integer/square-root oracle; X%=101,P%=10000,S~100; idealEMAX=0,tolerance={tolerance}; observedEMAX={emax},validatedNonzeroRows={parsed.Rows.Count}; literal sample summary and terminal Ok; conditional exact-zero omitted-row identities remain a reference gap");
        var warmBefore = Volatile.Read(ref ctx.Trace.ZeroVectorEntries);
        await Send("SYSTEM\n", "A>");
        Assert.True(Volatile.Read(ref ctx.Trace.ZeroVectorEntries) > warmBefore, "SYSTEM must perform documented CP/M warm start");
        await WaitFramesKeeping(ctx.Worker, 30, frame => finalFrame = frame);
        Assert.NotNull(finalFrame);
        Assert.True(FrameShowsAPrompt(finalFrame!), "SYSTEM must return to persistent physical A> screen");
        Assert.EndsWith("A>", Text().TrimEnd());
        var after = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.ExportDisk)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(after.Error);
        Assert.Equal(0, after.A.DirtySectors);
        var postDisk = Dsk.Load(after.Export.AsSpan());
        Assert.Equal(logical, ReadLogical(postDisk, fs.Parameters));
        var postFs = CpmFs.Open(postDisk, "cpm-hc");
        Assert.Equal(interpreter, postFs.Get("MBASIC.COM", 0));
        Assert.Equal(sample, postFs.Get("SQR.BAS", 0));
        Assert.Equal(original, await File.ReadAllBytesAsync(diskPath));
        Assert.Equal(rom, await File.ReadAllBytesAsync(Path.Combine(root, ProfileRomSelection.WholeChipSourcePath)));
        output.WriteLine($"why=SYSTEM warm vector and physical A> returned; private mounted logicalSHA256={Hash(logical)} preserved, dirty0; original ROM/image/interpreter/sample unchanged including EOF tail");
    }

    internal sealed record MbasicDeviation(int Counter, double Error);
    internal sealed record MbasicSampleTranscript(int CandidateRows, IReadOnlyList<MbasicDeviation> Rows,
        int RejectedRows, double? Emax, bool Complete, IReadOnlyList<string> Errors);

    // Pure leaf parser: no machine, I/O, runner, guest arithmetic, or token repair.
    internal static MbasicSampleTranscript ParseMbasicSample(string text)
    {
        var lines = MbasicLines(text);
        var rows = new List<MbasicDeviation>();
        var errors = new List<string>();
        var candidates = 0;
        var rejected = 0;
        double? emax = null;
        var summaries = 0;
        var completed = false;
        var previous = -1;
        // These strings are pinned original binary/command facts, not a supplied
        // Rev5.2 real-HC transcript. Unexpected banner/echo lines are rejected too.
        string[] header = ["MBASIC SQR", "BASIC-80 Rev. 5.2", "[CP/M Version]",
            "Copyright 1977, 78, 79, 80 (C) by Microsoft", "Created: 14-Jul-80"];
        for (var i = 0; i < header.Length; i++)
            if (i >= lines.Length || lines[i] != header[i])
                errors.Add($"header[{i}] expected '{header[i]}', seen '{(i < lines.Length ? lines[i] : "<missing>")}'");
        if (lines.Length < 6 || !Regex.IsMatch(lines[5], @"^\d+ Bytes free$", RegexOptions.CultureInvariant))
            errors.Add("missing/malformed Bytes free boundary");
        var rawLines = MbasicAllLines(text);
        for (var i = 0; i < rawLines.Length; i++)
            if (rawLines[i].StartsWith("emax =", StringComparison.Ordinal) && (i == 0 || rawLines[i - 1].Length != 0))
                errors.Add("missing source line100 PRINT blank before summary");
        for (var i = 6; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("emax =", StringComparison.Ordinal))
            {
                summaries++;
                if (summaries != 1 || completed) errors.Add($"unexpected summary at line{i}: '{line}'");
                var match = Regex.Match(line, @"^emax =[ \t]*(" + MbasicNumber + @")$", RegexOptions.CultureInvariant);
                if (!match.Success || !MbasicNumeric(match.Groups[1].Value, out var value))
                    errors.Add($"malformed summary at line{i}: '{line}'");
                else
                {
                    emax = value;
                    if (value < 0 || value > MbasicFunctionalTolerance)
                        errors.Add($"summary outside independent bound at line{i}: '{line}'");
                }
                continue;
            }
            if (line == "Ok")
            {
                if (completed || summaries != 1 || emax is null)
                    errors.Add($"premature/duplicate completion at line{i}: '{line}'");
                completed = true;
                continue;
            }
            candidates++;
            var reasons = new List<string>();
            if (summaries > 0 || completed) reasons.Add("data after summary/completion");
            var tokens = Regex.Split(line, @"[ \t]+", RegexOptions.CultureInvariant);
            int counter = -1;
            double error = 0;
            if (tokens.Length != 2) reasons.Add("expected exactly counter/error fields");
            else
            {
                if (!Regex.IsMatch(tokens[0], @"^\d+$", RegexOptions.CultureInvariant) ||
                    !int.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out counter) ||
                    counter < 0 || counter >= MbasicSourceIterationCount)
                    reasons.Add("counter outside source0..100");
                else
                {
                    if (counter <= previous) reasons.Add("duplicate/out-of-order counter");
                    previous = Math.Max(previous, counter);
                }
                if (!MbasicNumeric(tokens[1], out error)) reasons.Add("malformed/nonfinite numeric token");
                else if (Math.Abs(error) < double.Epsilon || Math.Abs(error) > MbasicFunctionalTolerance)
                    reasons.Add("error outside independent nonzero bound");
            }
            if (reasons.Count == 0) rows.Add(new(counter, error));
            else
            {
                rejected++;
                errors.Add($"row line{i} rejected ({string.Join(';', reasons)}): '{line}'");
            }
        }
        if (candidates > MbasicSourceIterationCount) errors.Add("data count exceeds source101 iterations");
        if (summaries != 1 || !completed) errors.Add("missing unique summary/terminal Ok boundary");
        // Additional consistency only; never silently exclude a rejected row.
        if (rejected == 0 && emax is { } summary)
        {
            var maxPrinted = rows.Count == 0 ? 0 : rows.Max(row => Math.Abs(row.Error));
            if (Math.Abs(summary - maxPrinted) > 1e-10) errors.Add("summary disagrees with maximum printed error");
        }
        // No fixed nonzero count/identity is derivable: sample line50 suppresses
        // zero-error rows. Missing nonmaximum rows cannot be proven from this
        // transcript alone; exact version transcript/iteration observation is a gap.
        return new(candidates, rows, rejected, emax,
            summaries == 1 && completed && emax is not null && lines[^1] == "Ok", errors);
    }

    internal static double[] ParseMbasicDirect(string text)
    {
        var lines = MbasicLines(text);
        if (lines.Length != 3 || lines[0] != "?X%;P%;S;EMAX" || lines[2] != "Ok")
            throw new FormatException("Direct readback requires exact echo, one data row, terminal Ok; seen: " + text);
        var tokens = Regex.Split(lines[1], @"[ \t]+", RegexOptions.CultureInvariant);
        if (tokens.Length != 4) throw new FormatException("Direct readback requires four fields: " + lines[1]);
        var values = new double[4];
        for (var i = 0; i < values.Length; i++)
            if (!MbasicNumeric(tokens[i], out values[i]))
                throw new FormatException($"Malformed direct token{i}: '{tokens[i]}'");
        if (values[0] != MbasicSourceIterationCount || values[1] != 10000 ||
            Math.Abs(values[2] - 100) > MbasicFunctionalTolerance ||
            values[3] < 0 || values[3] > MbasicFunctionalTolerance)
            throw new FormatException("Direct readback violates source completion/independent bounds: " + lines[1]);
        return values;
    }

    private static string[] MbasicAllLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n').Split('\n').Select(line => line.Trim(' ', '\t')).ToArray();

    private static string[] MbasicLines(string text) => MbasicAllLines(text)
        .Where(line => line.Length != 0).ToArray(); // only documented PRINT whitespace/blank lines

    private static bool MbasicNumeric(string token, out double value)
    {
        value = 0;
        return Regex.IsMatch(token, "^" + MbasicNumber + "$", RegexOptions.CultureInvariant) &&
            double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }
}

public sealed class WholeChipMbasicFactAttribute : FactAttribute
{
    public WholeChipMbasicFactAttribute([System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null, [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        var root = RomSelectionFixture.RepositoryRoot();
        foreach (var (relative, size) in new[]
        {
            (ProfileRomSelection.WholeChipSourcePath, 65536L),
            ("disks/cpm/CPM22-HC.DSK", 0L)
        })
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path) || (size > 0 && new FileInfo(path).Length != size)
                || (File.Exists(path) && new FileInfo(path).Length < 1024))
                Skip = "Canonical asset absent or not LFS-materialized: " + relative;
        }
    }
}
