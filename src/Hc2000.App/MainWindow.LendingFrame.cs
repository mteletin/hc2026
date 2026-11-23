using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using Hc2000.App.Input;
using Hc2000.App.Runtime;
using Hc2000.Machine;
using Hc2000.Machine.Video;

namespace Hc2000.App;

// hc2000-expansion-lending-interrupts: the one-frame lending demo on the Benchmarks menu. A cold
// X5 + X2 machine runs the shipped ROM; four units each draw one band of the frame into their own
// pages, lend the pages to the X2 card, the card DMAs the bands up and presents the frame, and the
// main Z80 advances only because the row's interrupt source wakes it. The X2 VGA window holds the
// final four-band picture and the status line reports the counted interrupt sources as text.
public partial class MainWindow
{
    private TaskCompletionSource<LendingFrameRunResult>? activeLendingFrameTcs;

    internal Task<LendingFrameRunResult>? ActiveLendingFrameCompletion => activeLendingFrameTcs?.Task;
    internal LendingFrameRunResult? LastLendingFrameResult { get; private set; }
    internal int LendingFrameFrameLimit { get; set; } = LendingFrameRunner.DefaultFrameLimit;
    internal SpeedMode LendingFrameSpeed { get; set; } = SpeedMode.Normal;

    private void WireLendingFrame()
    {
        var runItem = this.FindControl<MenuItem>("RunLendingFrameDemoMenuItem")!;
        runItem.Click += (_, _) =>
        {
            this.FindControl<ContextMenu>("BenchmarksMenu")?.Close();
            _ = StartLendingFrameDemoAsync();
        };
    }

    /// <summary>Starts the one-frame lending demo with no setup required: a cold X5 + X2 machine runs
    /// the shipped ROM on the X2 VGA window and reports the counted interrupt sources when it finishes.</summary>
    internal async Task<bool> StartLendingFrameDemoAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        var session = CurrentSession;
        if (closing || startingConfiguration || session.ReplacingRuntime || session.Closed)
            return false;

        this.FindControl<ContextMenu>("BenchmarksMenu")?.Close();
        HideX2BenchmarkResultsPanel();

        var tcs = new TaskCompletionSource<LendingFrameRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        activeLendingFrameTcs = tcs;

        session.ReplacingRuntime = true;
        presentation.Stop();
        this.FindControl<Runtime.VideoSurface>("Screen")!.ClearPresentation();
        ClearX2Presentation(session);

        var old = session.Worker;
        session.KeyboardInput.ReleaseFocus(InputReleaseOrigin.Reset);
        session.KeyboardInput.Evidence.EndSession();
        ResetInputEvidenceView();
        session.DetachWorker();
        session.BumpGeneration();
        old?.Dispose();

        try
        {
            if (old is not null)
            {
                try { await old.Completion; }
                catch { }
            }
            if (closing || session.Closed) return false;

            session.ReleaseHeldRead("starting lending one-frame demo");
            session.BasicRuntimeActive = false;
            this.FindControl<Media.MediaPanel>("MediaControls")!.Disconnect();
            this.FindControl<Snapshots.HcsPanel>("SnapshotControls")!.Disconnect();

            prestart.IsVisible = false;
            draftInFront = false;
            filesInFront = false;
            SetSideRailOpen(false);
            ArrangeWindowsForX2Benchmark();

            var runGeneration = session.RuntimeGeneration + 1;
            var worker = LendingFrameRunner.CreateWorker(
                onCompleted: result =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (closing || !ReferenceEquals(CurrentSession, session) || session.RuntimeGeneration != runGeneration)
                        {
                            tcs.TrySetCanceled();
                            if (ReferenceEquals(activeLendingFrameTcs, tcs))
                                activeLendingFrameTcs = null;
                            return;
                        }
                        LastLendingFrameResult = result;
                        this.FindControl<TextBlock>("StatusLine")!.Text = result.Completed
                            ? string.Create(CultureInfo.InvariantCulture,
                                $"Lending one-frame demo finished: {result.FrameWidth}x{result.FrameHeight}, {result.Frames} frames; unit-done {result.UnitDone}, dma-complete {result.DmaDone}, frame-presented {result.FramePresented}.")
                            : "Lending one-frame demo incomplete: " + result.FailureMessage;
                        tcs.TrySetResult(result);
                        if (ReferenceEquals(activeLendingFrameTcs, tcs))
                            activeLendingFrameTcs = null;
                    });
                },
                frameLimit: LendingFrameFrameLimit,
                initialSpeed: LendingFrameSpeed);

            adoptedProfiles[session] = MachineProfile.Hc2000V2PagedExpansionX5V1.Name;
            adoptedOverlays[session] = false;
            adoptedTurbos[session] = 1;

            AttachRuntime(worker);
            // Offer and open the X2 VGA window immediately at frame 0 so the four bands are visible live.
            session.Video.Update(null, new VideoFrame(0, 320, 240, new uint[320 * 240]));
            SyncX2Screen();
            this.FindControl<TextBlock>("StatusLine")!.Text = "Running lending one-frame demo…";
            return true;
        }
        catch (Exception ex)
        {
            if (!closing)
                this.FindControl<TextBlock>("StatusLine")!.Text = "Lending one-frame demo failed: " + ex.GetBaseException().Message;
            tcs.TrySetException(ex);
            return false;
        }
        finally
        {
            session.ReplacingRuntime = false;
            RefreshShellView();
        }
    }
}
