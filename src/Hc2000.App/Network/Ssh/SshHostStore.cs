using System.Text.RegularExpressions;

namespace Hc2000.App.Network.Ssh;

/// <summary>The App's SSH trust and key files (hc2000-ssh-terminal), kept on the host and never in CP/M. Host keys
/// are trusted on first use and remembered in <c>known_hosts</c> (OpenSSH format, <c>[host]:port type base64</c>); a
/// key that differs from the remembered one is refused. Login keys are unencrypted openssh-key-v1 files in
/// <c>keys/</c>, found by the name a CP/M profile gives (PARAM field 6). Every decision goes to the gateway's log.</summary>
public sealed partial class SshHostStore
{
    private readonly object sync = new();
    private readonly OutboundGateway? log;

    public SshHostStore(string directory, OutboundGateway? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
        this.log = log;
    }

    public string Directory { get; }
    public string KnownHostsPath => Path.Combine(Directory, "known_hosts");
    public string KeysDirectory => Path.Combine(Directory, "keys");

    /// <summary>The connector's host-key policy: a new host is remembered and trusted, a remembered key must match.</summary>
    public ValueTask<bool> TrustAsync(SshHostKey key, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(key);
        var name = HostName(key.Host, key.Port);
        var line = $"{name} {key.KeyType} {Convert.ToBase64String(key.Blob)}";
        bool trusted;
        string result;
        lock (sync)
        {
            var known = File.Exists(KnownHostsPath) ? File.ReadAllLines(KnownHostsPath) : [];
            var match = known.Select(entry => entry.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(parts => parts.Length >= 3 && parts[0] == name && parts[1] == key.KeyType);
            if (match is null)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllLines(KnownHostsPath, [line]);
                (trusted, result) = (true, "host key trusted on first use " + key.Fingerprint);
            }
            else if (match[2] == Convert.ToBase64String(key.Blob)) (trusted, result) = (true, "host key known " + key.Fingerprint);
            else (trusted, result) = (false, "refused: host key changed, now " + key.Fingerprint);
        }
        log?.Note(key.Host, key.Port, "ssh", result);
        return ValueTask.FromResult(trusted);
    }

    /// <summary>The login key a profile names, or null: no such file, a name that is not a plain file name, or a
    /// file the client cannot read (the reason goes to the log).</summary>
    public SshPrivateKey? FindKey(string name)
    {
        if (!KeyName().IsMatch(name)) return null;
        var path = Path.Combine(KeysDirectory, name);
        if (!File.Exists(path)) return null;
        try { return SshPrivateKey.Parse(File.ReadAllText(path)); }
        catch (SshException error)
        {
            log?.Note("-", 0, "ssh", $"key {name} not usable: {error.Message}");
            return null;
        }
    }

    // OpenSSH writes a host on port 22 bare and any other port as [host]:port.
    private static string HostName(string host, int port) => port == 22 ? host : $"[{host}]:{port}";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex KeyName();
}
