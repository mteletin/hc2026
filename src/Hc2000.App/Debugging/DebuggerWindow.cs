using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Hc2000.Disasm;

namespace Hc2000.App.Debugging;

public enum DebuggerCommandKind { Resume, Step, AddBreakpoint, ClearBreakpoints, StartRecording, StopRecording, ExportTextMap, ExportBinaryMap }
public sealed record DebuggerCommand(DebuggerCommandKind Kind, Breakpoint? Breakpoint = null);

/// <summary>Displays immutable copies only. The optional command sink must enqueue to the runtime
/// owner, not access live state on the UI thread. Without a sink this window is explicitly disconnected.</summary>
public sealed class DebuggerWindow : Window
{
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox address = new() { Text = "0000", Width = 90 };
    private readonly TextBox control = new() { Text = "entry 0000", AcceptsReturn = true, MinHeight = 80, PlaceholderText = "Control-file directives (entry/label/port/data…)" };
    private readonly TextBox code = Panel();
    private readonly TextBox memory = Panel();
    private readonly TextBox registers = Panel();
    private readonly TextBox fdc = Panel();
    private readonly TextBox breakpointList = Panel();
    private readonly TextBlock errors = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Action<DebuggerCommand>? enqueue;
    private readonly Button pause;
    private Action? pauseAction;
    private DebuggerSnapshot? snapshot;
    private long generation;
    private long sequence;
    private bool paused;
    // Set when the view is explicitly detached; null while a machine is bound.
    private string? detached;

    public DebuggerWindow(Action<DebuggerCommand>? enqueue = null)
    {
        this.enqueue = enqueue;
        Title = "HC2000 debugger"; Width = 940; Height = 680;
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        // Pause is the one control this window drives, and it goes through the existing FIFO, not the
        // command sink: the sink stays null, so the unimplemented commands below stay visibly disabled.
        pause = new Button { Content = "Pause machine", IsEnabled = false, Margin = new Thickness(4) };
        pause.Click += (_, _) => pauseAction?.Invoke();
        toolbar.Children.Add(pause);
        foreach (var (name, kind) in new[] { ("Resume", DebuggerCommandKind.Resume), ("Step", DebuggerCommandKind.Step),
            ("Record", DebuggerCommandKind.StartRecording), ("Stop recording", DebuggerCommandKind.StopRecording),
            ("Save text map", DebuggerCommandKind.ExportTextMap), ("Save binary map", DebuggerCommandKind.ExportBinaryMap) })
            toolbar.Children.Add(CommandButton(name, kind));
        var refresh = new Button { Content = "Inspect copied range" }; refresh.Click += (_, _) => Render();
        toolbar.Children.Add(address); toolbar.Children.Add(refresh);
        var codePanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        codePanel.Children.Add(control); Grid.SetRow(code, 1); codePanel.Children.Add(code);
        var tabs = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem { Header = "Code / labels", Content = codePanel }, new TabItem { Header = "Memory / page map", Content = memory },
                new TabItem { Header = "Registers", Content = registers }, new TabItem { Header = "FDC", Content = fdc },
                new TabItem { Header = "Breakpoints", Content = BreakpointPanel() }
            }
        };
        var layout = new Grid { Margin = new Thickness(12), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        layout.Children.Add(status); Grid.SetRow(toolbar, 1); layout.Children.Add(toolbar);
        Grid.SetRow(tabs, 2); layout.Children.Add(tabs); Grid.SetRow(errors, 3); layout.Children.Add(errors);
        Content = layout;
        Render();
    }

    private static TextBox Panel() => new()
    {
        IsReadOnly = true, AcceptsReturn = true, FontFamily = FontFamily.Parse("monospace"),
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
    };

    private Button CommandButton(string caption, DebuggerCommandKind kind)
    {
        var button = new Button { Content = caption, IsEnabled = enqueue is not null, Margin = new Thickness(4) };
        button.Click += (_, _) => enqueue?.Invoke(new(kind));
        return button;
    }

    private Control BreakpointPanel()
    {
        var kind = new ComboBox { ItemsSource = Enum.GetValues<BreakpointKind>(), SelectedIndex = 0 };
        var start = new TextBox { Text = "0000", PlaceholderText = "Start (hex)" };
        var end = new TextBox { Text = "FFFF", PlaceholderText = "Inclusive end (hex)" };
        var add = new Button { Content = "Add breakpoint", IsEnabled = enqueue is not null };
        add.Click += (_, _) =>
        {
            if (kind.SelectedItem is not BreakpointKind selected || !ushort.TryParse(start.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a) ||
                !ushort.TryParse(end.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b) || a > b)
            { errors.Text = "Invalid breakpoint range (four hexadecimal digits, start <= end)."; return; }
            enqueue?.Invoke(new(DebuggerCommandKind.AddBreakpoint, new(selected, a, b)));
            errors.Text = "Breakpoint command queued; applied by the emulation owner at a boundary.";
        };
        return new StackPanel { Spacing = 8, Children =
        {
            new TextBlock { Text = "PC stops before Step. Memory/port/paging hits stop before the next Step, never mid-instruction.", TextWrapping = TextWrapping.Wrap },
            kind, start, end, add, CommandButton("Clear breakpoints", DebuggerCommandKind.ClearBreakpoints),
            new TextBlock { Text = "Applied breakpoints at last captured boundary:" }, breakpointList
        } };
    }

    /// <summary>Binds or clears the pause action. Enabled only while bound; it enqueues the existing
    /// pause control and never touches the command sink, so the other toolbar buttons stay disabled.</summary>
    public void BindPause(Action? action)
    {
        Dispatcher.UIThread.VerifyAccess(); pauseAction = action; pause.IsEnabled = action is not null;
    }

    /// <summary>Call on the UI dispatcher with a copied boundary view and the stamps it was captured
    /// with. No live component reference crosses this boundary.</summary>
    public void Publish(DebuggerSnapshot view, long generation, long sequence, bool paused)
    {
        Dispatcher.UIThread.VerifyAccess();
        snapshot = view; this.generation = generation; this.sequence = sequence; this.paused = paused;
        detached = null; Render();
    }

    /// <summary>Drops the displayed copy and says why the view is no longer following a machine.
    /// The wording is the caller's: a lost selection, a machine awaiting its first copy, and a
    /// failed worker read differently.</summary>
    public void Detach(string reason)
    {
        Dispatcher.UIThread.VerifyAccess();
        snapshot = null; detached = reason; BindPause(null); Render();
    }

    /// <summary>Invalidates the retired generation without disconnecting the selected-session FIFO.</summary>
    public void AwaitSnapshot()
    {
        Dispatcher.UIThread.VerifyAccess();
        snapshot = null; detached = null; Render();
    }

    private void Render()
    {
        status.Text = detached is not null ? detached
            : snapshot is null ? "Selected machine awaiting snapshot."
            : (pauseAction is not null ? "Following selected machine. "
                : enqueue is null ? "Disconnected: runtime controls unavailable. " : "Commands are queued to the runtime owner. ") +
                $"{snapshot.Source} — last captured boundary T={snapshot.CpuTStates}, PC={snapshot.Registers.Pc:X4}, " +
                $"gen={generation} seq={sequence} paused={(paused ? "yes" : "no")}. Display is a copied snapshot, not live state.";
        if (snapshot is null)
        {
            code.Text = memory.Text = registers.Text = fdc.Text = breakpointList.Text = "Unavailable: no boundary snapshot."; return;
        }
        registers.Text = DebuggerDisplay.Registers(snapshot); fdc.Text = DebuggerDisplay.Fdc(snapshot);
        breakpointList.Text = snapshot.Breakpoints.Count == 0 ? "None" :
            string.Join("\n", snapshot.Breakpoints.Select(b => $"{b.Kind} {b.Start:X4}-{b.End:X4}"));
        if (!ushort.TryParse(address.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a))
        { errors.Text = "Invalid inspection address (hex)."; return; }
        var length = Math.Min(256, 65536 - a);
        memory.Text = DebuggerDisplay.Memory(snapshot, a, length);
        var result = snapshot.Disassemble(a, length, ControlFile.Parse(control.Text ?? ""));
        code.Text = result.Source;
        errors.Text = string.Join("\n", result.Diagnostics.Select(d => $"Line {d.Line}: {d.Message}"));
    }
}
