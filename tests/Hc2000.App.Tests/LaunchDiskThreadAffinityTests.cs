using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Hc2000.App.Media;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Real v2 startup and the A-await-to-B UI callback boundary, independently of guest input.</summary>
public sealed class LaunchDiskThreadAffinityTests(ITestOutputHelper output)
{
    [AvaloniaFact]
    public async Task ActualV2StartupMountsBothPrivateDisksBeforeGuestInput()
    {
        Assert.Null(RomSelectionFixture.MissingAssets());
        var root = RomSelectionFixture.RepositoryRoot();
        var roms = await Task.Run(() => ProfileRomSelection.LoadWholeChipV2(root));
        var copies = CreateCopies(root);
        var window = new MainWindow(Options(copies), root);
        try
        {
            window.Show();
            var media = window.FindControl<MediaPanel>("MediaControls")!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!HasBothDisks(media))
            {
                AssertNoError(window, media);
                await Task.Delay(20, deadline.Token);
            }
            await AssertUsableV2(window, media, roms.Basic, deadline.Token);
            AssertBothDisks(media);
            Assert.Equal(0, window.InputQueue.BoundaryCount);
        }
        finally
        {
            window.Close();
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(copies, true);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteRealAInsertionKeepsBCallbackOnUiOwner(bool pollBeforeB)
    {
        Assert.Null(RomSelectionFixture.MissingAssets());
        var root = RomSelectionFixture.RepositoryRoot();
        var roms = await Task.Run(() => ProfileRomSelection.LoadWholeChipV2(root));
        var copies = CreateCopies(root);
        // Compose the genuine worker without launch media; the callback seam below supplies A/B.
        var window = new MainWindow(new LaunchOptions(null, null, true, false), root);
        var aInserted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?>? application = null;
        Task? overlappingInspection = null;
        var entered = new List<DiskSlot>();
        var aOnUi = false;
        var bOnUi = false;
        try
        {
            window.Show();
            var media = window.FindControl<MediaPanel>("MediaControls")!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await AssertUsableV2(window, media, roms.Basic, deadline.Token);
            async Task Insert(DiskSlot slot, string path)
            {
                entered.Add(slot);
                if (slot == DiskSlot.A) aOnUi = Dispatcher.UIThread.CheckAccess();
                else bOnUi = Dispatcher.UIThread.CheckAccess();
                output.WriteLine($"Before {slot} insertion: controls ready={InsertReady(media)}; notice={media.Notice}");
                // Do not substitute a stub reply or fake mounted state: use the real panel/worker.
                await media.InsertDiskAsync(path, slot);
                if (slot == DiskSlot.A)
                {
                    aInserted.TrySetResult();
                    await releaseA.Task;
                    if (pollBeforeB)
                    {
                        do
                        {
                            while (!InsertReady(media)) await Task.Delay(5, deadline.Token);
                            overlappingInspection = media.PollAsync(); // genuine inspection, not a fake mounted reply
                            if (overlappingInspection.IsCompleted) await Task.Delay(5, deadline.Token);
                        } while (overlappingInspection.IsCompleted);
                        output.WriteLine($"Inspection after held A: pending={!overlappingInspection.IsCompleted}");
                        Assert.False(overlappingInspection.IsCompleted);
                    }
                    // The synthetic hold lets normal inspection acquire the media command gate.
                    // Complete A only when B's real UI action is enabled again, without bypassing it.
                    while (!InsertReady(media)) await Task.Delay(5, deadline.Token);
                }
            }
            application = LaunchStartup.ApplyDisksAsync(Options(copies), Insert);
            await aInserted.Task.WaitAsync(deadline.Token);
            Assert.False(application.IsCompleted);
            Assert.False(releaseA.Task.IsCompleted);
            Assert.Contains("A-private.dsk", DriveLabel(media, DiskSlot.A), StringComparison.Ordinal);
            // An explicitly incomplete A callback, released off UI, forces the continuation boundary
            // without a sleep or changing any worker/file-I/O scheduling or emulation boundary.
            await Task.Run(() => releaseA.TrySetResult());
            var status = await application.WaitAsync(deadline.Token);
            if (overlappingInspection is not null) await overlappingInspection.WaitAsync(deadline.Token);
            output.WriteLine($"A UI={aOnUi}; B UI={bOnUi}; status={status ?? "(null)"}");
            Assert.Equal(new[] { DiskSlot.A, DiskSlot.B }, entered);
            Assert.True(aOnUi);
            Assert.True(bOnUi, "B callback left the UI owner after the incomplete A callback.");
            Assert.Null(status);
            AssertNoError(window, media);
            AssertBothDisks(media);
            Assert.Equal(0, window.InputQueue.BoundaryCount);
        }
        finally
        {
            releaseA.TrySetResult();
            try
            {
                if (application is not null)
                    await application.WaitAsync(TimeSpan.FromSeconds(10));
                if (overlappingInspection is not null)
                    await overlappingInspection.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                window.Close();
                await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10));
                Directory.Delete(copies, true);
            }
        }
    }

    private static string CreateCopies(string root)
    {
        var source = Path.Combine(root, "disks", "cpm", "CPM22-HC.DSK");
        Assert.True(File.Exists(source) && new FileInfo(source).Length > 256, "Repository disk is not materialized.");
        var directory = Path.Combine(Path.GetTempPath(), "hc2000-launch-affinity-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        File.Copy(source, Path.Combine(directory, "A-private.dsk"));
        File.Copy(source, Path.Combine(directory, "B-private.dsk"));
        return directory;
    }

    private static LaunchOptions Options(string directory) => new(
        "A:" + Path.Combine(directory, "A-private.dsk"),
        "B:" + Path.Combine(directory, "B-private.dsk"), true, false);

    private static string DriveLabel(MediaPanel media, DiskSlot slot) =>
        media.GetLogicalDescendants().OfType<TextBlock>()
            .Single(t => t.Text?.StartsWith(slot + ":", StringComparison.Ordinal) == true).Text!;

    private static bool HasBothDisks(MediaPanel media) =>
        DriveLabel(media, DiskSlot.A).Contains("A-private.dsk", StringComparison.Ordinal)
        && DriveLabel(media, DiskSlot.B).Contains("B-private.dsk", StringComparison.Ordinal);

    private static bool InsertReady(MediaPanel media)
    {
        var buttons = media.GetLogicalDescendants().OfType<Button>()
            .Where(b => Equals(b.Content, "Insert DSK…")).ToArray();
        return buttons.Length == 2 && buttons.All(b => b.IsEnabled);
    }

    private void AssertBothDisks(MediaPanel media)
    {
        foreach (var slot in new[] { DiskSlot.A, DiskSlot.B })
        {
            var label = DriveLabel(media, slot);
            output.WriteLine(label);
            Assert.Contains($"{slot}-private.dsk", label, StringComparison.Ordinal);
            Assert.Contains("last boundary #1", label, StringComparison.Ordinal);
            Assert.Contains("dirty sectors 0", label, StringComparison.Ordinal);
            Assert.DoesNotContain("empty", label, StringComparison.Ordinal);
        }
    }

    private static void AssertNoError(MainWindow window, MediaPanel media)
    {
        var status = window.FindControl<TextBlock>("RuntimeStatus")!.Text ?? "";
        Assert.DoesNotContain("Disk A:", status, StringComparison.Ordinal);
        Assert.DoesNotContain("Disk B:", status, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Runtime stopped", status, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", media.Notice, StringComparison.OrdinalIgnoreCase);
    }

    private async Task AssertUsableV2(MainWindow window, MediaPanel media, byte[] basic, CancellationToken token)
    {
        var screen = "(no frame)";
        while (true)
        {
            AssertNoError(window, media);
            if (window.LatestPresentedFrame is { } frame)
            {
                screen = BasicMachineCompositionTests.DecodeBasicScreen(frame, basic);
                var buttons = media.GetLogicalDescendants().OfType<Button>()
                    .Where(b => Equals(b.Content, "Insert DSK…")).ToArray();
                if (screen.Contains("HC2000", StringComparison.Ordinal)
                    && screen.Contains("I.C.E.", StringComparison.Ordinal)
                    && screen.Contains("FELIX", StringComparison.Ordinal)
                    && buttons.Length == 2 && buttons.All(b => b.IsEnabled)) break;
            }
            await Task.Delay(20, token);
        }
        output.WriteLine("Faithful v2 screen before guest input: " + screen);
        Assert.Equal(16384, window.BootedIf1Length);
        Assert.NotNull(window.CurrentRuntime);
        Assert.False(window.CurrentRuntime!.Completion.IsCompleted);
        Assert.Contains("Real hc2000-v2 whole-chip machine running",
            window.FindControl<TextBlock>("RuntimeStatus")!.Text, StringComparison.Ordinal);
    }
}
