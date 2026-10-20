using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Hc2000.App.Runtime;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Uses only pre-observer APIs: can run on c7ca13f without an API-removal compile failure.</summary>
public sealed class FrontendEvidenceOptInRegressionTests
{
    [AvaloniaFact]
    public async Task OptInWindowUiReadsActualRawAdapterHandoffAndDisableErasesDisplay()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new EmulationWorker(() => new RuntimeTests.Fixture().Machine,
            context => { context.Pause(); ready.TrySetResult(); });
        var window = new MainWindow();
        try
        {
            window.AttachRuntime(runtime);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Show(); window.Focus();
            var parent = Assert.IsType<StackPanel>(window.FindControl<TextBlock>("InputNotice")!.Parent);
            var expander = Assert.Single(parent.Children.OfType<Expander>(), e =>
                Equals(e.Header, "Prospective input evidence — no guest-effect proof"));
            var controls = Assert.IsType<StackPanel>(expander.Content);
            var toggle = Assert.Single(controls.Children.OfType<CheckBox>());
            var read = Assert.Single(controls.Children.OfType<Button>(), b => Equals(b.Content, "Read bounded input evidence"));
            var display = Assert.Single(controls.Children.OfType<TextBlock>());
            Assert.False(toggle.IsChecked == true);
            toggle.IsChecked = true;
            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.None);
            read.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("queue-handoff key-down A", display.Text);
            Assert.Contains("queue-handoff key-up A", display.Text);
            Assert.Contains("enqueued-not-guest-effect", display.Text);
            toggle.IsChecked = false;
            Assert.DoesNotContain("queue-handoff", display.Text);
        }
        finally
        {
            window.Close(); runtime.Dispose();
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
