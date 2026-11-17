using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Hc2000.HostFiles;

namespace Hc2000.App.Explorer;

internal sealed record CpmSearchResultRow(CpmFileLocator.LocateMatch Match)
{
    public override string ToString() =>
        $"{Match.ImagePath} · user {Match.User} · {Match.FileName} · {Match.StoredBytes:N0} bytes";
}

internal sealed record CpmIncompleteRow(CpmFileLocator.IncompleteImage Incomplete)
{
    public override string ToString() =>
        $"[Skipped] {Incomplete.ImagePath}: {Incomplete.Reason}";
}

internal sealed class CpmSearchPanel : Grid
{
    private readonly TextBox patternInput = new()
    {
        Name = "SearchPattern",
        PlaceholderText = "CP/M pattern (e.g. *.TXT, README.*)",
        Width = 220
    };
    private readonly CheckBox recursiveBox = new()
    {
        Name = "SearchRecursive",
        Content = "Subfolders",
        IsChecked = false,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Button findButton = new() { Name = "SearchFindButton", Content = "Find" };
    private readonly Button cancelButton = new() { Name = "SearchCancelButton", Content = "Cancel", IsEnabled = false };
    private readonly Button clearButton = new() { Name = "SearchClearButton", Content = "Clear", IsEnabled = false };
    private readonly Button openResultButton = new() { Name = "SearchOpenResultButton", Content = "Open in Explorer", IsEnabled = false };
    private readonly TextBlock statusNotice = new() { Name = "SearchNotice", TextWrapping = TextWrapping.Wrap };
    private readonly ListBox resultsList = new() { Name = "SearchResults", MaxHeight = 110, IsVisible = false };
    private readonly ListBox incompletesList = new() { Name = "SearchIncompletes", MaxHeight = 70, IsVisible = false };

    private CancellationTokenSource? searchCts;
    private int searchGeneration;

    internal Func<string?>? GetHostFolder { get; set; }
    internal Func<CpmFileLocator.LocateMatch, Task>? OpenMatch { get; set; }

    internal string Pattern
    {
        get => patternInput.Text ?? "";
        set => patternInput.Text = value;
    }

    internal string Notice
    {
        get => statusNotice.Text ?? "";
        set => statusNotice.Text = value;
    }

    internal bool IsSearching => cancelButton.IsEnabled;

    internal CpmFileLocator.LocateMatch? SelectedMatch => (resultsList.SelectedItem as CpmSearchResultRow)?.Match;

    internal IReadOnlyList<CpmFileLocator.LocateMatch> Matches =>
        (resultsList.ItemsSource as IEnumerable<CpmSearchResultRow>)?.Select(r => r.Match).ToArray() ?? [];

    internal IReadOnlyList<CpmFileLocator.IncompleteImage> Incompletes =>
        (incompletesList.ItemsSource as IEnumerable<CpmIncompleteRow>)?.Select(r => r.Incomplete).ToArray() ?? [];

    internal CpmSearchPanel()
    {
        RowDefinitions = new RowDefinitions("Auto,Auto,Auto");

        var controlsBar = new StackPanel { Spacing = 4 };
        var topRow = new WrapPanel { Orientation = Orientation.Horizontal };
        topRow.Children.Add(new TextBlock
        {
            Text = "Find CP/M files:",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 8, 0)
        });
        topRow.Children.Add(patternInput);
        topRow.Children.Add(recursiveBox);
        topRow.Children.Add(findButton);
        topRow.Children.Add(cancelButton);
        topRow.Children.Add(clearButton);
        topRow.Children.Add(openResultButton);

        controlsBar.Children.Add(topRow);
        controlsBar.Children.Add(statusNotice);

        SetRow(controlsBar, 0);
        Children.Add(controlsBar);

        SetRow(resultsList, 1);
        Children.Add(resultsList);

        SetRow(incompletesList, 2);
        Children.Add(incompletesList);

        statusNotice.Text = "Select a host folder and enter a CP/M pattern to search across floppy images.";

        findButton.Click += async (_, _) => await StartSearchAsync();
        cancelButton.Click += (_, _) => CancelSearch();
        clearButton.Click += (_, _) => ClearSearch();
        openResultButton.Click += async (_, _) => await OpenSelectedMatchAsync();

        patternInput.AddHandler(InputElement.KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                await StartSearchAsync();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        resultsList.SelectionChanged += async (_, _) =>
        {
            openResultButton.IsEnabled = SelectedMatch is not null;
            if (SelectedMatch is not null)
            {
                await OpenSelectedMatchAsync();
            }
        };

        resultsList.DoubleTapped += async (_, _) => await OpenSelectedMatchAsync();
        resultsList.AddHandler(InputElement.KeyDownEvent, async (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                await OpenSelectedMatchAsync();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    internal void SetNotice(string message)
    {
        statusNotice.Text = message;
    }

    internal async Task StartSearchAsync()
    {
        var pattern = patternInput.Text?.Trim();
        if (string.IsNullOrEmpty(pattern))
        {
            statusNotice.Text = "Please enter a CP/M filename pattern (e.g. *.TXT, README.*).";
            return;
        }

        try
        {
            CpmFileLocator.ValidatePattern(pattern);
        }
        catch (ArgumentException ex)
        {
            statusNotice.Text = ex.Message;
            return;
        }

        var folder = GetHostFolder?.Invoke();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            statusNotice.Text = string.IsNullOrWhiteSpace(folder)
                ? "Please select or open a host folder to search."
                : $"Selected folder does not exist: '{folder}'.";
            return;
        }

        var isRecursive = recursiveBox.IsChecked == true;
        int currentGen = ++searchGeneration;
        bool searchActive = true;

        searchCts?.Cancel();
        searchCts?.Dispose();
        var cts = new CancellationTokenSource();
        searchCts = cts;

        findButton.IsEnabled = false;
        cancelButton.IsEnabled = true;
        clearButton.IsEnabled = true;
        openResultButton.IsEnabled = false;
        resultsList.ItemsSource = null;
        resultsList.IsVisible = false;
        incompletesList.ItemsSource = null;
        incompletesList.IsVisible = false;
        statusNotice.Text = "Searching candidate floppy images…";

        CpmFileLocator.SearchResult? result = null;
        string? failureMessage = null;

        try
        {
            await Task.Run(() =>
            {
                NativeImageAdmission? admission = null;
                try
                {
                    try
                    {
                        var acquired = NativeImageAdmission.TryAcquireColdRead(cts.Token);
                        admission = acquired.Admission;
                    }
                    catch (Exception ex)
                    {
                        failureMessage = "Cold read refused: " + ex.Message;
                        return;
                    }

                    result = CpmFileLocator.SearchFolder(
                        folder: folder,
                        pattern: pattern,
                        userFilter: null,
                        recursive: isRecursive,
                        format: "auto",
                        geometry: null,
                        admission: admission,
                        cancellationToken: cts.Token,
                        onCheckingImage: relPath =>
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                if (currentGen == searchGeneration && searchActive && !cts.IsCancellationRequested)
                                {
                                    statusNotice.Text = $"Searching: checking {relPath}…";
                                }
                            });
                        });
                }
                catch (Exception ex)
                {
                    failureMessage = ex.Message;
                }
                finally
                {
                    admission?.Dispose();
                }
            });
        }
        finally
        {
            searchActive = false;
        }

        if (cts != searchCts) return;

        findButton.IsEnabled = true;
        cancelButton.IsEnabled = false;

        if (failureMessage is not null)
        {
            statusNotice.Text = failureMessage;
            return;
        }

        if (result is null) return;

        if (result.Cancelled)
        {
            statusNotice.Text = $"Search cancelled. {result.Matches.Count} match(es) found across {result.ImagesChecked} image(s) checked.";
        }
        else
        {
            bool hasIncompletes = result.Incompletes.Count > 0;
            if (result.Matches.Count > 0)
            {
                if (hasIncompletes)
                {
                    statusNotice.Text = $"Search incomplete ({result.Incompletes.Count} candidate image(s) skipped): {result.Matches.Count} match(es) found across {result.ImagesChecked} image(s) checked.";
                }
                else
                {
                    statusNotice.Text = $"Found {result.Matches.Count} match(es) across {result.ImagesChecked} image(s) checked.";
                }
            }
            else
            {
                if (result.ImagesChecked == 0)
                {
                    statusNotice.Text = "No supported CP/M images found in folder.";
                }
                else if (hasIncompletes)
                {
                    statusNotice.Text = $"Search incomplete: no matching files found on readable images ({result.Incompletes.Count} candidate image(s) skipped).";
                }
                else
                {
                    statusNotice.Text = $"No matching files found in {result.ImagesChecked} image(s) checked.";
                }
            }
        }

        if (result.Matches.Count > 0)
        {
            resultsList.ItemsSource = result.Matches.Select(m => new CpmSearchResultRow(m)).ToArray();
            resultsList.IsVisible = true;
        }

        if (result.Incompletes.Count > 0)
        {
            incompletesList.ItemsSource = result.Incompletes.Select(inc => new CpmIncompleteRow(inc)).ToArray();
            incompletesList.IsVisible = true;
        }
    }

    internal void CancelSearch()
    {
        searchCts?.Cancel();
    }

    internal void ClearSearch()
    {
        searchCts?.Cancel();
        searchCts?.Dispose();
        searchCts = null;
        patternInput.Text = "";
        statusNotice.Text = "Search cleared.";
        resultsList.ItemsSource = null;
        resultsList.IsVisible = false;
        incompletesList.ItemsSource = null;
        incompletesList.IsVisible = false;
        findButton.IsEnabled = true;
        cancelButton.IsEnabled = false;
        clearButton.IsEnabled = false;
        openResultButton.IsEnabled = false;
    }

    private async Task OpenSelectedMatchAsync()
    {
        if (SelectedMatch is { } match && OpenMatch is { } action)
        {
            await action(match);
        }
    }
}
