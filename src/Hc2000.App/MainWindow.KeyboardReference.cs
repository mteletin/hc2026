using Avalonia.Controls;
using Avalonia.Interactivity;
using Hc2000.App.Input;

namespace Hc2000.App;

public partial class MainWindow
{
    private KeyboardReferenceWindow? keyboardReference;

    private void OpenKeyboardReference(object? sender, RoutedEventArgs args)
    {
        if (closing) return;
        if (keyboardReference is { } existing)
        {
            RefreshKeyboardReference();
            existing.Activate();
            return;
        }
        KeyboardInput.ReleaseFocus(InputReleaseOrigin.Deactivated);
        var window = new KeyboardReferenceWindow(workspace.Selected?.Configuration.Name ?? "HC2000", KeyboardInput.Mode);
        keyboardReference = window;
        window.Closed += (_, _) =>
        {
            keyboardReference = null;
            if (closing) return;
            Activate();
            this.FindControl<Runtime.VideoSurface>("Screen")?.Focus();
        };
        window.Show(this);
    }

    private void RefreshKeyboardReference() => keyboardReference?.Refresh(
        workspace.Selected?.Configuration.Name ?? "HC2000", KeyboardInput.Mode);
}
