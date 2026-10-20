using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;

namespace Hc2000.App.Audio;

/// <summary>Says in plain words, next to the speed control it is bound to, what
/// BeeperAudioOutput.ProcessFrame already does: sound is rendered only at 100% host speed, so every
/// other speed is silent. It shows the notice and one button that puts the bound speed field back to
/// 100%; at 100% (or for a value that is not a number, which is an invalid draft rather than a silent
/// machine) nothing is shown. It states the speed rule and never changes it.</summary>
public sealed class SoundSpeedNotice : UserControl
{
    private readonly TextBox speedField;
    private readonly TextBlock notice = new() { Name = "SoundSilenceNotice", TextWrapping = TextWrapping.Wrap };
    private readonly Button restore = new() { Name = "SoundRestoreSpeed", Content = "Set speed to 100% (sound audible)", Focusable = true };

    /// <summary>Raised after the bound field was set back to 100, so the owning panel can push that
    /// speed to the machine it owns (the reconfigure panel applies it; a draft panel has nothing to apply).</summary>
    public event EventHandler? Restored;

    public SoundSpeedNotice(TextBox speedField)
    {
        this.speedField = speedField;
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(notice);
        body.Children.Add(restore);
        Content = body;
        // TextChanged is deferred, so a typed or bound value would not be stated until a later
        // dispatcher turn. The property change itself is synchronous, and that is what the notice follows.
        speedField.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty) Refresh();
        };
        restore.Click += (_, _) =>
        {
            speedField.Text = "100";
            Refresh();
            Restored?.Invoke(this, EventArgs.Empty);
        };
        Refresh();
    }

    public Button RestoreButton => restore;
    public TextBox SpeedField => speedField;
    public string NoticeText => notice.Text ?? "";
    public bool IsShowing => notice.IsVisible;

    /// <summary>Re-reads the bound field and shows or hides the notice. Runs on every field change and
    /// after a panel binds new values, so the words always match the speed that is being set or read.</summary>
    public void Refresh()
    {
        var silent = int.TryParse(speedField.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var speed) && speed != 100;
        notice.Text = silent ? $"Sound is silent at {speed}% speed: sound is rendered only at 100%." : "";
        notice.IsVisible = silent;
        restore.IsVisible = silent;
    }
}
