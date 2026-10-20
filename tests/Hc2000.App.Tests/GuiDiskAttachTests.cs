using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.Machine.Input;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Installable GUI disk-attach path: faithful FDC, owner-thread MediaSession, AttachMedia.</summary>
public sealed class GuiDiskAttachTests
{
    [AvaloniaFact]
    public async Task WholeChipBootAttachesMediaBridgeOnTheInstallableWindow()
    {
        var missing = RomSelectionFixture.MissingAssets();
        Assert.Null(missing);
        var window = new MainWindow(new LaunchOptions(null, null, true, false));
        window.Show();
        var media = window.FindControl<MediaPanel>("MediaControls")!;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        Button? insert = null;
        while (DateTime.UtcNow < deadline)
        {
            insert = media.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => Equals(b.Content, "Insert DSK…"));
            if (insert?.IsEnabled == true) break;
            await Task.Delay(20);
        }
        Assert.True(insert?.IsEnabled == true, "Whole-chip boot must call AttachMedia so Insert DSK is enabled.");
        window.Close();
    }

    [AvaloniaFact]
    public async Task FaithfulAttachInsertsCpmDiskObservesReadDataAndReachesAPrompt()
    {
        var missing = RomSelectionFixture.MissingAssets();
        Assert.Null(missing);
        var diskPath = Path.Combine(RomSelectionFixture.RepositoryRoot(), "disks", "cpm", "CPM22-HC.DSK");
        Assert.True(File.Exists(diskPath) && new FileInfo(diskPath).Length > 256, "CPM22-HC.DSK is not materialized.");
        // The production loader validates the complete original M27C512 identity,
        // then supplies its authentic BASIC/CPM/IF1 banks; no legacy v1 fallback.
        var selected = await Task.Run(() => ProfileRomSelection.LoadWholeChipV2(RomSelectionFixture.RepositoryRoot()));
        var rom = selected.Basic;

        var window = new MainWindow();
        var actualClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler closedObserver = (_, _) => actualClosed.TrySetResult();
        window.Closed += closedObserver;
        EmulationWorker? worker = null;
        Exception? primaryFailure = null;
        try
        {
            var prepared = BasicMachineComposition.PrepareWholeChipV2(selected);
            var slot = new ComposedFloppySlot();
            var bridge = new MediaBridge(window.InputQueue);
            // Attach before Show: window open boots the whole-chip machine unless a worker is already attached.
            // Same public seams ReplaceBasicSessionAsync uses: AttachMedia, then CreateWorker with that bridge.
            window.AttachMedia(bridge);
            worker = BasicMachineComposition.CreateWorker(prepared, window.InputQueue, bridge, slot);
            window.AttachRuntime(worker);
            window.Show();

            var composed = await WaitForFloppy(slot, TimeSpan.FromSeconds(5));
            Assert.False(composed.Instant, "Faithful GUI composition must not opt into fdc.instant.");

            await WaitForDecoded(worker, rom, text => text.Contains("HC2000", StringComparison.Ordinal)
                && text.Contains("I.C.E.", StringComparison.Ordinal)
                && text.Contains("FELIX", StringComparison.Ordinal),
                TimeSpan.FromSeconds(15));
            await WaitFrames(worker, 40);

            var beforeInsert = await bridge.RequestAsync(new MediaCommand(MediaAction.Inspect));
            Assert.Null(beforeInsert.Error);
            var unloaded = Assert.IsType<TapeView>(beforeInsert.Tape);
            var deck = Assert.IsType<TapeDeckView>(unloaded.Deck);
            Assert.Null(unloaded.Name);
            Assert.Null(unloaded.SourceFormat);
            Assert.Equal(TapeState.Unloaded, unloaded.Cursor.State);
            Assert.False(deck.Present);
            Assert.Equal(TapeDeckMode.Stopped, deck.Mode);
            Assert.False(deck.Paused);
            Assert.Null(deck.Capacity);
            Assert.False(deck.A.Dirty);
            Assert.False(deck.B.Dirty);

            var disk = ImmutableArray.CreateRange(await File.ReadAllBytesAsync(diskPath));
            var inserted = await bridge.RequestAsync(new MediaCommand(MediaAction.InsertDisk, DiskSlot.A, disk, "CPM22-HC.DSK"));
            Assert.Null(inserted.Error);
            Assert.True(inserted.A.Present);
            Assert.Equal("CPM22-HC.DSK", inserted.A.Name);
            Assert.Equal(beforeInsert.B, inserted.B);
            AssertNoCassettePreserved(unloaded, inserted.Tape);

            var rejected = await bridge.RequestAsync(new MediaCommand(MediaAction.InsertDisk, DiskSlot.A,
                ImmutableArray.Create<byte>(1, 2, 3), "bad.dsk"));
            Assert.NotNull(rejected.Error);
            Assert.Equal("CPM22-HC.DSK", rejected.A.Name);
            Assert.Equal(inserted.A, rejected.A);
            Assert.Equal(inserted.B, rejected.B);
            AssertNoCassettePreserved(unloaded, rejected.Tape);

            await TypeWarmBoot(window.InputQueue, worker);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(75);
            var last = "(no frame)";
            var sawPrompt = false;
            while (DateTime.UtcNow < deadline)
            {
                if (worker.TakeLatestFrame() is { } frame)
                {
                    last = DecodeBasicScreen(frame, rom);
                    if (last.Contains("A>", StringComparison.Ordinal) || FrameShowsAPrompt(frame))
                    {
                        sawPrompt = true;
                        break;
                    }
                }
                if (worker.Completion.IsCompleted) await worker.Completion;
                await Task.Delay(15);
            }

            Assert.True(slot.ReadData > 0,
                $"FDC READ DATA gate was not observed after InsertDisk + warm boot. ReadData={slot.ReadData}. Last screen:\n{last}");
            Assert.True(sawPrompt,
                $"CP/M warm boot did not reach A> (faithful timing, no instant). ReadData={slot.ReadData}. Last screen:\n{last}");
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            // Separate cleanup-only budget: never extend the guest or frame deadlines above.
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                worker?.Dispose();
                window.Close();
                await actualClosed.Task.WaitAsync(cleanupDeadline.Token);
                // Capture only after genuine Closed has run the production shutdown handler.
                var shutdown = window.ShutdownCompletion;
                await shutdown.WaitAsync(cleanupDeadline.Token);
                if (worker is not null) await worker.Completion.WaitAsync(cleanupDeadline.Token);
            }
            catch (Exception cleanupFailure)
            {
                if (primaryFailure is not null)
                    throw new AggregateException("GUI attach failed and owned teardown also failed.",
                        primaryFailure, cleanupFailure);
                throw;
            }
            finally
            {
                window.Closed -= closedObserver;
            }
        }
    }

    private static void AssertNoCassettePreserved(TapeView before, TapeView? observed)
    {
        var after = Assert.IsType<TapeView>(observed);
        var deck = Assert.IsType<TapeDeckView>(after.Deck);
        var original = Assert.IsType<TapeDeckView>(before.Deck);
        Assert.Null(after.Name);
        Assert.Null(after.SourceFormat);
        Assert.Equal(before.Cursor, after.Cursor); // No insertion, transport motion or reset.
        Assert.False(deck.Present);
        Assert.Equal(TapeDeckMode.Stopped, deck.Mode);
        Assert.False(deck.Paused);
        Assert.Equal(original.MediaIdentity, deck.MediaIdentity);
        Assert.Equal(original.ActiveSide, deck.ActiveSide);
        Assert.Equal(original.Capacity, deck.Capacity);
        Assert.Equal(original.Position, deck.Position);
        Assert.Equal(original.Duration, deck.Duration);
        Assert.Equal(original.Counter, deck.Counter);
        Assert.Equal(original.A, deck.A);
        Assert.Equal(original.B, deck.B);
        // Live EAR/MIC levels are not cassette identity or motion predicates.
    }

    private static async Task<Hc2000.Machine.Floppy.FloppyController> WaitForFloppy(ComposedFloppySlot slot, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (slot.Controller is { } floppy) return floppy;
            await Task.Delay(5);
        }
        throw new TimeoutException("Faithful FloppyController was not published.");
    }

    private static async Task TypeWarmBoot(InputQueue queue, EmulationWorker worker)
    {
        // Token-aware physical keys, same sequence as the headless CP/M warm boot:
        // P (PRINT) Space, Caps+Symbol extended chord, L (USR), Space, 14446, Enter.
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
        var source = "gui-disk:" + Interlocked.Increment(ref seq);
        queue.Enqueue(new KeyTransition(source, new KeyChord(key), true));
        await WaitFrames(worker, 8);
        queue.Enqueue(new KeyTransition(source, new KeyChord(key), false));
        await WaitFrames(worker, 8);
    }

    private static async Task Hold(InputQueue queue, EmulationWorker worker, HcKey a, HcKey b)
    {
        var sa = "gui-disk:" + Interlocked.Increment(ref seq);
        var sb = "gui-disk:" + Interlocked.Increment(ref seq);
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

    // CP/M paints 4x8 glyphs, two per 8x8 cell. A = 00 02 05 05 07 05 05 00, > = 00 00 04 02 01 02 04 00.
    private static bool FrameShowsAPrompt(VideoFrame frame)
    {
        if (frame.Width != 352 || frame.Height != 296) return false;
        ReadOnlySpan<byte> glyphA = [0x00, 0x02, 0x05, 0x05, 0x07, 0x05, 0x05, 0x00];
        ReadOnlySpan<byte> glyphGt = [0x00, 0x00, 0x04, 0x02, 0x01, 0x02, 0x04, 0x00];
        var pixels = frame.Pixels.Span;
        for (var row = 0; row < 24; row++)
        {
            for (var col = 0; col < 63; col += 2)
            {
                if (GlyphMatches(pixels, frame.Width, col, row, glyphA)
                    && GlyphMatches(pixels, frame.Width, col + 1, row, glyphGt))
                    return true;
            }
        }
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
