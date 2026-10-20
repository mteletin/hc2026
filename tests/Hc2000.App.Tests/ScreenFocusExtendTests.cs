using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Hc2000.App.Input;
using Hc2000.App.Runtime;
using Hc2000.Machine.Input;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class ScreenFocusExtendTests(ITestOutputHelper output)
{
    private static bool Down(KeyboardMatrix matrix, HcKey key)
        => (matrix.Read((ushort)(0xffff ^ (1 << ((int)key / 5 + 8)))) & (1 << ((int)key % 5))) == 0;

    [AvaloniaFact]
    public async Task ScreenTakesFocusOnOpenClickAndMappingClose()
    {
        var window = new MainWindow(new LaunchOptions(null, null, true, false)); window.Show();
        try
        {
            // Explicit automation Start is asynchronous. Exercise the actual running frame,
            // not a synthetic image while startup is still changing the shell geometry.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (window.BootedIf1Length == 0 || window.LatestPresentedFrame is null)
                await Task.Delay(5, deadline.Token);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(window.UpdateLayout,
                Avalonia.Threading.DispatcherPriority.Background);
            Assert.False(window.FindControl<Border>("ConfigurationCard")!.IsVisible);
            var screen = window.FindControl<VideoSurface>("Screen")!;
            var box = window.FindControl<ComboBox>("InputModeBox")!;
            Assert.True(screen.IsFocused);
            var pointerObservation = 0;
            void ObservePointer(string phase, object? source = null, bool? handled = null)
            {
                if (++pointerObservation > 20) return;
                var point = screen.TranslatePoint(new Avalonia.Point(20, 20), window);
                var hit = point is { } p ? window.GetVisualAt(p) : null;
                output.WriteLine($"pointer#{pointerObservation} hostStopwatch={System.Diagnostics.Stopwatch.GetTimestamp()} " +
                    $"phase={phase} point={point} windowBounds={window.Bounds} screenBounds={screen.Bounds} " +
                    $"hit={hit?.GetType().Name}/{(hit as Control)?.Name} " +
                    $"source={source?.GetType().Name}/{(source as Control)?.Name} handled={handled} " +
                    $"screenFocused={screen.IsFocused} mappingFocused={box.IsFocused}");
                foreach (var scroll in window.GetVisualDescendants().OfType<ScrollViewer>().Take(4))
                    output.WriteLine($"scroll name={scroll.Name} offset={scroll.Offset} viewport={scroll.Viewport} extent={scroll.Extent}");
            }
            window.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
            {
                ObservePointer($"PointerPressed tunnel eventPoint={e.GetPosition(window)}", e.Source, e.Handled);
            }, RoutingStrategies.Tunnel, handledEventsToo: true);
            window.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
                ObservePointer("PointerPressed bubble", e.Source, e.Handled),
                RoutingStrategies.Bubble, handledEventsToo: true);
            window.UpdateLayout();
            ObservePointer("after layout");
            box.Focus(); Assert.True(box.IsFocused);
            ObservePointer("after original mapping focus / before pointer");
            screen.BringIntoView();
            window.UpdateLayout();
            var translated = screen.TranslatePoint(new Point(20, 20), window);
            Assert.NotNull(translated);
            var point = translated!.Value;
            Assert.True(new Rect(window.ClientSize).Contains(point));
            Assert.True(box.IsFocused);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            ObservePointer("after original pointer");
            Assert.True(screen.IsFocused);
            box.Focus(); box.IsDropDownOpen = true; box.IsDropDownOpen = false;
            ObservePointer("after original mapping close");
            Assert.True(screen.IsFocused);
        }
        finally { window.Close(); await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [AvaloniaFact]
    public void TunnelRoutesBeforeFocusedControlCanSwallowKeysButExemptsPasteEditor()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            var box = window.FindControl<ComboBox>("InputModeBox")!;
            box.AddHandler(InputElement.KeyDownEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel);
            box.AddHandler(InputElement.KeyUpEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel);
            box.Focus();
            var matrix = new KeyboardMatrix(); var controller = new InputController(window.InputQueue);
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.D1 });
            controller.ApplyPending(0, matrix); Assert.True(Down(matrix, HcKey.D1));
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.D1 });
            controller.ApplyPending(1, matrix); Assert.Equal(31, matrix.Read(0x00fe));
            var editor = window.FindControl<TextBox>("PasteTextBox")!;
            editor.Focus();
            editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A });
            editor.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "a" });
            controller.ApplyPending(2, matrix); Assert.Equal(31, matrix.Read(0x00fe));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SymbolicLoneCtrlClosesBothShiftsAndReleases()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            window.KeyboardInput.Mode = KeyMappingMode.Symbolic;
            var matrix = new KeyboardMatrix(); var controller = new InputController(window.InputQueue);
            foreach (var key in new[] { Key.LeftCtrl, Key.RightCtrl })
            {
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = KeyModifiers.Control });
                controller.ApplyPending(0, matrix);
                Assert.True(Down(matrix, HcKey.CapsShift)); Assert.True(Down(matrix, HcKey.SymbolShift));
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key });
                controller.ApplyPending(1, matrix); Assert.Equal(31, matrix.Read(0x00fe));
            }
        }
        finally { window.Close(); }
    }
}
