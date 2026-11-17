using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Hc2000.App.Network;

/// <summary>Operator-selected CP/NET host session for the selected running machine (decision #245).
/// A compact Network section: Server, Port, Connect and Disconnect. The panel owns only the
/// fields and the two explicit buttons; every request crosses the boundary as one command in the
/// session's shared input FIFO through the session-owned <see cref="CpNetBridge"/>. Nothing
/// connects automatically: attaching, rebinding on a tab reopen, card selection, a restart and a
/// snapshot load only render state — only the Connect button enqueues a Connect. The verified
/// server identity is shown only when the owner reports one (a real validated TLS connection).</summary>
public sealed class CpNetPanel : StackPanel
{
    private CpNetBridge? bridge;
    private bool busy;
    private long binding;
    private CancellationTokenSource? pollTimer;
    private Task<CpNetSessionStatus>? observation;
    private CpNetBridge? observationSource;
    private readonly TextBox server, port;
    private readonly TextBlock connection, tls, notice, recovery;
    private readonly Button connect, disconnect;
    private readonly CpNetServerPanel serverPanel;

    public CpNetPanel()
    {
        Spacing = 6;
        Children.Add(new TextBlock { Text = "Network · CP/NET host", FontSize = 18 });
        Children.Add(new TextBlock { Text = "The host gateway is off until you Connect here. Connect never happens on its own: not on card selection, restart, snapshot load or tab reopen.", TextWrapping = TextWrapping.Wrap, Classes = { "caption" } });
        Children.Add(new TextBlock { Text = "Enable the CP/NET hardware profile and boot disks/HC2000-CPNET.DSK as A:. In CP/M run NETINST, Connect here, then run NETLOGIN 1 and enter the server password. NETMAP B 1 A maps local B: to server 1's A:; exported files are read-only. If A: is mapped, the server must contain CCP.SPR for warm boots.", TextWrapping = TextWrapping.Wrap, Classes = { "caption" } });
        Children.Add(new TextBlock { Text = "Server" });
        server = new TextBox { Name = "CpNetServer", Text = "" };
        Children.Add(server);
        Children.Add(new TextBlock { Text = "Port" });
        port = new TextBox { Name = "CpNetPort", Text = "" };
        Children.Add(port);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(connect = new Button { Content = "Connect", Focusable = true, Classes = { "primaryAction" } });
        buttons.Children.Add(disconnect = new Button { Content = "Disconnect", Focusable = true });
        Children.Add(buttons);
        connection = new() { Text = "Connection: Disconnected" };
        tls = new() { Text = "TLS: Not connected" };
        notice = new() { Text = "Not connected to a machine runtime.", TextWrapping = TextWrapping.Wrap, Classes = { "notice" } };
        Children.Add(connection);
        Children.Add(tls);
        Children.Add(notice);
        recovery = new TextBlock
        {
            Text = "If a CP/NET transport fault stops the guest, the last request may have completed; check its outcome before retrying. Disconnect and cold-restart the machine: unsaved CP/M work is lost. Then load CP/NET, reconnect, LOGIN, and recreate and verify the network mappings before continuing. The stopped guest cannot return to its local prompt.",
            TextWrapping = TextWrapping.Wrap,
            Classes = { "caption" }
        };
        Children.Add(recovery);
        Children.Add(serverPanel = new CpNetServerPanel());
        connect.Click += async (_, _) => await ConnectAsync();
        disconnect.Click += async (_, _) => await RunAsync(CpNetAction.Disconnect);
        UpdateControls();
    }

    public string ConnectionLine => connection.Text ?? "";
    public string TlsLine => tls.Text ?? "";
    public string Notice => notice.Text ?? "";
    public string RecoveryGuidance => recovery.Text ?? "";
    public TextBox ServerField => server;
    public TextBox PortField => port;
    public Button ConnectButton => connect;
    public Button DisconnectButton => disconnect;
    internal CpNetServerPanel ServerPanel => serverPanel;
    // The transitional-state re-observe cadence; tests shorten it.
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Bind the shared view to this session's bridge. Never connects: it only renders the
    /// current state with one best-effort Observe in the shared FIFO.</summary>
    internal void Attach(CpNetBridge value, CpNetServerBridge? serverBridge = null, CpNetServerSettings? serverSettings = null)
    {
        bridge = value;
        binding++;
        notice.Text = "Ready. Connect is explicit only.";
        if (serverBridge is not null && serverSettings is not null) serverPanel.Attach(serverBridge, serverSettings);
        UpdateControls();
        if (!busy) _ = ObserveBestEffortAsync();
    }

    /// <summary>A tab switch releases only the shared view, not the session's live session.</summary>
    public void Unbind()
    {
        binding++;
        bridge = null;
        serverPanel.Unbind();
        pollTimer?.Cancel();
        pollTimer = null;
        Render(new(CpNetSessionState.Off, true));
        notice.Text = "Not connected to a machine runtime.";
        UpdateControls();
    }

    /// <summary>The session's worker retired or the window is closing: the bridge refuses from now on.</summary>
    public void Disconnect()
    {
        bridge?.Disconnect();
        Unbind();
    }

    private async Task ConnectAsync()
    {
        var host = (server.Text ?? "").Trim();
        var portText = (port.Text ?? "").Trim();
        if (host.Length == 0) { notice.Text = "Enter the server host name or address."; return; }
        if (!int.TryParse(portText, out var portNumber) || portNumber is < 1 or > 65535) { notice.Text = "Port must be a number between 1 and 65535."; return; }
        await RunAsync(CpNetAction.Connect, host, portNumber);
    }

    private async Task ObserveBestEffortAsync()
    {
        var source = bridge;
        if (source is null || busy) return;
        var generation = binding;
        Task<CpNetSessionStatus>? request;
        try
        {
            request = source.RequestAsync(CpNetAction.Observe);
            observation = request;
            observationSource = source;
        }
        catch { return; } // Another request is already in flight; it will render its own reply.
        try
        {
            var reply = await request;
            if (ReferenceEquals(bridge, source) && binding == generation && !busy) Render(reply);
        }
        catch { /* a runtime change or retirement while observing is reported by its own command. */ }
        finally
        {
            if (ReferenceEquals(observation, request)) { observation = null; observationSource = null; }
        }
    }

    internal async Task RunAsync(CpNetAction action, string host = "", int port = 0)
    {
        var source = bridge;
        if (busy || source is null) return;
        var generation = binding;
        busy = true; UpdateControls();
        try
        {
            notice.Text = action == CpNetAction.Connect ? "Connecting…" : "Disconnecting…";
            if (ReferenceEquals(observationSource, source) && observation is { } pending)
                await pending;
            if (!ReferenceEquals(bridge, source) || binding != generation) return;
            var reply = await source.RequestAsync(action, host, port);
            if (ReferenceEquals(bridge, source) && binding == generation) Render(reply);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(bridge, source) && binding == generation)
            {
                notice.Text = "Network action failed: " + ex.Message;
                Render(new(CpNetSessionState.Failed, false, ex.Message));
            }
        }
        finally
        {
            busy = false;
            UpdateControls();
            if (binding != generation && bridge is not null) _ = ObserveBestEffortAsync();
        }
    }

    private void Render(CpNetSessionStatus status)
    {
        connection.Text = "Connection: " + (status.State switch
        {
            CpNetSessionState.Off => "Disconnected",
            CpNetSessionState.Armed => "Armed — waiting for guest",
            CpNetSessionState.Connecting => "Connecting",
            CpNetSessionState.Connected => "Connected",
            CpNetSessionState.Closing => "Closing",
            CpNetSessionState.Failed => "Failed",
            _ => "Disconnected"
        });
        // The identity comes from the owner (a real validated TLS connection); it is never claimed
        // by the panel itself and never shown before a connected, verified reply.
        tls.Text = status.State switch
        {
            CpNetSessionState.Connected when status.VerifiedHost is not null => "TLS: verified as " + status.VerifiedHost,
            CpNetSessionState.Connected => "TLS: Connected",
            CpNetSessionState.Connecting => "TLS: Connecting",
            CpNetSessionState.Closing => "TLS: Closing",
            _ => "TLS: Not connected"
        };
        if (status.State == CpNetSessionState.Failed && status.Error is not null) notice.Text = "Not connected: " + status.Error;
        else if (status.State == CpNetSessionState.Armed) notice.Text = "Armed. The first guest request will connect to the selected server.";
        else if (status.State == CpNetSessionState.Connected) notice.Text = "Connected.";
        else if (status.State == CpNetSessionState.Connecting) notice.Text = "Connecting…";
        else if (status.State == CpNetSessionState.Off && notice.Text == "Disconnecting…") notice.Text = "Disconnected.";
        // The owner reports state only when asked: while a connect or close is still moving,
        // re-observe on a short cadence through the same shared FIFO until the state settles.
        if (bridge is not null && status.State is CpNetSessionState.Armed or CpNetSessionState.Connecting or CpNetSessionState.Closing)
            SchedulePoll(bridge);
        else
            pollTimer?.Cancel();
    }

    private void SchedulePoll(CpNetBridge source)
    {
        pollTimer?.Cancel();
        var timer = new CancellationTokenSource();
        pollTimer = timer;
        _ = ObserveAfterDelayAsync(source, timer.Token);
    }

    private async Task ObserveAfterDelayAsync(CpNetBridge source, CancellationToken token)
    {
        try { await Task.Delay(PollInterval, token); }
        catch (OperationCanceledException) { return; }
        if (!ReferenceEquals(bridge, source) || busy) return;
        await ObserveBestEffortAsync();
    }

    private void UpdateControls()
    {
        connect.IsEnabled = bridge is not null && !busy;
        disconnect.IsEnabled = bridge is not null && !busy;
    }
}
