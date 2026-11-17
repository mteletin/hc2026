using System.Globalization;

namespace Hc2000.App.Network;

/// <summary>The default SSH connection CP/M's <c>SSH</c> uses with no arguments, so a session starts without typing
/// an address: a
/// host, port and user, and the host-side key to log in with, or none for a typed password. It never holds a
/// password. Written as <c>user@host[:port][,key:name]</c>.</summary>
public sealed record X6DefaultTarget(string Host, ushort Port, string User, string? KeyName)
{
    public const ushort SshPort = 22;

    /// <summary>What the guest shows: <c>user@host:port</c>.</summary>
    public string Text => $"{User}@{Host}:{Port.ToString(CultureInfo.InvariantCulture)}";

    public override string ToString() => KeyName is null ? Text : Text + ",key:" + KeyName;

    /// <summary>Reads <c>user@host[:port][,key:name]</c>; null when it is not one (the reason in <paramref name="error"/>).</summary>
    public static X6DefaultTarget? Parse(string? text, out string? error)
    {
        error = null;
        var value = (text ?? "").Trim();
        string? key = null;
        var comma = value.IndexOf(',', StringComparison.Ordinal);
        if (comma >= 0)
        {
            var rest = value[(comma + 1)..].Trim();
            value = value[..comma].Trim();
            if (!rest.StartsWith("key:", StringComparison.Ordinal) || rest.Length == 4) { error = "after the comma: key:<name>"; return null; }
            key = rest[4..];
            if (key.Length > 64 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) { error = "a key name is letters, digits, . _ -"; return null; }
        }
        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at == value.Length - 1) { error = "write user@host[:port]"; return null; }
        var user = value[..at];
        var host = value[(at + 1)..];
        ushort port = SshPort;
        var colon = host.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!ushort.TryParse(host[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port == 0)
            { error = "the port is 1-65535"; return null; }
            host = host[..colon];
        }
        if (host.Length is 0 or > X6SessionParameters.MaximumHost || user.Length > 64
            || !(host + user).All(c => c is > ' ' and <= '~' && c != '@'))
        { error = "the host is at most 64 printable characters"; return null; }
        return new(host, port, user, key);
    }
}

/// <summary>The App's one default connection, shared by every machine's X6 card: set by the owner in the Network
/// section or by <c>--ssh-default</c>, and otherwise remembered from the first SSH session that opens. Each change is
/// handed to <paramref name="persist"/> (the App saves it beside its other settings), except a launch-option override.
/// Thread-safe: the card asks on the emulation thread, the panel sets it on the UI thread.</summary>
public sealed class X6DefaultConnection(X6DefaultTarget? initial = null, Action<X6DefaultTarget?>? persist = null)
{
    private readonly object sync = new();
    private X6DefaultTarget? current = initial;

    public X6DefaultTarget? Current { get { lock (sync) return current; } }

    /// <summary>Raised after the default changed (on the thread that changed it).</summary>
    public event Action<X6DefaultTarget?>? Changed;

    public void Set(X6DefaultTarget? value)
    {
        lock (sync)
        {
            if (current == value) return;
            current = value;
        }
        persist?.Invoke(value);
        Changed?.Invoke(value);
    }

    /// <summary>A session reached the far end: if no default is set yet, it becomes the default (never its password).</summary>
    public void RememberIfUnset(X6SessionParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Kind != 1 || parameters.Host.Length == 0 || parameters.User.Length == 0) return;
        X6DefaultTarget remembered;
        lock (sync)
        {
            if (current is not null) return;
            current = remembered = new(parameters.Host, parameters.Port, parameters.User,
                parameters.KeyName.Length == 0 ? null : parameters.KeyName);
        }
        persist?.Invoke(remembered);
        Changed?.Invoke(remembered);
    }
}
