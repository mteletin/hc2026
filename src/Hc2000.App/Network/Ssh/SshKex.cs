using System.Security.Cryptography;
using System.Text;

namespace Hc2000.App.Network.Ssh;

/// <summary>The algorithms offered, and the steps of an ecdh-sha2-* key exchange that both ends share: the exchange
/// hash (RFC 5656 §4) and the keys derived from it (RFC 4253 §7.2).</summary>
internal static class SshKex
{
    /// <summary>OpenSSH's strict key exchange markers (the Terrapin fix): when both ends send theirs, the first KEXINIT
    /// must be the first packet, nothing else may come between the exchange's packets, and every NEWKEYS restarts the
    /// sequence numbers.</summary>
    public const string StrictClient = "kex-strict-c-v00@openssh.com", StrictServer = "kex-strict-s-v00@openssh.com";

    public static readonly string[] KeyExchanges = ["ecdh-sha2-nistp256", "ecdh-sha2-nistp384", "ecdh-sha2-nistp521"];
    public static readonly string[] Ciphers = ["aes128-gcm@openssh.com", "aes256-gcm@openssh.com", "aes128-ctr", "aes256-ctr"];
    public static readonly string[] Macs = ["hmac-sha2-256", "hmac-sha2-512"];

    public static bool IsAead(string cipher) => cipher.EndsWith("-gcm@openssh.com", StringComparison.Ordinal);

    /// <summary>H = HASH(V_C, V_S, I_C, I_S, K_S, Q_C, Q_S, K), with the version lines as sent and K as an mpint.</summary>
    public static byte[] ExchangeHash(HashAlgorithmName hash, string clientVersion, string serverVersion, byte[] clientInit,
        byte[] serverInit, byte[] hostKey, byte[] clientPoint, byte[] serverPoint, byte[] secret)
    {
        using var hasher = IncrementalHash.CreateHash(hash);
        hasher.AppendData(new SshWriter().String(Encoding.Latin1.GetBytes(clientVersion)).String(Encoding.Latin1.GetBytes(serverVersion))
            .String(clientInit).String(serverInit).String(hostKey).String(clientPoint).String(serverPoint).Mpint(secret).ToArray());
        return hasher.GetHashAndReset();
    }

    /// <summary>One direction's cipher with its keys: IV, key and MAC key are HASH(K || H || letter || session id),
    /// extended by HASH(K || H || what came so far); 'A' 'C' 'E' client to server, 'B' 'D' 'F' back.</summary>
    public static SshPacketCipher CreateCipher(SshAlgorithms algorithms, HashAlgorithmName hash, byte[] secret,
        byte[] exchangeHash, byte[] sessionId, bool clientToServer)
    {
        var k = new SshWriter().Mpint(secret).ToArray();
        byte[] Derive(char letter, int length)
        {
            using var hasher = IncrementalHash.CreateHash(hash);
            hasher.AppendData(k);
            hasher.AppendData(exchangeHash);
            hasher.AppendData([(byte)letter]);
            hasher.AppendData(sessionId);
            var key = hasher.GetHashAndReset();
            while (key.Length < length)
            {
                hasher.AppendData(k);
                hasher.AppendData(exchangeHash);
                hasher.AppendData(key);
                key = [.. key, .. hasher.GetHashAndReset()];
            }
            return key[..length];
        }
        var (cipher, mac) = clientToServer
            ? (algorithms.CipherClientToServer, algorithms.MacClientToServer)
            : (algorithms.CipherServerToClient, algorithms.MacServerToClient);
        var (iv, key, integrity) = clientToServer ? ('A', 'C', 'E') : ('B', 'D', 'F');
        var keyLength = cipher.StartsWith("aes256", StringComparison.Ordinal) ? 32 : 16;
        if (IsAead(cipher)) return new SshGcmCipher(Derive(key, keyLength), Derive(iv, 12));
        return new SshCtrCipher(Derive(key, keyLength), Derive(iv, 16), mac, Derive(integrity, mac == "hmac-sha2-512" ? 64 : 32));
    }
}

/// <summary>A KEXINIT message (RFC 4253 §7.1): its payload, as sent and as hashed, and its name-lists.</summary>
internal sealed class SshKexInit
{
    public required byte[] Payload { get; init; }
    public required string[] KeyExchanges { get; init; }
    public required string[] HostKeys { get; init; }
    public required string[] CiphersClientToServer { get; init; }
    public required string[] CiphersServerToClient { get; init; }
    public required string[] MacsClientToServer { get; init; }
    public required string[] MacsServerToClient { get; init; }
    public required string[] CompressionClientToServer { get; init; }
    public required string[] CompressionServerToClient { get; init; }

    /// <summary>Our KEXINIT: a fresh cookie, the same lists both ways, no compression, no guessed packet.</summary>
    public static SshKexInit Build(IEnumerable<string> keyExchanges, IEnumerable<string> hostKeys, IEnumerable<string> ciphers,
        IEnumerable<string> macs)
    {
        string[] cipherList = [.. ciphers], macList = [.. macs];
        return Parse(new SshWriter(SshMessage.KexInit).Raw(RandomNumberGenerator.GetBytes(16)).NameList(keyExchanges)
            .NameList(hostKeys).NameList(cipherList).NameList(cipherList).NameList(macList).NameList(macList)
            .NameList(["none"]).NameList(["none"]).NameList([]).NameList([]).Boolean(false).UInt32(0).ToArray());
    }

    public static SshKexInit Parse(byte[] payload)
    {
        var reader = new SshReader(payload, 17);
        return new SshKexInit
        {
            Payload = payload,
            KeyExchanges = reader.NameList(),
            HostKeys = reader.NameList(),
            CiphersClientToServer = reader.NameList(),
            CiphersServerToClient = reader.NameList(),
            MacsClientToServer = reader.NameList(),
            MacsServerToClient = reader.NameList(),
            CompressionClientToServer = reader.NameList(),
            CompressionServerToClient = reader.NameList(),
        };
    }
}

/// <summary>What both ends agreed on: for each list, the client's first choice that the server also has. An AES-GCM
/// cipher brings its own integrity, so that direction's MAC is empty.</summary>
internal sealed record SshAlgorithms(string Kex, string HostKey, string CipherClientToServer, string CipherServerToClient,
    string MacClientToServer, string MacServerToClient)
{
    public static SshAlgorithms Negotiate(SshKexInit client, SshKexInit server)
    {
        static string Choose(string[] client, string[] server, string what) =>
            client.FirstOrDefault(name => server.Contains(name)) ?? throw new SshException("no common " + what);
        var kex = Choose(client.KeyExchanges, server.KeyExchanges, "key exchange");
        var hostKey = Choose(client.HostKeys, server.HostKeys, "host key type");
        var outward = Choose(client.CiphersClientToServer, server.CiphersClientToServer, "cipher");
        var inward = Choose(client.CiphersServerToClient, server.CiphersServerToClient, "cipher");
        var macOut = SshKex.IsAead(outward) ? "" : Choose(client.MacsClientToServer, server.MacsClientToServer, "mac");
        var macIn = SshKex.IsAead(inward) ? "" : Choose(client.MacsServerToClient, server.MacsServerToClient, "mac");
        if (Choose(client.CompressionClientToServer, server.CompressionClientToServer, "compression") != "none"
            || Choose(client.CompressionServerToClient, server.CompressionServerToClient, "compression") != "none")
            throw new SshException("no common compression");
        return new(kex, hostKey, outward, inward, macOut, macIn);
    }
}

/// <summary>The NIST curves by their SSH name, with the hash their ECDSA and ECDH use (RFC 5656 §6.2.1, §10.1).</summary>
internal static class SshCurve
{
    public static (ECCurve Curve, HashAlgorithmName Hash, int Size) Of(string name) => name switch
    {
        "nistp256" => (ECCurve.NamedCurves.nistP256, HashAlgorithmName.SHA256, 32),
        "nistp384" => (ECCurve.NamedCurves.nistP384, HashAlgorithmName.SHA384, 48),
        "nistp521" => (ECCurve.NamedCurves.nistP521, HashAlgorithmName.SHA512, 66),
        _ => throw new SshException("unsupported curve " + name),
    };

    public static string NameOf(int keySize) => keySize switch
    {
        256 => "nistp256",
        384 => "nistp384",
        521 => "nistp521",
        _ => throw new SshException("unsupported curve"),
    };

    /// <summary>A point in SEC1 uncompressed form: 4, X, Y.</summary>
    public static byte[] Encode(ECPoint point) => [4, .. point.X!, .. point.Y!];

    public static ECPoint Decode(byte[] point, int size) =>
        point.Length == 1 + 2 * size && point[0] == 4
            ? new ECPoint { X = point[1..(1 + size)], Y = point[(1 + size)..] }
            : throw new SshException("bad curve point");
}

/// <summary>Our ephemeral half of an ecdh-sha2-* exchange (RFC 5656 §4).</summary>
internal sealed class SshEcdh : IDisposable
{
    private readonly ECDiffieHellman key;
    private readonly ECCurve curve;
    private readonly int size;

    public SshEcdh(string kex)
    {
        var (named, hash, bytes) = SshCurve.Of(kex.StartsWith("ecdh-sha2-", StringComparison.Ordinal) ? kex["ecdh-sha2-".Length..] : kex);
        curve = named;
        Hash = hash;
        size = bytes;
        key = ECDiffieHellman.Create(curve);
        PublicPoint = SshCurve.Encode(key.ExportParameters(false).Q);
    }

    /// <summary>The exchange hash and key derivation hash of this curve.</summary>
    public HashAlgorithmName Hash { get; }

    public byte[] PublicPoint { get; }

    /// <summary>The shared secret K: the x coordinate of the agreed point, unsigned big-endian. The peer's point must
    /// be on the curve.</summary>
    public byte[] Agree(byte[] peerPoint)
    {
        try
        {
            using var peer = ECDiffieHellman.Create(new ECParameters { Curve = curve, Q = SshCurve.Decode(peerPoint, size) });
            using var peerKey = peer.PublicKey;
            return key.DeriveRawSecretAgreement(peerKey);
        }
        catch (CryptographicException) { throw new SshException("bad key exchange"); }
    }

    public void Dispose() => key.Dispose();
}
