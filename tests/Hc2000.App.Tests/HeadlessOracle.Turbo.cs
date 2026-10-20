using System.Text;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.App.Tests;

public sealed partial class HeadlessOracle
{
    // Source map and conditional scope on hc2000-cpm-title-turbo-proof, BEFORE execution.
    // Same I04/whole-chip asset gate as MBASIC; no alternate runner or shared dispatch.
    [WholeChipMbasicFact]
    public Task OriginalTurboOnI04WholeChipCompilesAndRunsSameImageHello() =>
        CpmWarmBoot(prove: ProveOriginalTurbo);

    private async Task ProveOriginalTurbo(OracleContext ctx, string diskPath)
    {
        var original = await File.ReadAllBytesAsync(diskPath);
        Assert.Equal("ff5a1c541c8aa80778a0f44d3ece398013084aafd9be1d3f32d2f68b774bb008", Hash(original));
        var romPath = Path.Combine(RomSelectionFixture.RepositoryRoot(), ProfileRomSelection.WholeChipSourcePath);
        var rom = await File.ReadAllBytesAsync(romPath);
        Assert.Equal(65536, rom.Length);
        Assert.Equal("83e3213b049c5b849ee32e04da901eba06992d5aa1f44f76564f312c79748d2b", Hash(rom));
        var before = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.ExportDisk))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(before.Error);
        Assert.Equal(0, before.A.DirtySectors);
        var disk = Dsk.Load(before.Export.AsSpan());
        var fs = CpmFs.Open(disk, "cpm-hc");
        var logical = ReadLogical(disk, fs.Parameters);
        Assert.Equal(ReadLogical(Dsk.Load(original), fs.Parameters), logical);
        var identities = new[]
        {
            (Name: "TURBO.COM", Size: 28160, Sha: "85f70a54000e72ac30363d075de50fab6fdce27cf3e983779dd481816ba7ae17"),
            (Name: "HELLO.PAS", Size: 128, Sha: "c19e41853e98293f343b61ba95e10ae1895ce3391f1ac667f1d2a45c00fca683"),
            (Name: "TURBO.OVR", Size: 896, Sha: "c48c6335b488be2c8043dcecb81872ad85b29bbe51a0786e7d597c53f5fdfac7"),
            (Name: "TURBOMSG.OVR", Size: 1408, Sha: "5b19ba411ade267cc05e85884909d0609ebbf2cb798209ce54df67538ea0239c")
        };
        foreach (var item in identities)
        {
            var bytes = fs.Get(item.Name, 0);
            Assert.Equal(item.Size, bytes.Length);
            Assert.Equal(item.Sha, Hash(bytes));
            output.WriteLine($"I04:user0/{item.Name}; bytes={bytes.Length}; sha256={Hash(bytes)}");
        }
        Assert.DoesNotContain(fs.List(), f => f.User == 0 && f.Name == "HELLO.COM");
        var sample = fs.Get("HELLO.PAS", 0);
        Assert.Equal(60, Array.IndexOf(sample, (byte)0x1a));
        Assert.Equal("program cucu;\r\n\r\nbegin\r\n  writeln ('Hello, world!');\r\n  end.", Encoding.ASCII.GetString(sample, 0, 60));
        Assert.All(sample.Skip(60), value => Assert.Equal((byte)0x1a, value));
        // Original EOF/copy/CR/end-pointer instructions define [S,E), not raw disk equality.
        // FILE20E2-20E9 excludes 1A; FILE2089-208F appends CR and stores the exclusive end.
        var expectedLogicalSource = sample.Take(60).Append((byte)0x0d).ToArray();
        var program = fs.Get("TURBO.COM", 0);
        const string marker = "HC 91 VERS > 4.4";
        Assert.Equal(marker, Encoding.ASCII.GetString(program, 0x24, marker.Length));
        const string banner = "TURBO Pascal version 1.00 [CP/M-80, Z80]";
        Assert.Equal(banner, Encoding.ASCII.GetString(program, 0x1cd1, banner.Length));
        output.WriteLine("REFERENCE GAP: held HC91-identifying TP1.00 binary/same-image overlays, not documented overlay ABI or real-HC transcript. No supplied Pascal manual. Independent literal oracle is one unconditional writeln in held HELLO.PAS; memory compile/run only, no original/generated HELLO.COM.");

        var evidence = Path.Combine(Path.GetTempPath(), "hc2000-turbo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        output.WriteLine($"evidence={evidence}; DB=none; ROM={Hash(rom)}; sourceI04={Hash(original)}");
        while (ctx.Trace.Console.TryDequeue(out _)) { }
        string Text() => ConsoleText(ctx).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        // One-changed observation diagnostic (tl-one-changed-observation-diagnostic-20261014,
        // po-observation-proposal-629056): qualified state/delta/PC observations only —
        // never byte-fetch/read/writer identity, causality or title PASS.
        ushort Peek16(ushort address) =>
            (ushort)(ctx.Memory.Read(address) | (ctx.Memory.Read((ushort)(address + 1)) << 8));
        byte[] PeekBytes(ushort start, int length) =>
            Enumerable.Range(0, length).Select(i => ctx.Memory.Read((ushort)(start + i))).ToArray();
        // Original-byte table: raw-caption-derivation-before-edit-20261013 on the item.
        // High caption initial -> ordinary tail emits ESC j; match raw, never strip it.
        static bool MainMenu(string text)
        {
            var work = text.IndexOf("W\u001bjork file:", StringComparison.Ordinal);
            var compile = text.IndexOf("C\u001bjompile", StringComparison.Ordinal);
            var run = text.IndexOf("R\u001bjun", StringComparison.Ordinal);
            var quit = text.IndexOf("Q\u001bjuit  compiler", StringComparison.Ordinal);
            return work >= 0 && compile > work && run > compile && quit > run
                && text.TrimEnd().EndsWith(">", StringComparison.Ordinal);
        }
        static bool Options(string text)
        {
            var memory = text.IndexOf("M\u001bjemory", StringComparison.Ordinal);
            var com = text.IndexOf("C\u001bjom-file", StringComparison.Ordinal);
            var find = text.IndexOf("F\u001bjind run-time error", StringComparison.Ordinal);
            var quit = text.IndexOf("Q\u001bjuit", StringComparison.Ordinal);
            return memory >= 0 && com > memory && find > com && quit > find
                && text.TrimEnd().EndsWith(">", StringComparison.Ordinal);
        }
        VideoFrame? last = null;

        // Title-local observation only: reuses the existing InputController/physical paste.
        // No menu translator, compiler, input fallback, ROM injection or new boot runner.
        async Task<string> Stage(string input, string expected, Func<string, bool> predicate, Action<ushort>? samplePc = null)
        {
            var start = Text().Length;
            const long maxStageTStates = 2000L * 71136L;
            var startTStates = ctx.Cpu.TStates;
            var lastTStates = startTStates;
            var stallDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            var masterStart = ctx.Worker.TakeLatestFrame()?.MasterTick ?? -1;
            long latestTick = -1;
            var frames = 0;
            long lastPcSampleTick = masterStart;
            output.WriteLine($"input={input.Replace("\n", "<CR>", StringComparison.Ordinal)}; expected={expected}; bound=40s/2000frames including paste; cadence=16frames/edge");
            ctx.Queue.Enqueue(new PasteText(input, 16 * 355680L));
            while (ctx.Cpu.TStates - startTStates < maxStageTStates && DateTime.UtcNow < stallDeadline && frames < 2000)
            {
                var nowTStates = ctx.Cpu.TStates;
                if (nowTStates > lastTStates)
                {
                    lastTStates = nowTStates;
                    stallDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                }
                if (ctx.Worker.TakeLatestFrame() is { } frame && frame.MasterTick > latestTick)
                {
                    last = frame;
                    latestTick = frame.MasterTick;
                    frames++;
                    if (masterStart < 0) masterStart = lastPcSampleTick = frame.MasterTick;
                    if (samplePc is not null && frame.MasterTick - lastPcSampleTick >= 16 * 355680L)
                    {
                        lastPcSampleTick = frame.MasterTick;
                        samplePc(ctx.Cpu.R.PC);
                    }
                    if (frame.MasterTick - masterStart >= 2000 * 355680L) break;
                }
                if (ctx.Worker.Completion.IsCompleted) await ctx.Worker.Completion;
                Assert.True(ctx.Trace.Console.Count < 8192, "Bounded TURBO CONOUT observation overflow");
                var seen = Text()[start..];
                foreach (var refusal in new[] { "File not found", "*** Disk", "Error ", "Run-time error", "Program aborted" })
                    Assert.DoesNotContain(refusal, seen, StringComparison.Ordinal);
                if (predicate(seen))
                {
                    output.WriteLine($"framesObserved={frames}; seen:\n{seen}");
                    return seen;
                }
                await Task.Delay(5);
            }
            Assert.Fail($"Incomplete TURBO stage: {expected}; PC={ctx.Cpu.R.PC:X4}; framesObserved={frames}; seen:\n{Text()[start..]}\nUnsupported-input/termination gap: stop, never inject an invented wait key.");
            return "";
        }

        try
        {
            var launch = await Stage("TURBO\n", "pinned banner + Include error messages (Y/N)?",
                text => text.Contains(banner, StringComparison.Ordinal)
                    && text.Contains("Include error messages", StringComparison.Ordinal)
                    && text.TrimEnd().EndsWith("(Y/N)?", StringComparison.Ordinal));
            Assert.Contains(banner, launch);
            await Stage("Y", "identified main menu", MainMenu); // binary guest291F accepts Y/N
            await Stage("W", "Work file name prompt", text => text.Contains("Work file name", StringComparison.Ordinal)
                && text.TrimEnd().EndsWith(":", StringComparison.Ordinal));
            ushort wS = 0;
            int wLen = 0;
            byte[]? wPayload = null;
            bool wReturned = false;
            try
            {
                // PO functional-witness amendment: this is a content candidate, not W-path readiness.
                // Newly dispatched O/M/Q below supplies the separate supported-command witness.
                await Stage("HELLO.PAS\n", "same-image logical source [S,E); later dispatch readiness still required",
                    text =>
                    {
                        ushort start = Peek16(0x3E6E), end = Peek16(0x3E70);
                        return text.Contains("Loading", StringComparison.Ordinal)
                            && text.Contains("HELLO.PAS", StringComparison.Ordinal)
                            && text.TrimEnd().EndsWith(">", StringComparison.Ordinal)
                            && start > 0 && end > start && end - start == expectedLogicalSource.Length
                            && PeekBytes(start, end - start).SequenceEqual(expectedLogicalSource);
                    });
                wReturned = true;
            }
            finally
            {
                // Capture even at the frozen predicate's stop; not proof of load or readiness.
                wS = Peek16(0x3E6E);
                ushort wE = Peek16(0x3E70), wTok = Peek16(0x3E49);
                wLen = (int)wE + 1 - wS;
                output.WriteLine($"OBS w-checkpoint: predicateReturned={wReturned}; PC={ctx.Cpu.R.PC:X4}; 3E6E={wS:X4} 3E70={wE:X4} 3E49={wTok:X4} spanE+1-S={wLen}; non-atomic host snapshot, not read/fetch/causal evidence");
                if (wLen is > 0 and <= 512 && wS + wLen <= 0x10000)
                {
                    wPayload = PeekBytes(wS, wLen);
                    var heldSample = fs.Get("HELLO.PAS", 0);
                    output.WriteLine($"OBS w-payload({wLen}): {Convert.ToHexString(wPayload)}; sha256={Hash(wPayload)}; heldSampleBytes={heldSample.Length}; exactHeldSample={wPayload.SequenceEqual(heldSample)} (raw only; no padding/EOF normalization)");
                }
                else output.WriteLine($"OBS w-payload: unavailable (range guard: S={wS:X4} E={wE:X4})");
            }
            Assert.NotNull(wPayload);
            Assert.Equal(expectedLogicalSource.Length + 1, wLen); // raw diagnostic includes byte AT E
            Assert.Equal(expectedLogicalSource, wPayload.Take(wLen - 1).ToArray());
            output.WriteLine("content=[S,E) matches independently defined61-byte source+CR; raw62/held128 remain unequal; non-atomic content observation, not W branch identity");
            var pre8000 = Enumerable.Range(0, 64).Select(i => ctx.Memory.Read((ushort)(0x8000 + i))).ToArray();
            output.WriteLine($"OBS pre8000 sha256={Hash(pre8000)}; {Convert.ToHexString(pre8000)}");
            await Stage("O", "identified options menu", Options);
            await Stage("M", "explicit memory mode selected in options",
                text => Options(text) && text.Contains("compile -> \u001bkM\u001bjemory", StringComparison.Ordinal));
            await Stage("Q", "return from options to identified main menu", MainMenu);
            output.WriteLine("readiness=new O/M/Q supported dispatch responses observed; not proof of W success branch; C may reload via original load-if-needed behavior");
            // Original FILE249D-24A2 stamps success and RETs to the ordinary dispatcher,
            // not full-menu redraw; Stage supplies only this newly emitted command suffix.
            var compile = await Stage("C", "fresh Compiling/lines/Code/Data report and ordinary dispatcher prompt",
                text => System.Text.RegularExpressions.Regex.IsMatch(text,
                    @"Compiling\b[\s\S]*?\b\d+\s+lines\b[\s\S]*?Code:\s*\d+\s+bytes\b[\s\S]*?Data:\s*\d+\s+bytes\b[\s\S]*?>\s*\z",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant));
            Assert.DoesNotContain("Error ", compile, StringComparison.Ordinal);
            Assert.DoesNotContain("Program aborted", compile, StringComparison.Ordinal);
            // O1 checkpoint 2 (post-C): qualified pointer/cell state observation.
            int codeN = -1;
            byte[]? post8000Snap = null;
            ushort c04 = Peek16(0x6F04), c06 = Peek16(0x6F06), c08 = Peek16(0x6F08), c0A = Peek16(0x6F0A), cTok = Peek16(0x3E49), cS = Peek16(0x3E6E), cE = Peek16(0x3E70);
            int cLen = (int)cE + 1 - cS;
            output.WriteLine($"OBS c-checkpoint: 6F04={c04:X4} 6F06={c06:X4} 6F08={c08:X4} 6F0A={c0A:X4} 3E49={cTok:X4} 3E6E={cS:X4} 3E70={cE:X4} (E+1={((ushort)(cE + 1)):X4})");
            if (wPayload is not null && cLen == wLen && cS == wS && cS + cLen <= 0x10000)
            {
                byte[] cPayload = PeekBytes(cS, cLen);
                output.WriteLine($"OBS c-payload-identity vs post-W: {Convert.ToHexString(cPayload)} same={cPayload.SequenceEqual(wPayload)}");
            }
            else output.WriteLine($"OBS c-payload-identity: unavailable (S={cS:X4} E={cE:X4} wLen={wLen})");
            // O2 post-C delta limited by newly observed stage Code size.
            var codeM = System.Text.RegularExpressions.Regex.Match(compile, @"Code:\s*(\d+)");
            if (codeM.Success && int.TryParse(codeM.Groups[1].Value, out int n2) && n2 is > 0 and <= 0x8000)
            {
                codeN = n2;
                post8000Snap = Enumerable.Range(0, Math.Min(n2, 256)).Select(i => ctx.Memory.Read((ushort)(0x8000 + i))).ToArray();
                int d = 0;
                int lim = Math.Min(64, n2);
                for (var k = 0; k < lim; k++) if (post8000Snap[k] != pre8000[k]) d++;
                output.WriteLine($"OBS post8000 N={n2} sha256={Hash(post8000Snap)}; first64-delta-vs-pre8000={d}");
            }
            else output.WriteLine($"OBS post8000: Code: size unparsed -> skip (qualified unavailable)");
            // After R observe only. Successful literal alone is NOT completion. No guessed Enter.
            var sampledPcs = new List<ushort>();
            string run;
            try
            {
                run = await Stage("R", "fresh Running, held literal and ordinary dispatcher return prompt",
                    text => System.Text.RegularExpressions.Regex.IsMatch(text,
                        @"Running\b[\s\S]*?\nHello, world!\n[\s\S]*?>\s*\z",
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                    pc => { if (sampledPcs.Count < 128) sampledPcs.Add(pc); });
            }
            finally
            {
                output.WriteLine($"OBS r-sparse-PC: count={sampledPcs.Count}; cap128; cadence>=16 guest frames; {string.Join(",", sampledPcs.Select(pc => pc.ToString("X4")))}; non-atomic host samples, not instruction fetches or completed execution");
            }
            Assert.Single(run.Split('\n'), line => line == "Hello, world!");
            Assert.DoesNotContain("Run-time error", run, StringComparison.Ordinal);
            Assert.DoesNotContain("Program aborted", run, StringComparison.Ordinal);
            byte[]? post8000After = null;
            if (codeN > 0 && post8000Snap is not null)
            {
                post8000After = Enumerable.Range(0, Math.Min(codeN, 256)).Select(i => ctx.Memory.Read((ushort)(0x8000 + i))).ToArray();
                int d2 = 0;
                for (var k = 0; k < Math.Min(64, codeN); k++) if (post8000After[k] != post8000Snap[k]) d2++;
                output.WriteLine($"OBS r-post8000 delta-vs-postC={d2} (qualified; no writer-identity claim)");
            }
            output.WriteLine($"OBS r-checkpoint: 6F04={Peek16(0x6F04):X4} (return dispatch cell; logged, not asserted)");
            var warmBefore = Volatile.Read(ref ctx.Trace.ZeroVectorEntries);
            await Stage("Q", "binary-supported dispatcher Q warm start and A>",
                text => text.TrimEnd().EndsWith("A>", StringComparison.Ordinal)
                    && Volatile.Read(ref ctx.Trace.ZeroVectorEntries) > warmBefore);
            await WaitFramesKeeping(ctx.Worker, 30, frame => last = frame);
            Assert.NotNull(last);
            Assert.True(FrameShowsAPrompt(last!), "Final A> must persist on the physical frame");
            Assert.EndsWith("A>", Text().TrimEnd());
            output.WriteLine("why=held unconditional writeln literal exactly once; compiled in memory; source-backed ordinary return followed by supported Q warm termination and persistent physical A>. No physical/installed/all-title parity inference.");
        }
        finally
        {
            File.WriteAllText(Path.Combine(evidence, "console.txt"), ConsoleText(ctx));
            Assert.Equal(original, await File.ReadAllBytesAsync(diskPath));
            Assert.Equal(rom, await File.ReadAllBytesAsync(romPath));
            var after = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.ExportDisk))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(after.Error);
            File.WriteAllBytes(Path.Combine(evidence, "post-a.dsk"), after.Export.ToArray());
            Assert.Equal(0, after.A.DirtySectors);
            var post = Dsk.Load(after.Export.AsSpan());
            Assert.Equal(logical, ReadLogical(post, fs.Parameters));
            var postFs = CpmFs.Open(post, "cpm-hc");
            foreach (var item in identities) Assert.Equal(fs.Get(item.Name, 0), postFs.Get(item.Name, 0));
            Assert.DoesNotContain(postFs.List(), f => f.User == 0 && f.Name == "HELLO.COM");
            output.WriteLine($"preservation=source ROM/media and full private logical image unchanged including directory/attributes/EOF tail; dirty0; logicalSHA256={Hash(logical)}; no scratch COM created; evidence retained");
        }
    }
}
