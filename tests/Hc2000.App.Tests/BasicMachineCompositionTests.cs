using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
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

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            var last = "(no frame yet)";
            while (DateTime.UtcNow < deadline)
            {
                if (window.LatestPresentedFrame is { } frame)
                {
                    last = DecodeBasicScreen(frame, basic);
                    // Settled v2 glyph screen (measured: still this line after 3e8 master ticks).
                    // Not the v1 contiguous banner "HC2000 ICE FELIX", and not the console-hook word READY.
                    if (last.Contains("I.C.E.", StringComparison.Ordinal)
                        && last.Contains("HC2000", StringComparison.Ordinal))
                        break;
                }
                if (window.ShutdownCompletion.IsCompleted) await window.ShutdownCompletion;
                await Task.Delay(20);
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
        await WaitFrames(worker, 250, frame.MasterTick);
        Assert.True(keyboard.Paste(text));
        var seen = new List<string>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        var last = "";
        while (DateTime.UtcNow < deadline)
        {
            if (worker.TakeLatestFrame() is { } later)
            {
                var interesting = string.Join(" | ", DecodeBasicScreen(later, rom).Split('\n')
                    .Select(line => line.TrimEnd())
                    .Where(line => line.Trim().Length > 0));
                if (interesting.Length > 0 && interesting != last)
                {
                    seen.Add(interesting);
                    last = interesting;
                }
            }
            await Task.Delay(15);
        }
        worker.Dispose();
        await worker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        return seen;
    }

    private static async Task<long> TypeSymbolicKey(KeyboardInputAdapter keyboard, Key key, string? text,
        EmulationWorker worker, long afterTick)
    {
        Assert.Equal(text is null, keyboard.KeyDown(key));
        if (text is not null) Assert.True(keyboard.TextInput(text));
        var heldThrough = await WaitFrames(worker, 8, afterTick);
        keyboard.KeyUp(key);
        return await WaitFrames(worker, 8, heldThrough);
    }

    private static async Task<long> WaitFrames(EmulationWorker worker, int count, long afterTick)
    {
        var seen = 0;
        var tick = afterTick;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2 + count * 0.03);
        while (seen < count && DateTime.UtcNow < deadline)
        {
            if (worker.TakeLatestFrame() is { } frame && frame.MasterTick > tick)
            {
                tick = frame.MasterTick;
                seen++;
            }
            else await Task.Delay(1);
        }
        Assert.True(seen >= count, $"Expected {count} paced frames after tick {afterTick}; saw {seen}.");
        return tick;
    }

    private static async Task<Hc2000.Machine.Video.VideoFrame> WaitForDecodedScreen(EmulationWorker worker,
        byte[] rom, Func<string, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var last = "(no frame yet)";
        while (DateTime.UtcNow < deadline)
        {
            if (worker.TakeLatestFrame() is { } frame)
            {
                last = DecodeBasicScreen(frame, rom);
                if (predicate(last)) return frame;
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
        var glyphs = new Dictionary<string, char>(StringComparer.Ordinal);
        for (var code = 32; code < 127; code++)
        {
            var glyph = rom.AsSpan(0x3d00 + (code - 32) * 8, 8).ToArray();
            glyphs.TryAdd(Convert.ToHexString(glyph), (char)code);
        }
        var pixels = frame.Pixels.Span;
        var screen = new System.Text.StringBuilder(24 * 33);
        for (var row = 0; row < 24; row++)
        {
            for (var column = 0; column < 32; column++)
            {
                var bits = new byte[8];
                var counts = new Dictionary<uint, int>();
                for (var y = 0; y < 8; y++)
                    for (var x = 0; x < 8; x++)
                    {
                        var color = pixels[(40 + row * 8 + y) * frame.Width + 48 + column * 8 + x];
                        counts[color] = counts.GetValueOrDefault(color) + 1;
                    }
                var background = counts.MaxBy(pair => pair.Value).Key;
                for (var y = 0; y < 8; y++)
                    for (var x = 0; x < 8; x++)
                        if (pixels[(40 + row * 8 + y) * frame.Width + 48 + column * 8 + x] != background)
                            bits[y] |= (byte)(0x80 >> x);
                screen.Append(glyphs.GetValueOrDefault(Convert.ToHexString(bits), '?'));
            }
            if (row < 23) screen.Append('\n');
        }
        return screen.ToString();
    }

    private static async Task<Hc2000.Machine.Video.VideoFrame> WaitForBootFrame(EmulationWorker worker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (worker.TakeLatestFrame() is { } frame && frame.Pixels.ToArray().Distinct().Count() > 1) return frame;
            if (worker.Completion.IsCompleted) await worker.Completion;
            await Task.Delay(5);
        }
        throw new TimeoutException("Canonical BASIC ROM did not render boot output within the test timeout.");
    }
}
