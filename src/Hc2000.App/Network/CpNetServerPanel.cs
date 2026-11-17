using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Network;

// This view edits the selected session's configuration; only Start initiates host work.
internal sealed class CpNetServerPanel : StackPanel
{
    private readonly TextBox image, identity, identityPassword, login, address, port, serverId, resourceDisk;
    private readonly ComboBox format, preset;
    private readonly TextBlock identityStatus, publicIdentity, loginStatus, serverStatus, loginCount, notice;
    private readonly Button start, stop;
    private CpNetServerBridge? bridge;
    private CpNetServerSettings? settings;
    private CpNetServerStatus status = new(CpNetServerState.Unknown, false);
    private bool loading, busy;
    private long binding;
    private Task<CpNetServerStatus>? observation;
    private CpNetServerBridge? observationSource;
    private CancellationTokenSource? pollTimer;

    public CpNetServerPanel()
    {
        Spacing = 6;
        Children.Add(new TextBlock { Text = "Serve a CP/M resource (read-only snapshot)", FontSize = 16, TextWrapping = TextWrapping.Wrap });
        Children.Add(new TextBlock { Text = "Changes appear after Stop, then Start. Starting the server does not connect the guest.", TextWrapping = TextWrapping.Wrap });
        Children.Add(new TextBlock { Text = "CP/M disk image" });
        image = new TextBox { Name = "CpNetExportImage" };
        Children.Add(PickerRow(image, "Select CP/M disk…", false));
        Children.Add(new TextBlock { Text = "Image format" });
        format = new ComboBox { ItemsSource = new[] { "DSK container", "Raw floppy", "Raw HDD volume", "Raw paged M volume" } };
        Children.Add(format);
        Children.Add(new TextBlock { Text = "Floppy DPB (for DSK or raw floppy)" });
        preset = new ComboBox { ItemsSource = Dpb.Presets().Select(value => value.Name).ToArray() };
        Children.Add(preset);
        Children.Add(new TextBlock { Text = "Listen address" });
        Children.Add(address = new TextBox { Name = "CpNetListenAddress" });
        Children.Add(new TextBlock { Text = "Port" });
        Children.Add(port = new TextBox { Name = "CpNetListenPort" });
        Children.Add(new TextBlock { Text = "Server ID (0–254)" });
        Children.Add(serverId = new TextBox());
        Children.Add(new TextBlock { Text = "Advertised resource disk (0–15)" });
        Children.Add(resourceDisk = new TextBox());
        Children.Add(new TextBlock { Text = "TLS server identity (PFX)" });
        identity = new TextBox { Name = "CpNetIdentityPath" };
        Children.Add(PickerRow(identity, "Select PFX…", true));
        identityStatus = new TextBlock(); Children.Add(identityStatus);
        publicIdentity = new TextBlock { TextWrapping = TextWrapping.Wrap }; Children.Add(publicIdentity);
        Children.Add(new TextBlock { Text = "PFX unlock password (if needed)" });
        identityPassword = new TextBox { PasswordChar = '●', PlaceholderText = "Leave unchanged to keep the saved value" };
        var pfxRow = new StackPanel { Spacing = 6 };
        pfxRow.Children.Add(identityPassword);
        var clearIdentity = new Button { Content = "Clear unlock" };
        pfxRow.Children.Add(clearIdentity); Children.Add(pfxRow);
        Children.Add(new TextBlock { Text = "CP/NET LOGIN password (exactly 8 printable ASCII characters)", TextWrapping = TextWrapping.Wrap });
        login = new TextBox { PasswordChar = '●', PlaceholderText = "Enter or replace password" };
        var loginRow = new StackPanel { Spacing = 6 };
        loginRow.Children.Add(login);
        var clearLogin = new Button { Content = "Clear LOGIN" };
        loginRow.Children.Add(clearLogin); Children.Add(loginRow);
        loginStatus = new TextBlock(); Children.Add(loginStatus);
        Children.Add(new TextBlock { Text = "Clients must trust the certificate issuer in their OS and connect using a name or IP covered by the certificate.", TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(start = new Button { Content = "Start server" });
        buttons.Children.Add(stop = new Button { Content = "Stop server" });
        Children.Add(buttons);
        serverStatus = new TextBlock(); Children.Add(serverStatus);
        loginCount = new TextBlock(); Children.Add(loginCount);
        notice = new TextBlock { TextWrapping = TextWrapping.Wrap }; Children.Add(notice);

        ObserveText(image, (value, text) => value.ImagePath = text);
        ObserveText(identity, (value, text) => value.IdentityPath = text);
        ObserveText(address, (value, text) => value.Address = text);
        ObserveText(port, (value, text) => value.Port = text);
        ObserveText(serverId, (value, text) => value.ServerId = text);
        ObserveText(resourceDisk, (value, text) => value.ResourceDisk = text);
        ObserveText(identityPassword, (value, text) => value.SetIdentityPassword(text.AsSpan()));
        ObserveText(login, (value, text) => value.SetLoginPassword(text.AsSpan()));
        format.SelectionChanged += (_, _) => Change(value => value.Format = (CpNetResourceFormat)format.SelectedIndex);
        preset.SelectionChanged += (_, _) => Change(value => value.FloppyPreset = preset.SelectedIndex);
        clearIdentity.Click += (_, _) => { identityPassword.Text = ""; Change(value => value.SetIdentityPassword(ReadOnlySpan<char>.Empty)); };
        clearLogin.Click += (_, _) => { login.Text = ""; Change(value => value.SetLoginPassword(ReadOnlySpan<char>.Empty)); };
        start.Click += async (_, _) => await RunAsync(CpNetServerAction.Start);
        stop.Click += async (_, _) => await RunAsync(CpNetServerAction.Stop);
        Render(status);
    }

    internal TextBox ImageField => image;
    internal TextBox IdentityField => identity;
    internal TextBox IdentityPasswordField => identityPassword;
    internal TextBox LoginField => login;
    internal TextBox PortField => port;
    internal Button StartButton => start;
    internal Button StopButton => stop;
    internal string ServerLine => serverStatus.Text ?? "";
    internal string LoginLine => loginCount.Text ?? "";
    internal string IdentityLine => publicIdentity.Text ?? "";
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(400);

    private StackPanel PickerRow(TextBox field, string label, bool certificate)
    {
        var row = new StackPanel { Spacing = 6 };
        row.Children.Add(field);
        var button = new Button { Content = label };
        row.Children.Add(button);
        button.Click += async (_, _) => await PickAsync(field, certificate);
        return row;
    }

    private async Task PickAsync(TextBox field, bool certificate)
    {
        var generation = binding;
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException();
            var files = await provider.OpenFilePickerAsync(new() { Title = certificate ? "Select TLS server identity" : "Select CP/M disk image", AllowMultiple = false });
            try
            {
                if (generation != binding || settings is null || files.Count == 0) return;
                field.Text = files[0].TryGetLocalPath() ?? throw new InvalidOperationException();
            }
            finally { foreach (var file in files) file.Dispose(); }
        }
        catch { if (generation == binding) notice.Text = "Select a local file."; }
    }

    internal void Attach(CpNetServerBridge value, CpNetServerSettings sessionSettings)
    {
        binding++; bridge = value; settings = sessionSettings; loading = true;
        image.Text = settings.ImagePath; identity.Text = settings.IdentityPath;
        address.Text = settings.Address; port.Text = settings.Port; serverId.Text = settings.ServerId; resourceDisk.Text = settings.ResourceDisk;
        format.SelectedIndex = (int)settings.Format; preset.SelectedIndex = settings.FloppyPreset;
        identityPassword.Text = ""; login.Text = ""; loading = false;
        Render(new(CpNetServerState.Unknown, false));
        if (!busy) _ = ObserveAsync();
    }

    internal void Unbind()
    {
        binding++; bridge = null; settings = null;
        pollTimer?.Cancel(); pollTimer = null;
        loading = true; identityPassword.Text = ""; login.Text = ""; loading = false;
        Render(new(CpNetServerState.Unknown, false));
    }

    private void Change(Action<CpNetServerSettings> change)
    {
        if (loading || settings is null) return;
        change(settings);
        UpdateControls();
    }

    private void ObserveText(TextBox field, Action<CpNetServerSettings, string> change)
    {
        field.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.TextProperty) Change(value => change(value, field.Text ?? ""));
        };
    }

    private async Task ObserveAsync()
    {
        var source = bridge;
        if (source is null || busy) return;
        var generation = binding;
        Task<CpNetServerStatus> request;
        try { request = source.RequestAsync(CpNetServerAction.Observe); observation = request; observationSource = source; }
        catch { return; }
        try
        {
            var reply = await request;
            if (ReferenceEquals(source, bridge) && generation == binding && !busy) Render(reply);
        }
        catch { }
        finally { if (ReferenceEquals(request, observation)) { observation = null; observationSource = null; } }
    }

    internal async Task RunAsync(CpNetServerAction action)
    {
        var source = bridge;
        var selected = settings;
        if (source is null || selected is null || busy) return;
        var generation = binding;
        busy = true; UpdateControls();
        try
        {
            if (ReferenceEquals(observationSource, source) && observation is { } pending) await pending;
            if (!ReferenceEquals(bridge, source) || generation != binding) return;
            var activation = action == CpNetServerAction.Start ? selected.CreateActivation() : null;
            var reply = await source.RequestAsync(action, activation);
            if (ReferenceEquals(bridge, source) && generation == binding) Render(reply);
        }
        catch
        {
            if (ReferenceEquals(bridge, source) && generation == binding)
                Render(new(CpNetServerState.Failed, false, Error: "Server action failed; check the selected settings."));
        }
        finally
        {
            busy = false; UpdateControls();
            if (generation != binding && bridge is not null) _ = ObserveAsync();
        }
    }

    private void Render(CpNetServerStatus value)
    {
        status = value;
        serverStatus.Text = "Server: " + (value.State == CpNetServerState.Unknown ? (bridge is null ? "Unavailable" : "Checking") : value.State.ToString()) +
            (value.State == CpNetServerState.Listening && value.Address is not null ? " · " + value.Address : "");
        loginCount.Text = "Logged-in connections: " + (value.LoggedIn?.ToString() ?? "—");
        publicIdentity.Text = value.Identity is { } identityInfo
            ? $"Subject: {identityInfo.Subject}\nSAN: {identityInfo.SubjectAlternativeNames}\nExpires (UTC): {identityInfo.ExpiresUtc:yyyy-MM-dd HH:mm:ss}\nSHA-256: {identityInfo.Sha256}"
            : "";
        notice.Text = value.Error ?? "";
        UpdateControls();
        pollTimer?.Cancel();
        if (bridge is not null && value.State is CpNetServerState.Starting or CpNetServerState.Listening or CpNetServerState.Stopping)
        {
            var source = bridge;
            var timer = new CancellationTokenSource();
            pollTimer = timer;
            _ = PollAsync(source, timer.Token);
        }
    }

    private async Task PollAsync(CpNetServerBridge source, CancellationToken cancellation)
    {
        try { await Task.Delay(PollInterval, cancellation); }
        catch (OperationCanceledException) { return; }
        if (ReferenceEquals(source, bridge) && !busy) await ObserveAsync();
    }

    private void UpdateControls()
    {
        identityStatus.Text = "TLS identity: " + (settings?.IdentityConfigured == true ? "configured (validated on Start)" : "not configured");
        loginStatus.Text = "CP/NET LOGIN: " + (settings?.LoginConfigured == true ? "configured" : "not configured");
        start.IsEnabled = bridge is not null && settings?.CanStart == true && status.CanStart && !busy;
        stop.IsEnabled = bridge is not null && !busy && status.State is CpNetServerState.Starting or CpNetServerState.Listening;
        preset.IsEnabled = settings?.Format is CpNetResourceFormat.Dsk or CpNetResourceFormat.RawFloppy;
    }
}
