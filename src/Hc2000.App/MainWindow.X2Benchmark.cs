using System.Collections.Immutable;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Hc2000.App.Input;
using Hc2000.App.Runtime;
using Hc2000.Machine;
using Hc2000.Machine.Video;

namespace Hc2000.App;

public partial class MainWindow
{
    private X2BenchmarkResultsPanel? x2BenchmarkPanel;
    private X2BenchmarkResult? lastX2BenchmarkResult;
    private TaskCompletionSource<X2BenchmarkResult>? activeX2BenchmarkTcs;
    private bool x2BenchmarkSaving;

    internal int X2BenchmarkPhaseHoldMilliseconds { get; set; } = X2BenchmarkRunner.DefaultInteractivePhaseHoldMs;
    internal SpeedMode X2BenchmarkSpeed { get; set; } = SpeedMode.Normal;
    internal int X2BenchmarkFrameLimit { get; set; } = X2BenchmarkRunner.DefaultFrameLimit;
    internal byte[]? X2BenchmarkRomOverride { get; set; }
    internal Func<Task<string?>>? PickBenchmarkFolderAsync { get; set; }

    internal X2BenchmarkResultsPanel? X2BenchmarkPanel => x2BenchmarkPanel;
    internal X2BenchmarkResult? LastX2BenchmarkResult => lastX2BenchmarkResult;
    internal Task<X2BenchmarkResult>? ActiveX2BenchmarkCompletion => activeX2BenchmarkTcs?.Task;

    private void WireX2Benchmark()
    {
        x2BenchmarkPanel = new X2BenchmarkResultsPanel();
        var host = this.FindControl<ContentControl>("X2BenchmarkResultsHost")!;
        host.Content = x2BenchmarkPanel;

        var menuButton = this.FindControl<Button>("BenchmarksMenuButton")!;
        var menu = this.FindControl<ContextMenu>("BenchmarksMenu")!;
        var runItem = this.FindControl<MenuItem>("RunX2BenchmarkMenuItem")!;
        var showItem = this.FindControl<MenuItem>("ShowX2BenchmarkResultsMenuItem")!;

        menuButton.Click += (_, _) => menu.Open(menuButton);
        runItem.Click += (_, _) =>
        {
            menu.Close();
            _ = StartX2BenchmarkAsync();
        };
        showItem.Click += (_, _) =>
        {
            menu.Close();
            ShowX2BenchmarkResultsPanel();
        };

        x2BenchmarkPanel.SaveButton.Click += (_, _) => _ = SaveX2BenchmarkResultsAsync();
        x2BenchmarkPanel.RunAgainButton.Click += (_, _) => _ = StartX2BenchmarkAsync();
        x2BenchmarkPanel.CloseButton.Click += (_, _) => HideX2BenchmarkResultsPanel();
    }

    /// <summary>Starts the X2 VGA visual benchmark on the selected session with no setup required:
    /// an X5 + X2 machine runs its eight phases on the VGA window while the main window displays
    /// the ULA progress bar, and opens the results panel when the run finishes.</summary>
    internal async Task<bool> StartX2BenchmarkAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        var session = CurrentSession;
        if (closing || startingConfiguration || session.ReplacingRuntime || session.Closed)
            return false;

        this.FindControl<ContextMenu>("BenchmarksMenu")?.Close();
        HideX2BenchmarkResultsPanel();

        var tcs = new TaskCompletionSource<X2BenchmarkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        activeX2BenchmarkTcs = tcs;

        session.ReplacingRuntime = true;
        presentation.Stop();
        this.FindControl<VideoSurface>("Screen")!.ClearPresentation();
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

            session.ReleaseHeldRead("starting X2 VGA benchmark");
            session.BasicRuntimeActive = false;
            this.FindControl<Media.MediaPanel>("MediaControls")!.Disconnect();
            this.FindControl<Snapshots.HcsPanel>("SnapshotControls")!.Disconnect();

            prestart.IsVisible = false;
            draftInFront = false;
            filesInFront = false;
            SetSideRailOpen(false);
            ArrangeWindowsForX2Benchmark();

            var runGeneration = session.RuntimeGeneration + 1;
            var worker = X2BenchmarkRunner.CreateWorker(
                onPhaseProgress: (phaseNumber, phaseName) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (closing || !ReferenceEquals(CurrentSession, session) || session.RuntimeGeneration != runGeneration)
                            return;
                        this.FindControl<TextBlock>("StatusLine")!.Text =
                            $"Running X2 VGA benchmark — phase {phaseNumber}/{X2BenchmarkRunner.PhaseNames.Length} ({phaseName})…";
                    });
                },
                onCompleted: result =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (closing)
                        {
                            tcs.TrySetCanceled();
                            return;
                        }
                        lastX2BenchmarkResult = result;
                        this.FindControl<MenuItem>("ShowX2BenchmarkResultsMenuItem")!.IsEnabled = true;
                        x2BenchmarkPanel!.Bind(result);
                        this.FindControl<ContentControl>("X2BenchmarkResultsHost")!.IsVisible = true;
                        this.FindControl<TextBlock>("StatusLine")!.Text = result.Completed
                            ? string.Create(CultureInfo.InvariantCulture,
                                $"X2 VGA benchmark finished: {result.Commands.Length} commands across {result.Phases.Length} phases ({result.TotalFrames} frames, median {result.MedianHostMsPerFrame:0.00} ms/frame).")
                            : $"X2 VGA benchmark incomplete: {result.FailureMessage}";
                        tcs.TrySetResult(result);
                    });
                },
                phaseHoldMilliseconds: X2BenchmarkPhaseHoldMilliseconds,
                initialSpeed: X2BenchmarkSpeed,
                frameLimit: X2BenchmarkFrameLimit,
                romOverride: X2BenchmarkRomOverride);

            adoptedProfiles[session] = MachineProfile.Hc2000V2PagedExpansionX5V1.Name;
            adoptedOverlays[session] = false;
            adoptedTurbos[session] = 1;

            AttachRuntime(worker);
            // Offer and open the X2 VGA window immediately at frame 0 so all 8 phases are visible live.
            session.Video.Update(null, new VideoFrame(0, 320, 240, new uint[320 * 240]));
            SyncX2Screen();
            this.FindControl<TextBlock>("StatusLine")!.Text =
                $"Running X2 VGA benchmark — phase 1/{X2BenchmarkRunner.PhaseNames.Length} ({X2BenchmarkRunner.PhaseNames[0]})…";
            return true;
        }
        catch (Exception ex)
        {
            if (!closing)
                this.FindControl<TextBlock>("StatusLine")!.Text = "X2 VGA benchmark failed: " + ex.GetBaseException().Message;
            tcs.TrySetException(ex);
            return false;
        }
        finally
        {
            session.ReplacingRuntime = false;
            RefreshShellView();
        }
    }

    private void ArrangeWindowsForX2Benchmark()
    {
        var current = SelectedX2Screen with { Shown = true };
        if (Screens?.ScreenFromWindow(this)?.WorkingArea is { Width: > 0 } area)
        {
            var scaling = DesktopScaling > 0 ? DesktopScaling : 1.0;
            var mainPixelWidth = (int)Math.Ceiling((FrameSize?.Width ?? Width) * scaling);
            var targetMainX = area.X + 16;
            var targetMainY = Math.Clamp(Position.Y, area.Y + 16, Math.Max(area.Y + 16, area.Bottom - (int)Math.Ceiling(Height * scaling) - 16));
            Position = new PixelPoint(targetMainX, targetMainY);

            var besideX = targetMainX + mainPixelWidth + 8;
            var remainingRight = area.Right - besideX - 8;
            var defaultX2PixelWidth = (int)Math.Ceiling(656 * scaling);
            if (remainingRight >= defaultX2PixelWidth)
            {
                current = current with { X = besideX, Y = targetMainY, Width = 656, Height = 536 };
            }
            else if (remainingRight >= (int)Math.Ceiling(336 * scaling))
            {
                var fitWidth = Math.Clamp((int)(remainingRight / scaling), 336, 656);
                var fitHeight = Math.Clamp((int)Math.Round(fitWidth * 240.0 / 320.0) + 56, 296, 536);
                current = current with { X = besideX, Y = targetMainY, Width = fitWidth, Height = fitHeight };
            }
        }
        StoreX2Screen(workspace.Selected, current);
    }

    internal void ShowX2BenchmarkResultsPanel()
    {
        if (lastX2BenchmarkResult is null || x2BenchmarkPanel is null) return;
        x2BenchmarkPanel.Bind(lastX2BenchmarkResult);
        this.FindControl<ContentControl>("X2BenchmarkResultsHost")!.IsVisible = true;
    }

    internal void HideX2BenchmarkResultsPanel()
    {
        this.FindControl<ContentControl>("X2BenchmarkResultsHost")!.IsVisible = false;
    }

    internal async Task<bool> SaveX2BenchmarkResultsAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (x2BenchmarkSaving || x2BenchmarkPanel is null || lastX2BenchmarkResult is not { Completed: true } result)
            return false;

        x2BenchmarkSaving = true;
        x2BenchmarkPanel.SaveButton.IsEnabled = false;
        try
        {
            var folder = PickBenchmarkFolderAsync is not null
                ? await PickBenchmarkFolderAsync()
                : await OpenSystemBenchmarkFolderPickerAsync();

            if (string.IsNullOrWhiteSpace(folder))
            {
                x2BenchmarkPanel.SaveStatusText.Text = "Folder picker cancelled; benchmark results remain open.";
                this.FindControl<TextBlock>("StatusLine")!.Text = "Folder picker cancelled; benchmark results remain open.";
                return false;
            }

            var written = result.SaveToDirectory(folder);
            x2BenchmarkPanel.LastSavedDirectory = folder;
            x2BenchmarkPanel.LastSavedFiles = [.. written];
            x2BenchmarkPanel.SaveStatusText.Text =
                $"Saved benchmark numbers and 8 VGA phase screenshots to {folder}";
            this.FindControl<TextBlock>("StatusLine")!.Text =
                $"Saved X2 VGA benchmark numbers and 8 VGA phase screenshots to {folder}";
            return true;
        }
        catch (Exception ex)
        {
            x2BenchmarkPanel.SaveStatusText.Text = "Save failed: " + ex.Message;
            this.FindControl<TextBlock>("StatusLine")!.Text = "Benchmark save failed: " + ex.Message;
            return false;
        }
        finally
        {
            x2BenchmarkSaving = false;
            if (lastX2BenchmarkResult?.Completed == true)
                x2BenchmarkPanel.SaveButton.IsEnabled = true;
        }
    }

    private async Task<string?> OpenSystemBenchmarkFolderPickerAsync()
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider
            ?? throw new InvalidOperationException("Folder picker unavailable.");
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose folder to save X2 VGA benchmark numbers and phase screenshots",
            AllowMultiple = false,
        });
        try
        {
            if (folders.Count == 0) return null;
            return folders[0].TryGetLocalPath()
                ?? throw new InvalidOperationException("Only local folders are supported.");
        }
        finally
        {
            foreach (var folder in folders)
                folder.Dispose();
        }
    }
}

/// <summary>Readable in-app panel presenting the X2 VGA benchmark per-phase and per-command numbers,
/// or naming the unfinished phase when a phase does not complete (hc2000-x2-benchmark-in-app).</summary>
public sealed class X2BenchmarkResultsPanel : Border
{
    public TextBlock TitleText { get; }
    public TextBlock SummaryText { get; }
    public TextBlock SaveStatusText { get; }
    public Button SaveButton { get; }
    public Button RunAgainButton { get; }
    public Button CloseButton { get; }
    public Border IncompleteBanner { get; }
    public TextBlock IncompletePhaseTitle { get; }
    public TextBlock IncompletePhaseDetail { get; }
    public StackPanel TablesContainer { get; }
    public Grid PhaseTable { get; }
    public Grid CommandTable { get; }

    public X2BenchmarkResult? CurrentResult { get; private set; }
    public ImmutableArray<X2BenchmarkPhaseRow> DisplayedPhaseRows { get; private set; } = [];
    public ImmutableArray<X2BenchmarkCommandRow> DisplayedCommandRows { get; private set; } = [];
    public string? LastSavedDirectory { get; internal set; }
    public ImmutableArray<string> LastSavedFiles { get; internal set; } = [];

    public X2BenchmarkResultsPanel()
    {
        Name = "X2BenchmarkResultsPanel";
        Padding = new Thickness(12);
        this.Bind(BackgroundProperty, this.GetResourceObservable("AppScreenSurroundBrush"));

        TitleText = new TextBlock
        {
            Name = "X2BenchmarkResultsTitle",
            Classes = { "sectionTitle" },
            Text = "X2 VGA benchmark results",
            VerticalAlignment = VerticalAlignment.Center,
        };

        SaveButton = new Button
        {
            Name = "X2BenchmarkSaveButton",
            Classes = { "primaryAction" },
            Content = "Save results and screenshots…",
            Focusable = false,
        };
        AutomationProperties.SetName(SaveButton, "Save results and screenshots");

        RunAgainButton = new Button
        {
            Name = "X2BenchmarkRunAgainButton",
            Classes = { "secondaryAction" },
            Content = "Run again",
            Focusable = false,
        };
        AutomationProperties.SetName(RunAgainButton, "Run X2 VGA benchmark again");

        CloseButton = new Button
        {
            Name = "X2BenchmarkCloseButton",
            Classes = { "ghostAction" },
            Content = "Close",
            Focusable = false,
        };
        AutomationProperties.SetName(CloseButton, "Close benchmark results panel");

        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { SaveButton, RunAgainButton, CloseButton },
        };

        var headerRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { TitleText, headerActions },
        };
        Grid.SetColumn(headerActions, 1);

        SummaryText = new TextBlock
        {
            Name = "X2BenchmarkSummaryText",
            Classes = { "caption" },
            TextWrapping = TextWrapping.Wrap,
        };

        SaveStatusText = new TextBlock
        {
            Name = "X2BenchmarkSaveStatusText",
            Classes = { "caption" },
            TextWrapping = TextWrapping.Wrap,
        };

        IncompletePhaseTitle = new TextBlock
        {
            Name = "X2BenchmarkIncompleteTitle",
            Classes = { "sectionTitle" },
            TextWrapping = TextWrapping.Wrap,
        };

        IncompletePhaseDetail = new TextBlock
        {
            Name = "X2BenchmarkIncompleteDetail",
            TextWrapping = TextWrapping.Wrap,
        };

        IncompleteBanner = new Border
        {
            Name = "X2BenchmarkIncompleteBanner",
            Classes = { "section" },
            IsVisible = false,
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { IncompletePhaseTitle, IncompletePhaseDetail },
            },
        };

        PhaseTable = new Grid
        {
            Name = "X2BenchmarkPhaseTable",
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto,Auto,Auto"),
            RowSpacing = 4,
            ColumnSpacing = 12,
        };

        CommandTable = new Grid
        {
            Name = "X2BenchmarkCommandTable",
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto"),
            RowSpacing = 4,
            ColumnSpacing = 18,
        };

        var phaseCard = new Border
        {
            Classes = { "section" },
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Classes = { "sectionTitle" },
                        Text = "Per-phase numbers (Z80 T-states at 3.546895 MHz; 5 master ticks per T-state)",
                    },
                    PhaseTable,
                },
            },
        };

        var commandCard = new Border
        {
            Classes = { "section" },
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Classes = { "sectionTitle" },
                        Text = "Per-command summary (Z80 T-states across all 8 phases)",
                    },
                    CommandTable,
                },
            },
        };

        TablesContainer = new StackPanel
        {
            Name = "X2BenchmarkTablesContainer",
            Spacing = 10,
            Children = { phaseCard, commandCard },
        };

        var cardContent = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                headerRow,
                SummaryText,
                SaveStatusText,
                IncompleteBanner,
                TablesContainer,
            },
        };

        var outerCard = new Border
        {
            Classes = { "section" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = cardContent,
            },
        };

        Child = outerCard;
    }

    public void Bind(X2BenchmarkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        CurrentResult = result;
        SaveStatusText.Text = string.Empty;

        if (!result.Completed)
        {
            DisplayedPhaseRows = [];
            DisplayedCommandRows = [];
            PhaseTable.Children.Clear();
            PhaseTable.RowDefinitions.Clear();
            CommandTable.Children.Clear();
            CommandTable.RowDefinitions.Clear();

            TablesContainer.IsVisible = false;
            IncompleteBanner.IsVisible = true;
            SaveButton.IsEnabled = false;

            var phaseLabel = result.IncompletePhaseLabel ?? "unknown phase";
            TitleText.Text = $"X2 VGA benchmark — incomplete ({phaseLabel})";
            SummaryText.Text = $"Profile: {MachineProfile.Hc2000V2PagedExpansionX5V1.Name} + X2 VGA · Stopped in phase {phaseLabel}";
            IncompletePhaseTitle.Text = $"Phase {phaseLabel} did not finish";
            IncompletePhaseDetail.Text = result.FailureMessage ?? $"Phase {phaseLabel} did not complete; a complete benchmark table is not available.";
            return;
        }

        TitleText.Text = "X2 VGA benchmark results";
        SummaryText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"Profile: {MachineProfile.Hc2000V2PagedExpansionX5V1.Name} + X2 VGA (320×240, 256 colours) · {result.Commands.Length} commands across {result.Phases.Length} phases · {result.TotalFrames} emulated frames · median {result.MedianHostMsPerFrame:0.00} host ms/frame · Save writes commands.txt, benchmark.md, benchmark.json, and phase-1-palette-vga.png .. phase-8-done-vga.png.");
        IncompleteBanner.IsVisible = false;
        TablesContainer.IsVisible = true;
        SaveButton.IsEnabled = true;

        DisplayedPhaseRows = result.PhaseRows;
        DisplayedCommandRows = result.CommandRows;

        PopulatePhaseTable(result.PhaseRows);
        PopulateCommandTable(result.CommandRows);
    }

    private void PopulatePhaseTable(ImmutableArray<X2BenchmarkPhaseRow> rows)
    {
        PhaseTable.Children.Clear();
        PhaseTable.RowDefinitions.Clear();
        for (var i = 0; i <= rows.Length; i++)
            PhaseTable.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        string[] headers =
        [
            "Phase",
            "Commands",
            "Card per command",
            "Main CPU wait per command",
            "Send per command",
            "Phase time",
            "Frames",
            "Host ms/frame",
        ];
        for (var col = 0; col < headers.Length; col++)
            AddCell(PhaseTable, 0, col, headers[col], isHeader: true, alignRight: col >= 2, cellName: $"PhaseHeader_{col}");

        for (var r = 0; r < rows.Length; r++)
        {
            var row = rows[r];
            var gridRow = r + 1;
            AddCell(PhaseTable, gridRow, 0, row.PhaseLabel, isHeader: false, alignRight: false, cellName: $"PhaseCell_{row.Phase}_Phase");
            AddCell(PhaseTable, gridRow, 1, row.CommandsSummary, isHeader: false, alignRight: false, cellName: $"PhaseCell_{row.Phase}_Commands");
            AddCell(PhaseTable, gridRow, 2, row.CardPerCommandText, isHeader: false, alignRight: true, cellName: $"PhaseCell_{row.Phase}_Card");
            AddCell(PhaseTable, gridRow, 3, row.WaitPerCommandText, isHeader: false, alignRight: true, cellName: $"PhaseCell_{row.Phase}_Wait");
            AddCell(PhaseTable, gridRow, 4, row.SendPerCommandText, isHeader: false, alignRight: true, cellName: $"PhaseCell_{row.Phase}_Send");
            AddCell(PhaseTable, gridRow, 5, row.PhaseTimeText, isHeader: false, alignRight: true, cellName: $"PhaseCell_{row.Phase}_PhaseTime");
            AddCell(PhaseTable, gridRow, 6, row.FramesText, isHeader: false, alignRight: true, cellName: $"PhaseCell_{row.Phase}_Frames");
            AddCell(PhaseTable, gridRow, 7, row.HostMsText, isHeader: false, alignRight: true, cellName: $"PhaseCell_{row.Phase}_HostMs");
        }
    }

    private void PopulateCommandTable(ImmutableArray<X2BenchmarkCommandRow> rows)
    {
        CommandTable.Children.Clear();
        CommandTable.RowDefinitions.Clear();
        for (var i = 0; i <= rows.Length; i++)
            CommandTable.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        string[] headers =
        [
            "Command",
            "Count",
            "Card (mean)",
            "Main CPU wait (mean)",
            "Wait max",
            "Send (mean)",
        ];
        for (var col = 0; col < headers.Length; col++)
            AddCell(CommandTable, 0, col, headers[col], isHeader: true, alignRight: col > 0, cellName: $"CommandHeader_{col}");

        for (var r = 0; r < rows.Length; r++)
        {
            var row = rows[r];
            var gridRow = r + 1;
            AddCell(CommandTable, gridRow, 0, row.Command, isHeader: false, alignRight: false, cellName: $"CommandCell_{row.Command}_Name");
            AddCell(CommandTable, gridRow, 1, row.CountText, isHeader: false, alignRight: true, cellName: $"CommandCell_{row.Command}_Count");
            AddCell(CommandTable, gridRow, 2, row.CardMeanText, isHeader: false, alignRight: true, cellName: $"CommandCell_{row.Command}_CardMean");
            AddCell(CommandTable, gridRow, 3, row.WaitMeanText, isHeader: false, alignRight: true, cellName: $"CommandCell_{row.Command}_WaitMean");
            AddCell(CommandTable, gridRow, 4, row.WaitMaxText, isHeader: false, alignRight: true, cellName: $"CommandCell_{row.Command}_WaitMax");
            AddCell(CommandTable, gridRow, 5, row.SendMeanText, isHeader: false, alignRight: true, cellName: $"CommandCell_{row.Command}_SendMean");
        }
    }

    private static void AddCell(Grid grid, int row, int column, string text, bool isHeader, bool alignRight, string cellName)
    {
        var block = new TextBlock
        {
            Name = cellName,
            Text = text,
            FontWeight = isHeader ? FontWeight.SemiBold : FontWeight.Normal,
            FontFamily = !alignRight || isHeader ? FontFamily.Default : FontFamily.Parse("monospace"),
            HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = alignRight ? TextWrapping.NoWrap : TextWrapping.Wrap,
            FontSize = 12,
        };
        if (isHeader) block.Classes.Add("caption");
        Grid.SetRow(block, row);
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }
}
