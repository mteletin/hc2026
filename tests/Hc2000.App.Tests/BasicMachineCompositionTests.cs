using System.Buffers.Binary;
using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class BasicMachineCompositionTests
{
    [V1RomFact]
    public async Task VerifiedV1SelectionStartsWorkerAndProducesRealFrames()
    {
        var selected = await ProfileRomSelection.SelectAsync("hc2000-v1", RomSelectionFixture.RepositoryRoot());
        using var worker = BasicMachineComposition.CreateWorker(selected, new());

        var frame = await WaitForBootFrame(worker, TimeSpan.FromSeconds(5));

        Assert.Equal(352, frame.Width);
        Assert.Equal(296, frame.Height);
        Assert.True(frame.Pixels.ToArray().Distinct().Count() > 1,
            "The canonical BASIC ROM must render non-blank output through the real worker/machine/ULA path.");
        Assert.False(worker.Completion.IsCompleted);
    }

    [V1RomFact]
    public async Task ComposedPhysicalInputPrintsFourAndFreshResetRepeats()
    {
        var selected = await ProfileRomSelection.SelectAsync("hc2000-v1", RomSelectionFixture.RepositoryRoot());
        var rom = selected.Regions[0].Bytes.ToArray();
        for (var run = 0; run < 2; run++)
        {
            var input = new InputQueue();
            var keyboard = new KeyboardInputAdapter(input) { Mode = KeyMappingMode.Symbolic };
            using var worker = BasicMachineComposition.CreateWorker(selected, input);
            var frame = await WaitForDecodedScreen(worker, rom,
                text => text.Contains("HC2000 ICE FELIX", StringComparison.Ordinal), TimeSpan.FromSeconds(12));
            // v1's acknowledged visible boot identity differs from the v2 READY prompt oracle.
            // Let the real ROM finish boot and poll its keyboard; no CPU, RAM, or ROM state is injected.
            var lastTick = await WaitFrames(worker, 250, frame.MasterTick);

            lastTick = await TypeSymbolicKey(keyboard, Key.P, "p", worker, lastTick);
            lastTick = await TypeSymbolicKey(keyboard, Key.Space, " ", worker, lastTick);
            lastTick = await TypeSymbolicKey(keyboard, Key.D2, "2", worker, lastTick);
            lastTick = await TypeSymbolicKey(keyboard, Key.OemPlus, "+", worker, lastTick);
            lastTick = await TypeSymbolicKey(keyboard, Key.D2, "2", worker, lastTick);
            var entered = await WaitForDecodedScreen(worker, rom,
                text => text.Contains("PRINT", StringComparison.Ordinal)
                    && text.Contains("2+2", StringComparison.Ordinal), TimeSpan.FromSeconds(8));
            var typedLine = DecodeBasicScreen(entered, rom);
            Assert.Contains("PRINT", typedLine);
            Assert.Contains("2+2", typedLine);
            lastTick = Math.Max(lastTick, entered.MasterTick);
            lastTick = await TypeSymbolicKey(keyboard, Key.Enter, null, worker, lastTick);

            var output = await WaitForDecodedScreen(worker, rom,
                text => text.Contains("OK", StringComparison.Ordinal)
                    && text.Split('\n').Any(line => line.Trim() == "4"), TimeSpan.FromSeconds(12));
            var rendered = DecodeBasicScreen(output, rom);
            Assert.Contains(rendered.Split('\n'), line => line.Trim() == "4");

            worker.Dispose();
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [V1RomFact]
    public async Task SymbolicHostPasteOfPrintKeyShowsCommandThenFive()
    {
        var frames = await BootAndPaste("p 2+3\n");

        Assert.Contains(frames, line => line.Contains("PRINT", StringComparison.Ordinal)
            && line.Contains("2+3", StringComparison.Ordinal)
            && !line.Contains("PRINT RINT", StringComparison.Ordinal));
        Assert.Contains(frames, line => line.Split('|').Select(part => part.Trim()).Contains("5")
            && line.Contains("OK", StringComparison.Ordinal));
    }

    [V1RomFact]
    public async Task SymbolicHostPasteOfPrintLettersIsNotTheKeywordCommand()
    {
        var frames = await BootAndPaste("PRINT 2+3\n");

        Assert.Contains(frames, line => line.Contains("PRINT RINT", StringComparison.Ordinal));
        Assert.Contains(frames, line => line.Contains("Variable not found", StringComparison.Ordinal));
        Assert.DoesNotContain(frames, line => line.Split('|').Select(part => part.Trim()).Contains("5"));
    }

    [V1RomFact]
    public async Task FactoryRehashesPubliclyConstructibleSelectionBeforeStartingWorker()
    {
        var selected = await ProfileRomSelection.SelectAsync("hc2000-v1", RomSelectionFixture.RepositoryRoot());
        var first = selected.Regions[0];
        var forged = selected with
        {
            Regions = selected.Regions.SetItem(0, first with { Bytes = System.Collections.Immutable.ImmutableArray.Create(new byte[0x4000]) })
        };

        var error = Assert.Throws<InvalidDataException>(() => BasicMachineComposition.CreateWorker(forged, new()));

        Assert.Contains("identity mismatch", error.Message);
    }

    [V1RomFact]
    public async Task FactoryRejectsIncompleteV2WithoutSubstitution()
    {
        var profile = ProfileRomSelection.Describe("hc2000-v2");
        var regions = profile.RomRegions.Select(region => new SelectedProfileRom(region,
            System.Collections.Immutable.ImmutableArray.CreateRange(new byte[region.Length]), new string('0', 64)))
            .ToImmutableArray();
        var forgedV2 = new SelectedProfileRomSet("hc2000-v2", "/unused", regions);

        var error = Assert.Throws<InvalidDataException>(() => BasicMachineComposition.CreateWorker(forgedV2, new()));

        Assert.Contains("incomplete v2", error.Message);
    }

    [AvaloniaFact]
    public async Task WindowOpenBootsWholeChipAndReachesV2BasicPromptWithIf1()
    {
        Assert.Null(RomSelectionFixture.MissingAssets());
        // Charset for the presented frame only. Off the UI thread: LoadWholeChipV2 blocks on
        // ReadAsync. The machine itself is composed by window open, not PrepareWholeChipV2 here.
        var (_, basic, _, _) = await Task.Run(() =>
            ProfileRomSelection.LoadWholeChipV2(ProfileRomSelection.DefaultAssetRoot()));

        var window = new MainWindow(new LaunchOptions(null, null, true, false));
        window.Show();
        try
        {
            var panel = window.FindControl<ProfileRomPanel>("ProfileRomControls");
            Assert.NotNull(panel);
            Assert.False(panel!.IsVisible);

            var stallDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            const long maxBootTicks = 600L * FrameTiming.FrameTicks;
            long startTick = -1;
            long lastTick = -1;
            var last = "(no frame yet)";
            while (DateTime.UtcNow < stallDeadline)
            {
                if (window.LatestPresentedFrame is { } frame)
                {
                    if (startTick < 0) startTick = frame.MasterTick;
                    if (frame.MasterTick > lastTick)
                    {
                        lastTick = frame.MasterTick;
                        stallDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                    }
                    last = DecodeBasicScreen(frame, basic);
                    // Settled v2 glyph screen (measured: still this line after 3e8 master ticks).
                    // Not the v1 contiguous banner "HC2000 ICE FELIX", and not the console-hook word READY.
                    if (last.Contains("I.C.E.", StringComparison.Ordinal)
                        && last.Contains("HC2000", StringComparison.Ordinal))
                        break;
                    if (frame.MasterTick - startTick >= maxBootTicks)
                        break;
                }
                if (window.ShutdownCompletion.IsCompleted) await window.ShutdownCompletion;
                await Task.Delay(10);
            }

            Assert.Contains("HC2000", last, StringComparison.Ordinal);
            Assert.Contains("I.C.E.", last, StringComparison.Ordinal);
            Assert.DoesNotContain("HC2000 ICE FELIX", last, StringComparison.Ordinal);
            Assert.Equal(0x4000, window.BootedIf1Length);
            Assert.False(window.CurrentRuntime?.Completion.IsCompleted ?? true,
                "Window open must leave the whole-chip machine running. Last screen:\n" + last);
        }
        finally
        {
            window.Close();
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    [Fact]
    public async Task WholeChipV2BootsFaithfulMachineAndReachesBasicPromptWithIf1()
    {
        var assetRoot = RomSelectionFixture.RepositoryRoot();
        var (profile, basic, cpm, if1) = ProfileRomSelection.LoadWholeChipV2(assetRoot);

        // The whole-chip image supplies three 16-KiB slices through the existing paging: main ROM,
        // CP/M half and the IF1 shadow page.
        Assert.Equal(0x4000, basic.Length);
        Assert.Equal(0x4000, cpm.Length);
        Assert.Equal(0x4000, if1.Length);

        var input = new InputQueue();
        var keyboard = new KeyboardInputAdapter(input);
        var prepared = BasicMachineComposition.PrepareWholeChipV2((profile, basic, cpm, if1));
        using (var worker = BasicMachineComposition.CreateWorker(prepared, input))
        {
            // Machine ON at window open: no ROM panel selection, no Validate / no typing.
            var frame = await WaitForDecodedScreen(worker, basic,
                screen => screen.Contains("HC2000", StringComparison.Ordinal),
                TimeSpan.FromSeconds(12));

            var screen = DecodeBasicScreen(frame, basic);
            // The machine is composed from the whole-chip image through the existing paging,
            // with the IF1 shadow page (chip 0x4000..0x8000) mapped as the v2 IF1 image.
            // Reaching the v2 BASIC identity through the real worker/machine/ULA path proves
            // the machine is on, launched without a panel, and that IF1 is present.
            Assert.True(screen.Contains("HC2000", StringComparison.Ordinal),
                "The whole-chip BASIC image renders the v2 identity.");
            // v2-specific rendering ("I.C.E." with its graphic framing), distinct from the v1
            // compact "ICE FELIX" branding that a machine built from v1 slices would show.
            Assert.True(screen.Contains("I.C.E.", StringComparison.Ordinal),
                "The v2 BASIC renders its IF1-influenced identity, not the v1 branding.");
        }
    }

    private static async Task<List<string>> BootAndPaste(string text)
    {
        var selected = await ProfileRomSelection.SelectAsync("hc2000-v1", RomSelectionFixture.RepositoryRoot());
        var rom = selected.Regions[0].Bytes.ToArray();
        var input = new InputQueue();
        var keyboard = new KeyboardInputAdapter(input);
        using var worker = BasicMachineComposition.CreateWorker(selected, input);
        var frame = await WaitForDecodedScreen(worker, rom,
            screen => screen.Contains("HC2000 ICE FELIX", StringComparison.Ordinal), TimeSpan.FromSeconds(12));
        var pasteStartTick = await WaitFrames(worker, 250, frame.MasterTick);
        Assert.True(keyboard.Paste(text));
        var seen = new List<string>();
        var minPasteTicks = (text.Length * 16L + 80L) * FrameTiming.FrameTicks;
        var maxPasteTicks = (text.Length * 16L + 600L) * FrameTiming.FrameTicks;
        var latestTick = pasteStartTick;
        var stallDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var last = "";
        while (latestTick - pasteStartTick < maxPasteTicks && DateTime.UtcNow < stallDeadline)
        {
            if (worker.TakeLatestFrame() is { } later && later.MasterTick > latestTick)
            {
                latestTick = later.MasterTick;
                stallDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                var interesting = string.Join(" | ", DecodeBasicScreen(later, rom).Split('\n')
                    .Select(line => line.TrimEnd())
                    .Where(line => line.Trim().Length > 0));
                if (interesting.Length > 0 && interesting != last)
                {
                    seen.Add(interesting);
                    last = interesting;
                }
                if (latestTick - pasteStartTick >= minPasteTicks
                    && (last.Contains("OK", StringComparison.Ordinal)
                        || last.Contains("Variable not found", StringComparison.Ordinal)))
                {
                    break;
                }
            }
            else
            {
                if (worker.Completion.IsCompleted) await worker.Completion;
                await Task.Delay(1);
            }
        }
        worker.Dispose();
        await worker.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        return seen;
    }

    private static async Task<long> TypeSymbolicKey(KeyboardInputAdapter keyboard, Key key, string? text,
        EmulationWorker worker, long afterTick)
    {
        if (worker.TakeLatestFrame() is { } prior && prior.MasterTick > afterTick)
            afterTick = prior.MasterTick;
        Assert.Equal(text is null, keyboard.KeyDown(key));
        if (text is not null) Assert.True(keyboard.TextInput(text));
        var heldThrough = await WaitKeyFrames(worker, 8, afterTick);
        keyboard.KeyUp(key);
        return await WaitKeyFrames(worker, 8, heldThrough);
    }

    private static async Task<long> WaitKeyFrames(EmulationWorker worker, int count, long afterTick)
    {
        // Anchor on the first frame completed after the key transition was enqueued so stale
        // ticks never shorten the hold below the ROM's 5-frame debounce, and use synchronous
        // 1ms sleeps so a starved ThreadPool continuation queue cannot stretch a key hold past
        // the ROM's 35-frame typematic repeat threshold.
        long anchorTick = -1;
        var tick = afterTick;
        var targetTick = -1L;
        var stallDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (targetTick < 0 || tick < targetTick)
        {
            if (worker.TakeLatestFrame() is { } frame && frame.MasterTick > tick)
            {
                tick = frame.MasterTick;
                stallDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                if (anchorTick < 0)
                {
                    anchorTick = tick;
                    targetTick = anchorTick + count * FrameTiming.FrameTicks;
                }
                if (tick >= targetTick) break;
            }
            else
            {
                if (worker.Completion.IsCompleted) { await worker.Completion; break; }
                if (DateTime.UtcNow >= stallDeadline) break;
                Thread.Sleep(1);
            }
        }
        var elapsedFrames = anchorTick > 0 && tick >= anchorTick
            ? (int)((tick - anchorTick) / FrameTiming.FrameTicks) : 0;
        Assert.True(elapsedFrames >= count, $"Expected {count} key-hold frames after tick {afterTick}; saw {elapsedFrames}.");
        return tick;
    }

    private static async Task<long> WaitFrames(EmulationWorker worker, int count, long afterTick)
    {
        var startTick = afterTick;
        var tick = afterTick;
        var targetTick = afterTick > 0 ? afterTick + count * FrameTiming.FrameTicks : -1L;
        var stallDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (targetTick < 0 || tick < targetTick)
        {
            if (worker.TakeLatestFrame() is { } frame && frame.MasterTick > tick)
            {
                if (startTick <= 0)
                {
                    startTick = frame.MasterTick - FrameTiming.FrameTicks;
                    targetTick = startTick + count * FrameTiming.FrameTicks;
                }
                tick = frame.MasterTick;
                stallDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                if (tick >= targetTick) break;
            }
            else
            {
                if (worker.Completion.IsCompleted) { await worker.Completion; break; }
                if (DateTime.UtcNow >= stallDeadline) break;
                await Task.Delay(1);
            }
        }
        var elapsedFrames = startTick > 0 && tick > startTick
            ? (int)((tick - startTick) / FrameTiming.FrameTicks) : 0;
        Assert.True(elapsedFrames >= count, $"Expected {count} paced frames after tick {afterTick}; saw {elapsedFrames}.");
        return tick;
    }

    private static async Task<Hc2000.Machine.Video.VideoFrame> WaitForDecodedScreen(EmulationWorker worker,
        byte[] rom, Func<string, bool> predicate, TimeSpan timeout)
    {
        var maxEmulatedTicks = Math.Max(300L, (long)Math.Ceiling(timeout.TotalSeconds * 50.0)) * FrameTiming.FrameTicks;
        long startTick = -1;
        long lastTick = -1;
        var stallDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(30.0, timeout.TotalSeconds * 2));
        var last = "(no frame yet)";
        while (DateTime.UtcNow < stallDeadline)
        {
            if (worker.TakeLatestFrame() is { } frame)
            {
                if (startTick < 0) startTick = frame.MasterTick;
                if (frame.MasterTick > lastTick)
                {
                    lastTick = frame.MasterTick;
                    stallDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                }
                last = DecodeBasicScreen(frame, rom);
                if (predicate(last)) return frame;
                if (frame.MasterTick - startTick >= maxEmulatedTicks) break;
            }
            if (worker.Completion.IsCompleted) await worker.Completion;
            await Task.Delay(1);
        }
        throw new TimeoutException($"BASIC ROM screen did not reach expected text. Last rendered screen:\n{last}");
    }

    // Decode the actual ULA-published 32x24 framebuffer using glyphs in the selected BASIC ROM.
    // The test never reads Machine/RAM outside the emulation owner.
    internal static string DecodeBasicScreen(Hc2000.Machine.Video.VideoFrame frame, byte[] rom)
    {
        if (frame.Width != 352 || frame.Height != 296) return "";
        var glyphs = new Dictionary<ulong, char>(96);
        for (var code = 32; code < 127; code++)
        {
            var key = BinaryPrimitives.ReadUInt64LittleEndian(rom.AsSpan(0x3d00 + (code - 32) * 8, 8));
            glyphs.TryAdd(key, (char)code);
        }
        var pixels = frame.Pixels.Span;
        var screen = new System.Text.StringBuilder(24 * 33);
        Span<byte> bits = stackalloc byte[8];
        Span<uint> cellColors = stackalloc uint[8];
        Span<int> cellCounts = stackalloc int[8];
        for (var row = 0; row < 24; row++)
        {
            for (var column = 0; column < 32; column++)
            {
                bits.Clear();
                var colorKinds = 0;
                for (var y = 0; y < 8; y++)
                {
                    var rowOffset = (40 + row * 8 + y) * frame.Width + 48 + column * 8;
                    for (var x = 0; x < 8; x++)
                    {
                        var color = pixels[rowOffset + x];
                        var found = -1;
                        for (var k = 0; k < colorKinds; k++)
                        {
                            if (cellColors[k] == color) { found = k; break; }
                        }
                        if (found >= 0) cellCounts[found]++;
                        else if (colorKinds < cellColors.Length)
                        {
                            cellColors[colorKinds] = color;
                            cellCounts[colorKinds] = 1;
                            colorKinds++;
                        }
                    }
                }
                var background = cellColors[0];
                var bestCount = cellCounts[0];
                for (var k = 1; k < colorKinds; k++)
                {
                    if (cellCounts[k] > bestCount)
                    {
                        bestCount = cellCounts[k];
                        background = cellColors[k];
                    }
                }
                for (var y = 0; y < 8; y++)
                {
                    var rowOffset = (40 + row * 8 + y) * frame.Width + 48 + column * 8;
                    byte rowBits = 0;
                    for (var x = 0; x < 8; x++)
                        if (pixels[rowOffset + x] != background)
                            rowBits |= (byte)(0x80 >> x);
                    bits[y] = rowBits;
                }
                var glyphKey = BinaryPrimitives.ReadUInt64LittleEndian(bits);
                screen.Append(glyphs.GetValueOrDefault(glyphKey, '?'));
            }
            if (row < 23) screen.Append('\n');
        }
        return screen.ToString();
    }

    private static async Task<Hc2000.Machine.Video.VideoFrame> WaitForBootFrame(EmulationWorker worker, TimeSpan timeout)
    {
        var maxEmulatedTicks = Math.Max(150L, (long)Math.Ceiling(timeout.TotalSeconds * 50.0)) * FrameTiming.FrameTicks;
        long startTick = -1;
        long lastTick = -1;
        var stallDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(30.0, timeout.TotalSeconds * 2));
        while (DateTime.UtcNow < stallDeadline)
        {
            if (worker.TakeLatestFrame() is { } frame)
            {
                if (startTick < 0) startTick = frame.MasterTick;
                if (frame.MasterTick > lastTick)
                {
                    lastTick = frame.MasterTick;
                    stallDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                }
                if (frame.Pixels.ToArray().Distinct().Count() > 1) return frame;
                if (frame.MasterTick - startTick >= maxEmulatedTicks) break;
            }
            if (worker.Completion.IsCompleted) await worker.Completion;
            await Task.Delay(5);
        }
        throw new TimeoutException("Canonical BASIC ROM did not render boot output within the test timeout.");
    }
}
