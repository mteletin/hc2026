using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Hc2000.App.Configuration;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.App.Settings;
using Hc2000.App.Workspace;
using Hc2000.Machine.Input;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>X1 leaf C (hc2000-x1-app-ui-flow): the ordinary pre-start display choice and the actual
/// App flow — the choice persists as a draft, is adopted at the next cold Start, the real machine
/// publishes the derived surface (BASIC and CP/M readback), the viewport keeps the logical frame
/// size, the choice is per machine, a live change is refused through the cold-choice path, and the
/// selected Reset keeps the adopted choice. No flag-configured machine substitutes for the ordinary
/// UI route.</summary>
public sealed class X1AppOverlayWorkflowTests
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc2000-x1-overlay-" + Guid.NewGuid().ToString("N"));
    private SettingsStore Store => new(directory);
    private MainWindow Window() => new(null, RomSelectionFixture.RepositoryRoot(), Store);
    private static PrestartPanel Panel(MainWindow window) => (PrestartPanel)window.FindControl<ContentControl>("PrestartConfiguration")!.Content!;
    private static T Field<T>(PrestartPanel panel, string name) where T : Control
        => panel.GetLogicalDescendants().OfType<T>().Distinct().Single(c => c.Name == name);

    [AvaloniaFact]
    public async Task PrestartDisplayTopicShowsTheOverlayChoiceDefaultingOff()
    {
        var window = Window();
        try
        {
            window.Show();
            var checkbox = Field<CheckBox>(Panel(window), "PrestartTextOverlay");
            Assert.Contains("overlay", checkbox.Content?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.True(checkbox.IsChecked != true, "off must be the faithful default");
            Assert.False(Panel(window).Build().TextOverlay);
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task PrestartDraftCarriesTheOverlayChoiceWithoutStartingOrSaving()
    {
        var window = Window();
        try
        {
            window.Show();
            var checkbox = Field<CheckBox>(Panel(window), "PrestartTextOverlay");
            checkbox.IsChecked = true;
            var on = Panel(window).Build();
            Assert.True(on.TextOverlay);
            Assert.False(File.Exists(Store.FilePath));
            checkbox.IsChecked = false;
            Assert.False(Panel(window).Build().TextOverlay);
            Assert.False(File.Exists(Store.FilePath));
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task RealStartWithOverlayOnPublishesTheDerivedSurface()
    {
        Assert.Null(RomSelectionFixture.MissingAssets());
        var window = Window();
        try
        {
            window.Show();
            Field<CheckBox>(Panel(window), "PrestartTextOverlay").IsChecked = true;
            Assert.True(await Panel(window).StartAsync());
            var roms = await Task.Run(() => ProfileRomSelection.LoadWholeChipV2(RomSelectionFixture.RepositoryRoot()));
            var frame = await WaitDerivedFrame(window, TimeSpan.FromSeconds(45),
                frame => DecodeBasicScreen(frame, roms.Basic).Contains("FELIX", StringComparison.Ordinal));
            Assert.Equal((352, 296), (frame.Width, frame.Height));
            var mask = frame.TextOverlay;
            Assert.Equal((704, 592), (mask.Width, mask.Height));
            Assert.Equal(frame.MasterTick, mask.MasterTick);
            Assert.Equal(0, MaskViolations(frame));
            Assert.True(mask.Pixels.Span.ToArray().Any(value => value != 0u), "the BASIC screen must carry derived ink in the mask");
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task CpmDiskWithOverlayOnWarmBootsAndInputAdvancesTheDerivedFrame()
    {
        Assert.Null(RomSelectionFixture.MissingAssets());
        var root = RomSelectionFixture.RepositoryRoot();
        var diskPath = Path.Combine(root, "disks", "cpm", "CPM22-HC.DSK");
        Assert.True(File.Exists(diskPath) && new FileInfo(diskPath).Length > 256, "CPM22-HC.DSK is not materialized.");
        var rom = await Task.Run(() => ProfileRomSelection.LoadWholeChipV2(root));
        var window = Window();
        try
        {
            window.Show();
            Field<TextBox>(Panel(window), "PrestartDiskA").Text = diskPath;
            Field<CheckBox>(Panel(window), "PrestartTextOverlay").IsChecked = true;
            Assert.True(await Panel(window).StartAsync());
            await WaitDerivedFrame(window, TimeSpan.FromSeconds(45));
            var worker = window.CurrentRuntime;
            Assert.NotNull(worker);
            // BASIC must be READY before the warm-boot command; the same screen oracle as the disk-attach tests.
            await WaitForDecoded(worker, rom.Basic, text => text.Contains("FELIX", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
            await WaitFrames(worker, 40);
            await TypeWarmBoot(window.InputQueue, worker);
            var promptSeen = false;
            var promptUntil = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            while (DateTime.UtcNow < promptUntil && !promptSeen)
            {
                if (window.LatestPresentedFrame is { } frame && FrameShowsAPrompt(frame)) promptSeen = true;
                else await Task.Delay(25);
            }
            Assert.True(promptSeen, "the CP/M warm boot (PRINT USR 14446) did not reach the A> prompt.");
            var atPrompt = Assert.IsType<TextOverlayVideoFrame>(window.LatestPresentedFrame);
            Assert.Equal(0, MaskViolations(atPrompt));
            var before = atPrompt.Pixels.ToArray();
            await Tap(window.InputQueue, worker, HcKey.D);
            await Tap(window.InputQueue, worker, HcKey.I);
            await Tap(window.InputQueue, worker, HcKey.R);
            await Tap(window.InputQueue, worker, HcKey.Enter);
            var advanced = false;
            var advanceUntil = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTime.UtcNow < advanceUntil && !advanced)
            {
                if (window.LatestPresentedFrame is TextOverlayVideoFrame f && !f.Pixels.Span.SequenceEqual(before))
                {
                    Assert.Equal(0, MaskViolations(f));
                    advanced = true;
                }
                else await Task.Delay(25);
            }
            Assert.True(advanced, "input at the CP/M prompt did not advance the published frame.");
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task OverlayPresentationKeepsTheLogicalFrameSizeAndViewportFit()
    {
        Assert.Null(RomSelectionFixture.MissingAssets());
        var window = Window();
        try
        {
            window.Show();
            Field<CheckBox>(Panel(window), "PrestartTextOverlay").IsChecked = true;
            Assert.True(await Panel(window).StartAsync());
            await WaitDerivedFrame(window, TimeSpan.FromSeconds(45));
            var screen = window.FindControl<VideoSurface>("Screen")!;
            var source = Assert.IsType<WriteableBitmap>(screen.Source);
            Assert.Equal(new PixelSize(704, 592), source.PixelSize);
            // 96 dpi on both overlay paths: the render path drops non-96-dpi bitmap content (the
            // 0.1.150 blank screen), so the surface occupies W x H machine pixels at 96 dpi
            // instead of the 192 dpi half-size display trick.
            Assert.Equal(96.0, source.Dpi.X, 0.001);
            Assert.Equal(96.0, source.Dpi.Y, 0.001);
            Assert.Equal(704.0, source.Size.Width, 0.001);
            Assert.Equal(592.0, source.Size.Height, 0.001);
            var viewport = window.FindControl<AspectRatioViewport>("ScreenViewport")!;
            // The shell caps the live viewport height to the window. Remove that constraint only
            // for these synchronous layout probes of the real surface in a known rectangle.
            viewport.MaxHeight = double.PositiveInfinity;
            viewport.FitToWindow = true;
            // Normal (fit) mode: fills the available rectangle at the 352x296 frame aspect, not 2x.
            viewport.Measure(new Size(2000, 2000));
            var fit = viewport.DesiredSize;
            Assert.Equal(2000.0, fit.Width, 0.5);
            Assert.Equal(2000.0 * 296.0 / 352.0, fit.Height, 0.5);
            // Compact mode: the integer 2x zoom of the 352x296 frame; a hidden 2x would double this.
            viewport.FitToWindow = false;
            viewport.RequestedScale = 2;
            viewport.Measure(new Size(2000, 2000));
            var compact = viewport.DesiredSize;
            Assert.Equal(704.0, compact.Width, 0.5);
            Assert.Equal(592.0, compact.Height, 0.5);
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task OverlayIsPerMachineBetweenTwoRealMachines()
    {
        Assert.Null(RomSelectionFixture.MissingAssets());
        var windowA = Window();
        var windowB = Window();
        try
        {
            windowA.Show();
            windowB.Show();
            Field<CheckBox>(Panel(windowA), "PrestartTextOverlay").IsChecked = true;
            Assert.True(await Panel(windowA).StartAsync());
            Assert.True(await Panel(windowB).StartAsync());
            var derived = await WaitDerivedFrame(windowA, TimeSpan.FromSeconds(90));
            var plain = await WaitPresentedFrame(windowB, TimeSpan.FromSeconds(90));
            Assert.Equal(0, MaskViolations(derived));
            Assert.True(plain is not TextOverlayVideoFrame, "the second machine keeps its own display choice (no overlay).");
        }
        finally
        {
            windowA.Close(); await windowA.ShutdownCompletion;
            windowB.Close(); await windowB.ShutdownCompletion;
        }
    }

    [AvaloniaFact]
    public async Task LiveOverlayChangeIsRefusedThroughTheColdChoicePath()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "A", TextOverlay = true }, out var tab, out var refusal));
            Assert.Null(refusal);
            tab!.Session.AttachWorker(Z80VisualDemo.CreateWorker());
            tab.Session.BasicRuntimeActive = true;
            var target = new MachineDraftTarget(tab.TabId, tab.DraftRevision);
            var error = window.ApplyRunningDraft(target, tab.Configuration with { TextOverlay = false });
            Assert.Equal("The text overlay is a cold hardware choice; a new machine draft is required. Use New machine; selected Reset keeps the adopted choice.", error);
            Assert.True(tab.Configuration.TextOverlay); // a refused draft must not change the running configuration;
            Assert.Equal(0, tab.DraftRevision);
            Assert.Equal(0, tab.InputQueue.BoundaryCount);
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task SelectedResetKeepsTheAdoptedOverlayChoice()
    {
        Assert.Null(RomSelectionFixture.MissingAssets());
        var window = Window();
        try
        {
            window.Show();
            Field<CheckBox>(Panel(window), "PrestartTextOverlay").IsChecked = true;
            Assert.True(await Panel(window).StartAsync());
            // Do not reset on the first frame: the new machine's first frame has the SAME tick,
            // not a lower tick. Let the original session advance before comparing epochs.
            var before = await WaitDerivedFrame(window, TimeSpan.FromSeconds(45),
                frame => frame.MasterTick >= 10L * FrameTiming.FrameTicks);
            var worker = Assert.IsType<EmulationWorker>(window.CurrentRuntime);
            var generation = worker.SessionGeneration;
            var reset = window.FindControl<Button>("MachineReset")!;
            Assert.True(reset.IsEnabled, "a booted machine must offer the selected Reset");
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            // Reset rebuilds from the same prepared profile. Require the owner's new session AND
            // its restarted tick, so neither a stale pre-reset frame nor an ignored click can pass.
            var after = await WaitDerivedFrame(window, TimeSpan.FromSeconds(90),
                frame => worker.SessionGeneration > generation && frame.MasterTick < before.MasterTick);
            Assert.Equal(0, MaskViolations(after));
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    private static async Task<TextOverlayVideoFrame> WaitDerivedFrame(MainWindow window, TimeSpan deadline,
        Func<TextOverlayVideoFrame, bool>? predicate = null)
    {
        var until = DateTime.UtcNow + deadline;
        while (DateTime.UtcNow < until)
        {
            if (window.LatestPresentedFrame is TextOverlayVideoFrame frame
                && (predicate is null || predicate(frame)))
                return frame;
            await Task.Delay(50);
        }
        Assert.Fail($"No matching derived overlay frame within {deadline} (BootedIf1Length={window.BootedIf1Length}, latest={window.LatestPresentedFrame?.GetType().Name}, tick={window.LatestPresentedFrame?.MasterTick}).");
        return null!;
    }

    private static async Task<VideoFrame> WaitPresentedFrame(MainWindow window, TimeSpan deadline)
    {
        var until = DateTime.UtcNow + deadline;
        while (DateTime.UtcNow < until)
        {
            if (window.LatestPresentedFrame is { } frame && window.BootedIf1Length == 16384) return frame;
            await Task.Delay(50);
        }
        Assert.Fail($"No presented frame within {deadline} (BootedIf1Length={window.BootedIf1Length}).");
        return null!;
    }

    // ULA contract (Ula.cs PublishOverlay): the mask repeats every base pixel 2x2, so each mask
    // pixel is paper (ARGB 0) or the base frame pixel colour. Anything else is a violated invariant.
    private static int MaskViolations(TextOverlayVideoFrame frame)
    {
        var basePixels = frame.Pixels.Span;
        var mask = frame.TextOverlay;
        var maskPixels = mask.Pixels.Span;
        if (mask.Width != frame.Width * 2 || mask.Height != frame.Height * 2) return -1;
        var violations = 0;
        for (var y = 0; y < mask.Height; y++)
            for (var x = 0; x < mask.Width; x++)
            {
                var value = maskPixels[y * mask.Width + x];
                if (value == 0u || value == basePixels[(y / 2) * frame.Width + (x / 2)]) continue;
                violations++;
                break;
            }
        return violations;
    }

    private static async Task WaitForDecoded(EmulationWorker worker, byte[] rom, Func<string, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var last = "(no frame yet)";
        while (DateTime.UtcNow < deadline)
        {
            if (worker.TakeLatestFrame() is { } frame)
            {
                last = DecodeBasicScreen(frame, rom);
                if (predicate(last)) return;
            }
            if (worker.Completion.IsCompleted) await worker.Completion;
            await Task.Delay(5);
        }
        throw new TimeoutException("Screen did not reach the expected text.\n" + last);
    }

    // The warm-boot command, token-aware physical keys, same sequence as the headless CP/M warm boot:
    // P (PRINT) Space, Caps+Symbol extended chord, L (USR), Space, 14446, Enter.
    private static async Task TypeWarmBoot(InputQueue queue, EmulationWorker worker)
    {
        await Tap(queue, worker, HcKey.P);
        await Tap(queue, worker, HcKey.Space);
        await Hold(queue, worker, HcKey.CapsShift, HcKey.SymbolShift);
        await Tap(queue, worker, HcKey.L);
        await Tap(queue, worker, HcKey.Space);
        await Tap(queue, worker, HcKey.D1);
        await Tap(queue, worker, HcKey.D4);
        await Tap(queue, worker, HcKey.D4);
        await Tap(queue, worker, HcKey.D4);
        await Tap(queue, worker, HcKey.D6);
        await Tap(queue, worker, HcKey.Enter);
    }

    private static long seq;

    private static async Task Tap(InputQueue queue, EmulationWorker worker, HcKey key)
    {
        var source = "x1-overlay:" + Interlocked.Increment(ref seq);
        queue.Enqueue(new KeyTransition(source, new KeyChord(key), true));
        await WaitFrames(worker, 8);
        queue.Enqueue(new KeyTransition(source, new KeyChord(key), false));
        await WaitFrames(worker, 8);
    }

    private static async Task Hold(InputQueue queue, EmulationWorker worker, HcKey a, HcKey b)
    {
        var sa = "x1-overlay:" + Interlocked.Increment(ref seq);
        var sb = "x1-overlay:" + Interlocked.Increment(ref seq);
        queue.Enqueue(new KeyTransition(sa, new KeyChord(a), true));
        queue.Enqueue(new KeyTransition(sb, new KeyChord(b), true));
        await WaitFrames(worker, 8);
        queue.Enqueue(new KeyTransition(sa, new KeyChord(a), false));
        queue.Enqueue(new KeyTransition(sb, new KeyChord(b), false));
        await WaitFrames(worker, 8);
    }

    private static async Task WaitFrames(EmulationWorker worker, int count)
    {
        var seen = 0;
        long tick = -1;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4 + count * 0.05);
        while (seen < count && DateTime.UtcNow < deadline)
        {
            if (worker.TakeLatestFrame() is { } frame && frame.MasterTick > tick)
            {
                tick = frame.MasterTick;
                seen++;
            }
            else await Task.Delay(1);
        }
        Assert.True(seen >= count, $"Expected {count} frames; saw {seen}.");
    }

    // CP/M paints 4x8 glyphs, two per 8x8 cell. A = 00 02 05 05 07 05 05 00, > = 00 00 04 02 01 02 04 00.
    private static bool FrameShowsAPrompt(VideoFrame frame)
    {
        if (frame.Width != 352 || frame.Height != 296) return false;
        ReadOnlySpan<byte> glyphA = [0x00, 0x02, 0x05, 0x05, 0x07, 0x05, 0x05, 0x00];
        ReadOnlySpan<byte> glyphGt = [0x00, 0x00, 0x04, 0x02, 0x01, 0x02, 0x04, 0x00];
        var pixels = frame.Pixels.Span;
        for (var row = 0; row < 24; row++)
            for (var col = 0; col < 63; col += 2)
                if (GlyphMatches(pixels, frame.Width, col, row, glyphA)
                    && GlyphMatches(pixels, frame.Width, col + 1, row, glyphGt))
                    return true;
        return false;
    }

    private static bool GlyphMatches(ReadOnlySpan<uint> pixels, int width, int col, int row, ReadOnlySpan<byte> glyph)
    {
        const int originX = 48;
        const int originY = 40;
        var cellX = originX + (col / 2) * 8;
        var cellY = originY + row * 8;
        var counts = new Dictionary<uint, int>();
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
            {
                var color = pixels[(cellY + y) * width + cellX + x];
                counts[color] = counts.GetValueOrDefault(color) + 1;
            }
        var background = counts.MaxBy(pair => pair.Value).Key;
        for (var y = 0; y < 8; y++)
        {
            byte bits = 0;
            for (var x = 0; x < 4; x++)
                if (pixels[(cellY + y) * width + originX + col * 4 + x] != background)
                    bits |= (byte)(0x8 >> x);
            if (bits != glyph[y]) return false;
        }
        return true;
    }

    private static string DecodeBasicScreen(VideoFrame frame, byte[] rom)
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
}
