namespace Hc2000.App.Network;

/// <summary>Where a session's drive share stands (hc2000-remote-drive-mount): asked of the server, ready on the port
/// the server bound, mounted by the remote, or unavailable with the reason. CP/M reads it with the X6 op SHARE, the
/// Network section shows it with a stop action. Thread-safe: the SSH side sets it, the card and the panel read it.</summary>
public enum DriveShareStage : byte { None = 0, Asked = 1, Ready = 2, Mounted = 3, Unavailable = 4 }

public sealed class DriveShareState(string shares)
{
    private readonly object sync = new();
    private DriveShareStage stage = DriveShareStage.Asked;
    private ushort port;
    private string reason = "";

    /// <summary>The shares as the profile named them, e.g. <c>B:RW,A:RO</c>.</summary>
    public string Shares { get; } = shares;

    public event Action<DriveShareState>? Changed;

    /// <summary>The owner's "stop sharing": unmounts cleanly (set by whoever runs the share).</summary>
    public Func<Task>? Stop { get; set; }

    public (DriveShareStage Stage, ushort Port, string Text) Snapshot()
    {
        lock (sync) return (stage, port, stage == DriveShareStage.Unavailable ? reason : Shares);
    }

    public void Set(DriveShareStage value, ushort boundPort = 0, string why = "")
    {
        lock (sync)
        {
            stage = value;
            if (boundPort != 0) port = boundPort;
            reason = why;
        }
        Changed?.Invoke(this);
    }
}
