using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Hc2000.App.Network;

/// <summary>The selected running machine's network gateway (hc2000-ssh-terminal), shown when it has the X6 card:
/// the owner's switch and the gateway's log, newest last. The switch acts on the live gateway at once and is
/// remembered with the machine; nothing else turns it on.</summary>
public sealed class OutboundGatewayPanel : StackPanel
{
    public const int ShownLines = 12;
    private readonly CheckBox enabled;
    private readonly Button closeAll, setDefault, forgetDefault, stopShare;
    private readonly TextBlock shareLine;
    private DriveShareState? share;
    private readonly TextBox defaultTarget;
    private readonly TextBlock defaultLine;
    private X6DefaultConnection? defaults;
    private readonly TextBlock log, files;
    private OutboundGateway? gateway;
    private bool syncing;

    public OutboundGatewayPanel()
    {
        Spacing = 6;
        Children.Add(new TextBlock { Text = "Network · X6 gateway", FontSize = 18 });
        Children.Add(new TextBlock
        {
            Text = "The X6 card reaches out only through this gateway: outbound connections to the host and port the guest names, nothing listening. Off, it refuses every connection; switching it off closes the open ones. In CP/M run NETX6 to install the driver, then SSH (SSH ? lists its keys).",
            TextWrapping = TextWrapping.Wrap,
            Classes = { "caption" }
        });
        files = new TextBlock { Name = "X6SshFiles", TextWrapping = TextWrapping.Wrap, Classes = { "caption" } };
        Children.Add(files);
        enabled = new CheckBox { Name = "X6GatewayEnabled", Content = "Gateway on for this machine" };
        Children.Add(enabled);
        closeAll = new Button { Name = "X6GatewayCloseAll", Content = "Close connections" };
        closeAll.Click += (_, _) => gateway?.CloseAll();
        Children.Add(closeAll);
        // The live drive share of this machine's SSH session (hc2000-remote-drive-mount), with its stop action.
        shareLine = new TextBlock { Name = "X6ShareLine", TextWrapping = TextWrapping.Wrap };
        Children.Add(shareLine);
        stopShare = new Button { Name = "X6ShareStop", Content = "Stop sharing" };
        stopShare.Click += async (_, _) => { if (share?.Stop is { } stop) await stop(); };
        Children.Add(stopShare);
        ShowShare(null);
        // The default connection CP/M's SSH makes with no arguments, so a session needs no typing.
        defaultLine = new TextBlock { Name = "X6SshDefaultLine", TextWrapping = TextWrapping.Wrap };
        Children.Add(defaultLine);
        defaultTarget = new TextBox { Name = "X6SshDefaultTarget", PlaceholderText = "user@host[:port][,key:name]" };
        Children.Add(defaultTarget);
        var defaultButtons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        defaultButtons.Children.Add(setDefault = new Button { Name = "X6SshDefaultSet", Content = "Set default" });
        defaultButtons.Children.Add(forgetDefault = new Button { Name = "X6SshDefaultForget", Content = "Forget default" });
        Children.Add(defaultButtons);
        setDefault.Click += (_, _) =>
        {
            if (defaults is null) return;
            if (X6DefaultTarget.Parse(defaultTarget.Text, out var why) is { } target) { defaults.Set(target); defaultTarget.Text = ""; }
            else defaultLine.Text = "Not a connection: " + why + ".";
        };
        forgetDefault.Click += (_, _) => defaults?.Set(null);
        log = new TextBlock { Name = "X6GatewayLog", TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("monospace"), Classes = { "caption" } };
        Children.Add(log);
        enabled.IsCheckedChanged += (_, _) =>
        {
            if (syncing || gateway is null) return;
            var value = enabled.IsChecked == true;
            gateway.SetEnabled(value);
            Switched?.Invoke(this, value);
        };
        Render();
    }

    /// <summary>The owner flipped the switch (not raised when binding or rebinding).</summary>
    public event EventHandler<bool>? Switched;

    public CheckBox Switch => enabled;
    public Button CloseAllButton => closeAll;
    public string LogText => log.Text ?? "";

    public void Attach(OutboundGateway value, string? sshDirectory = null, string? logPath = null, X6DefaultConnection? defaultConnection = null)
    {
        if (!ReferenceEquals(defaults, defaultConnection))
        {
            if (defaults is not null) defaults.Changed -= OnDefaultChanged;
            defaults = defaultConnection;
            if (defaults is not null) defaults.Changed += OnDefaultChanged;
        }
        RenderDefault();
        files.Text = (sshDirectory is null ? "" : $"SSH host keys are trusted on first use and kept in {Path.Combine(sshDirectory, "known_hosts")}; a changed key is refused. Login keys (ECDSA or RSA, unencrypted OpenSSH or PEM files) go in {Path.Combine(sshDirectory, "keys")}, named as the CP/M profile names them.")
            + (logPath is null ? "" : $" The log is also kept in {logPath}.");
        if (ReferenceEquals(gateway, value)) return;
        Unbind();
        gateway = value;
        gateway.Logged += OnLogged;
        Render();
    }

    public void Unbind()
    {
        if (gateway is not null) gateway.Logged -= OnLogged;
        gateway = null;
        Render();
    }

    public string DefaultLine => defaultLine.Text ?? "";
    public string ShareLine => shareLine.Text ?? "";
    public Button StopShareButton => stopShare;

    /// <summary>Shows the machine's drive share: none, asked, ready (with the command the remote runs), mounted (the
    /// read-write drives are off the machine), or unavailable with the reason.</summary>
    public void ShowShare(DriveShareState? value)
    {
        share = value;
        var (stage, port, text) = value?.Snapshot() ?? (DriveShareStage.None, (ushort)0, "");
        shareLine.Text = stage switch
        {
            DriveShareStage.None => "Shared drives: none. A CP/M SSH profile names the drives it shares (SSH /S B:RW,A:RO).",
            DriveShareStage.Asked => $"Shared drives {text}: asking the server for the forward.",
            DriveShareStage.Ready => $"Shared drives {text}: ready. On the remote: sshfs -o directport={port} localhost:/ ~/hc2000",
            DriveShareStage.Mounted => $"Shared drives {text}: mounted by the remote on port {port}. Read-write drives are out of the machine until the unmount; then press ^C in CP/M.",
            _ => $"Shared drives: {text}.",
        };
        stopShare.IsVisible = stage is DriveShareStage.Ready or DriveShareStage.Mounted;
        stopShare.IsEnabled = value?.Stop is not null;
    }
    public TextBox DefaultTarget => defaultTarget;
    public Button SetDefaultButton => setDefault;
    public Button ForgetDefaultButton => forgetDefault;

    private void OnDefaultChanged(X6DefaultTarget? value)
    {
        if (Dispatcher.UIThread.CheckAccess()) RenderDefault();
        else Dispatcher.UIThread.Post(RenderDefault);
    }

    private void RenderDefault()
    {
        var current = defaults?.Current;
        defaultLine.Text = current is null
            ? "Default SSH connection: none yet. The first one that opens becomes it, or set it here; then SSH alone connects."
            : $"Default SSH connection: {current.Text}, {(current.KeyName is null ? "password typed in CP/M" : "key " + current.KeyName)}. In CP/M, SSH alone connects to it.";
        forgetDefault.IsEnabled = current is not null;
        setDefault.IsEnabled = defaults is not null;
    }

    private void OnLogged(OutboundLogEntry entry)
    {
        if (Dispatcher.UIThread.CheckAccess()) Render();
        else Dispatcher.UIThread.Post(Render);
    }

    private void Render()
    {
        syncing = true;
        try { enabled.IsChecked = gateway?.Enabled == true; }
        finally { syncing = false; }
        enabled.IsEnabled = gateway is not null;
        closeAll.IsEnabled = gateway?.OpenConnections > 0;
        var lines = gateway?.Log ?? [];
        log.Text = lines.Count == 0 ? "No connections yet."
            : string.Join("\n", lines.Skip(Math.Max(0, lines.Count - ShownLines)).Select(entry =>
                $"{entry.At.ToLocalTime():HH:mm:ss}  {(entry.Port == 0 ? entry.Host : $"{entry.Host}:{entry.Port}")}  {entry.Purpose}  {entry.Result}"));
    }
}
