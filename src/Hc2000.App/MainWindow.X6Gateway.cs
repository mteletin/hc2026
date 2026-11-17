using Avalonia.Controls;
using Hc2000.App.Network;
using Hc2000.App.Workspace;

namespace Hc2000.App;

// hc2000-ssh-terminal: the selected running machine's X6 network gateway. The switch acts on that machine's live
// gateway and is remembered with it (MachineConfiguration.NetworkGateway, saved by machine id), so its next start
// opens the gateway the way the owner left it.
public partial class MainWindow
{
    private MachineTabContext? x6GatewayTab;
    private MachineSession? x6ShareSession;
    private DriveShareStage x6ShareShown;

    private void OnDriveShareChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        var share = x6ShareSession?.DriveShare;
        X6GatewayPanel.ShowShare(share);
        var (stage, port, text) = share?.Snapshot() ?? (DriveShareStage.None, (ushort)0, "");
        if (stage == x6ShareShown) return;
        x6ShareShown = stage;
        // The status line names the share as it changes (decision hc2000-drive-sharing-sftp-v1 §6).
        this.FindControl<TextBlock>("StatusLine")!.Text = stage switch
        {
            DriveShareStage.Ready => $"Sharing {text} with the remote: sshfs -o directport={port} localhost:/ ~/hc2000",
            DriveShareStage.Mounted => $"Shared drives {text} mounted by the remote; read-write drives are out of the machine.",
            DriveShareStage.Unavailable => text,
            _ => this.FindControl<TextBlock>("StatusLine")!.Text,
        };
    });
    private Network.X6DefaultConnection? x6Default;

    /// <summary>The App's default SSH connection (CP/M's SSH with no arguments): from --ssh-default for this run, else
    /// settings.json's sshDefault, else the first SSH session that opens. Saved in settings.json unless the launch
    /// option set it.</summary>
    internal Network.X6DefaultConnection X6Default => x6Default ??= CreateX6Default();

    private Network.X6DefaultConnection CreateX6Default()
    {
        if (launchOptions?.SshDefault is { } launch)
            return new(Network.X6DefaultTarget.Parse(launch, out _));
        var loaded = configurationStore.Load();
        var stored = loaded.Warning is null ? Network.X6DefaultTarget.Parse(loaded.Settings.SshDefault, out _) : null;
        var connection = new Network.X6DefaultConnection(stored, StoreX6Default);
        return connection;
    }

    // Saved off the emulation thread: the card remembers a default while answering STATUS.
    private void StoreX6Default(Network.X6DefaultTarget? value) => _ = Task.Run(() =>
    {
        try
        {
            var loaded = configurationStore.Load();
            if (loaded.Warning is not null) return;
            configurationStore.Save(loaded.Settings with { SshDefault = value?.ToString() });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    });
    private Network.OutboundGateway? x6StatusGateway;
    private bool syncingX6Status;

    // Found by name like the window's other controls (the generated field is not set by this window's loader).
    private Avalonia.Controls.Primitives.ToggleButton X6GatewayStatusButton =>
        this.FindControl<Avalonia.Controls.Primitives.ToggleButton>("X6GatewayStatus")!;

    private Network.OutboundGatewayPanel X6GatewayPanel => this.FindControl<Network.OutboundGatewayPanel>("X6GatewayControls")!;

    // The SSH trust and key files sit beside settings.json, in ssh/ (known_hosts, keys/<name>); the gateway's log
    // carries every host-key decision.
    internal string SshDirectory => Path.Combine(Path.GetDirectoryName(configurationStore.FilePath)!, "ssh");

    private Func<Network.X6SessionParameters, Network.IX6Channel?, CancellationToken, Task<Network.IX6Channel>> X6SshConnector(MachineSession session)
    {
        var store = new Network.Ssh.SshHostStore(SshDirectory, session.OutboundGateway);
        return Network.Ssh.SshConnector.Create(store.TrustAsync, store.FindKey, forward: parameters =>
        {
            // hc2000-remote-drive-mount: a session that names shared drives asks for the forward; the share runs here.
            if (parameters.ShareState is not { } state) return null;
            var shares = Network.X6DriveShares.Parse(parameters.Shares, out _);
            session.DriveShare = state;
            state.Changed += _ => session.RaiseDriveShareChanged();
            session.RaiseDriveShareChanged();
            var run = new Network.DriveShareRun(state, shares, ShareMedia(session),
                line => session.OutboundGateway.Note("-", 0, "share", line));
            return run.Options;
        });
    }

    /// <summary>The App's own media paths for one machine, as the drive share uses them, each on the UI thread like the
    /// media panel's: inspect, export, the writeback eject, a plain eject of bytes already exported, an insert, and the
    /// way back through a bound file (saved through MediaFiles, inserted, bound to writeback again).</summary>
    private Network.DriveShareMedia ShareMedia(MachineSession session)
    {
        Media.MediaBridge Bridge() => session.MediaBridge ?? throw new InvalidOperationException("The machine's media are not running.");
        static Media.MediaReply Checked(Media.MediaReply reply) =>
            reply.Error is null ? reply : throw new InvalidOperationException(reply.Error);
        static Task<T> OnUi<T>(Func<Task<T>> work) => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(work);
        // The share's own key to the slot it holds (contract v3 §1d): its eject and insert carry it, everything else
        // queued for that drive is refused while the share is open, with the reason in this machine's gateway log.
        var key = Random.Shared.NextInt64(1, long.MaxValue);
        void Refused(string line) => session.OutboundGateway.Note("-", 0, "share", line);
        return new(new(
            Inspect: slot => OnUi(() => Bridge().RequestAsync(new Media.MediaCommand(Media.MediaAction.Inspect))),
            Export: slot => OnUi(async () => Checked(await Bridge().RequestAsync(new(Media.MediaAction.ExportDisk, slot))).Export),
            WritebackEject: slot => OnUi(() => session.WritebackHost?.TryEjectAsync(slot) ?? Task.FromResult(false)),
            Eject: slot => OnUi(async () => Checked(await Bridge().RequestAsync(new(Media.MediaAction.EjectDisk, slot, DiscardChanges: true, HoldKey: key)))),
            InsertBytes: (slot, bytes, name) => OnUi(async () =>
                Checked(await Bridge().RequestAsync(new(Media.MediaAction.InsertDisk, slot, [.. bytes], name, DiscardChanges: true, HoldKey: key)))),
            SaveAndInsertFromFile: (slot, path, bytes) => OnUi(async () =>
            {
                await Media.MediaFiles.SaveAsync(path, [.. bytes]);
                var reply = Checked(await Bridge().RequestAsync(new(Media.MediaAction.InsertDisk, slot, [.. bytes], Path.GetFileName(path), DiscardChanges: true, HoldKey: key)));
                if (session.WritebackHost is { } host) await host.NoteMounted(slot, path, [.. bytes], reply);
                return reply;
            }),
            BoundPath: slot => session.WritebackHost?.BoundPath(slot),
            IsProtectedOriginal: slot => session.WritebackHost?.BoundPath(slot) is { } path && session.PathIsProtectedOriginal(path),
            Hold: (slot, on) => OnUi(async () => Checked(await Bridge().RequestAsync(new(Media.MediaAction.HoldDisk, slot,
                Name: "the drive share (read-write, mounted on the remote)", Value: on, HoldKey: key, HoldRefused: Refused))))));
    }

    /// <summary>The gateway log is kept beside the SSH files, one append-only file per machine id
    /// (<c>network/&lt;id&gt;.log</c>): time, host:port, purpose and result, never a secret.</summary>
    internal string GatewayLogPath(string machineId) =>
        Path.Combine(Path.GetDirectoryName(configurationStore.FilePath)!, "network",
            new string(machineId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray()) + ".log");

    private void KeepX6GatewayLog(MachineSession session)
    {
        if (session.GatewayLogKept || session.StartingConfiguration?.Id is not { Length: > 0 } id) return;
        session.GatewayLogKept = true;
        var path = GatewayLogPath(id);
        session.OutboundGateway.Logged += entry =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, $"{entry.At.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ} {entry.Host}:{entry.Port} {entry.Purpose} {entry.Result}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
    }

    private void WireX6Gateway()
    {
        X6GatewayPanel.Switched += (_, on) => X6GatewaySwitched(on);
        X6GatewayStatusButton.IsCheckedChanged += (_, _) =>
        {
            if (syncingX6Status || x6StatusGateway is null) return;
            var on = X6GatewayStatusButton.IsChecked == true;
            x6StatusGateway.SetEnabled(on);
            X6GatewaySwitched(on);
        };
    }

    /// <summary>Ctrl+Shift+O: the same switch from the keyboard, for the selected machine with the card.</summary>
    internal bool ToggleX6Gateway()
    {
        if (x6StatusGateway is null) return false;
        var on = !x6StatusGateway.Enabled;
        x6StatusGateway.SetEnabled(on);
        X6GatewaySwitched(on);
        return true;
    }

    private void X6GatewaySwitched(bool on)
    {
        this.FindControl<TextBlock>("StatusLine")!.Text = on
            ? "X6 network gateway on: this machine may connect out; every connection is in the gateway log."
            : "X6 network gateway off: open connections closed, new ones refused.";
        StoreX6Gateway(x6GatewayTab, on);
        RenderX6Status();
    }

    private void OnX6StatusLogged(Network.OutboundLogEntry entry)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) RenderX6Status();
        else Avalonia.Threading.Dispatcher.UIThread.Post(RenderX6Status);
    }

    private void RenderX6Status()
    {
        var status = X6GatewayStatusButton;
        status.IsVisible = x6StatusGateway is not null;
        if (x6StatusGateway is null) return;
        var on = x6StatusGateway.Enabled;
        syncingX6Status = true;
        try { status.IsChecked = on; }
        finally { syncingX6Status = false; }
        var open = x6StatusGateway.OpenConnections;
        status.Content = on ? (open > 0 ? $"Network gateway ON · {open} open" : "Network gateway ON") : "Network gateway OFF · click to switch on";
    }

    /// <summary>Shows the gateway of the running machine with the card: a tab's, or the window's own session when the
    /// App was started with --start (it has no tab; its switch is not saved, like its other settings).</summary>
    private void BindX6Gateway(MachineSession? session, MachineTabContext? tab)
    {
        var panel = X6GatewayPanel;
        x6GatewayTab = tab;
        var machineId = tab?.Configuration.Id ?? session?.StartingConfiguration?.Id;
        if (session is null) panel.Unbind();
        else panel.Attach(session.OutboundGateway, SshDirectory, machineId is { Length: > 0 } ? GatewayLogPath(machineId) : null, X6Default);
        if (!ReferenceEquals(x6ShareSession, session))
        {
            if (x6ShareSession is not null) x6ShareSession.DriveShareChanged -= OnDriveShareChanged;
            x6ShareSession = session;
            if (session is not null) session.DriveShareChanged += OnDriveShareChanged;
        }
        panel.ShowShare(session?.DriveShare);
        var gateway = session?.OutboundGateway;
        if (!ReferenceEquals(x6StatusGateway, gateway))
        {
            if (x6StatusGateway is not null) x6StatusGateway.Logged -= OnX6StatusLogged;
            x6StatusGateway = gateway;
            if (gateway is not null) gateway.Logged += OnX6StatusLogged;
        }
        RenderX6Status();
        panel.IsVisible = session is not null;
    }

    private void StoreX6Gateway(MachineTabContext? tab, bool on)
    {
        if (tab is null || tab.Configuration.NetworkGateway == on) return;
        workspace.TryUpdateDraft(new MachineDraftTarget(tab.TabId, tab.DraftRevision), tab.Configuration with { NetworkGateway = on });
        var loaded = configurationStore.Load();
        if (loaded.Warning is not null)
        {
            this.FindControl<TextBlock>("StatusLine")!.Text = "Network gateway switched, not saved: " + loaded.Warning;
            return;
        }
        if (loaded.Settings.Workspace is not { } stored) return;
        var index = -1;
        for (var i = 0; i < stored.Machines.Length && index < 0; i++)
            if (stored.Machines[i].Id == tab.Configuration.Id) index = i;
        if (index < 0 || stored.Machines[index].NetworkGateway == on) return;
        try
        {
            configurationStore.Save(loaded.Settings with
            {
                Workspace = stored with { Machines = stored.Machines.SetItem(index, stored.Machines[index] with { NetworkGateway = on }) },
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.FindControl<TextBlock>("StatusLine")!.Text = "Network gateway switched, not saved: " + ex.Message;
        }
    }
}
