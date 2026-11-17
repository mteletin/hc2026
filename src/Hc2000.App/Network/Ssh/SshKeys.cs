using System.Numerics;
using System.Security.Cryptography;

namespace Hc2000.App.Network.Ssh;

/// <summary>A server's host key, as offered to the trust decision: where it came from, its type and wire blob (as a
/// known_hosts line holds them), and the fingerprint OpenSSH shows for it.</summary>
public sealed record SshHostKey(string Host, int Port, string KeyType, byte[] Blob)
{
    /// <summary>"SHA256:" and the unpadded base64 of the blob's SHA-256, as <c>ssh-keygen -l</c> prints it.</summary>
    public string Fingerprint => SshSignatures.Fingerprint(Blob);
}

/// <summary>Checks SSH signatures: ecdsa-sha2-nistp256/384/521 (RFC 5656 §3.1.2) and rsa-sha2-256/512 over an ssh-rsa
/// key of 1024 bits or more (RFC 8332).</summary>
internal static class SshSignatures
{
    /// <summary>The host key algorithms offered, best first.</summary>
    public static readonly string[] Algorithms =
        ["ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "rsa-sha2-512", "rsa-sha2-256"];

    public static string Fingerprint(byte[] blob) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

    /// <summary>The key type a blob names (its first string).</summary>
    public static string KeyTypeOf(byte[] blob) => new SshReader(blob).Text();

    /// <summary>True when <paramref name="signatureBlob"/> is a good <paramref name="algorithm"/> signature of
    /// <paramref name="data"/> by the key in <paramref name="keyBlob"/>.</summary>
    public static bool Verify(byte[] keyBlob, string algorithm, byte[] signatureBlob, byte[] data)
    {
        try
        {
            var key = new SshReader(keyBlob);
            var signature = new SshReader(signatureBlob);
            var keyType = key.Text();
            if (signature.Text() != algorithm) return false;
            var value = signature.String();
            if (algorithm.StartsWith("ecdsa-sha2-", StringComparison.Ordinal))
            {
                var name = key.Text();
                if (keyType != algorithm || algorithm != "ecdsa-sha2-" + name) return false;
                var (curve, hash, size) = SshCurve.Of(name);
                using var ecdsa = ECDsa.Create(new ECParameters { Curve = curve, Q = SshCurve.Decode(key.String(), size) });
                var rs = new SshReader(value);
                byte[] pair = [.. SshReader.PadLeft(rs.Mpint(), size), .. SshReader.PadLeft(rs.Mpint(), size)];
                return ecdsa.VerifyData(data, pair, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            if (algorithm is not ("rsa-sha2-256" or "rsa-sha2-512") || keyType != "ssh-rsa") return false;
            var exponent = key.Mpint();
            var modulus = key.Mpint();
            if (modulus.Length < 128) return false;
            using var rsa = RSA.Create(new RSAParameters { Exponent = exponent, Modulus = modulus });
            return rsa.VerifyData(data, SshReader.PadLeft(value, modulus.Length),
                algorithm == "rsa-sha2-512" ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception error) when (error is SshException or CryptographicException) { return false; }
    }
}

/// <summary>A key that signs: ECDSA on a NIST curve, or RSA signing rsa-sha2-512 or rsa-sha2-256. A user's key comes
/// from an unencrypted private key file: OpenSSH's own (openssh-key-v1, "BEGIN OPENSSH PRIVATE KEY") or PEM (PKCS #8
/// "BEGIN PRIVATE KEY", "BEGIN EC PRIVATE KEY", "BEGIN RSA PRIVATE KEY"); passphrase-protected keys are refused, per
/// decision hc2000-ssh-client-inhouse-v1 (no bcrypt). The test server's host key is made in memory.</summary>
public sealed class SshPrivateKey : IDisposable
{
    private const string Begin = "-----BEGIN OPENSSH PRIVATE KEY-----", End = "-----END OPENSSH PRIVATE KEY-----";
    private readonly ECDsa? ecdsa;
    private readonly RSA? rsa;

    private SshPrivateKey(ECDsa? ecdsa, RSA? rsa, string comment)
    {
        this.ecdsa = ecdsa;
        this.rsa = rsa;
        Comment = comment;
        if (ecdsa is not null)
        {
            var name = SshCurve.NameOf(ecdsa.KeySize);
            KeyType = "ecdsa-sha2-" + name;
            PublicBlob = new SshWriter().String(KeyType).String(name).String(SshCurve.Encode(ecdsa.ExportParameters(false).Q)).ToArray();
            Algorithms = [KeyType];
        }
        else
        {
            var parameters = rsa!.ExportParameters(false);
            KeyType = "ssh-rsa";
            PublicBlob = new SshWriter().String(KeyType).Mpint(parameters.Exponent).Mpint(parameters.Modulus).ToArray();
            Algorithms = ["rsa-sha2-512", "rsa-sha2-256"];
        }
    }

    /// <summary>"ecdsa-sha2-nistp256" and the like, or "ssh-rsa".</summary>
    public string KeyType { get; }

    /// <summary>The public key as it goes on the wire (and, base64, in a .pub or authorized_keys line).</summary>
    public byte[] PublicBlob { get; }

    public string Comment { get; }

    /// <summary>The signature algorithms it can make, best first.</summary>
    public IReadOnlyList<string> Algorithms { get; }

    public string Fingerprint => SshSignatures.Fingerprint(PublicBlob);

    internal static SshPrivateKey FromEcdsa(ECDsa key, string comment = "") => new(key, null, comment);

    internal static SshPrivateKey FromRsa(RSA key, string comment = "") => new(null, key, comment);

    /// <summary>Reads an unencrypted key file holding one ECDSA or RSA key (OpenSSH or PEM); anything else fails with
    /// the reason (an encrypted key, ssh-ed25519, another curve, a damaged file).</summary>
    public static SshPrivateKey Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains(Begin, StringComparison.Ordinal)) return ParsePem(text);
        var start = text.IndexOf(Begin, StringComparison.Ordinal);
        var end = text.IndexOf(End, StringComparison.Ordinal);
        if (start < 0 || end < start) throw new SshException("not an OpenSSH private key");
        try
        {
            var data = Convert.FromBase64String(text[(start + Begin.Length)..end]);
            var magic = "openssh-key-v1\0"u8;
            if (!data.AsSpan().StartsWith(magic)) throw new SshException("not an OpenSSH private key");
            var file = new SshReader(data, magic.Length);
            if (file.Text() != "none" || file.Text() != "none") throw new SshException("encrypted keys are not supported");
            file.String();
            if (file.UInt32() != 1) throw new SshException("one key per file");
            var publicBlob = file.String();
            var secret = new SshReader(file.String());
            if (secret.UInt32() != secret.UInt32()) throw new SshException("bad key file");
            var type = secret.Text();
            ECDsa? ecdsa = null;
            RSA? rsa = null;
            if (type.StartsWith("ecdsa-sha2-", StringComparison.Ordinal))
            {
                var name = secret.Text();
                var (curve, _, size) = SshCurve.Of(name);
                var q = SshCurve.Decode(secret.String(), size);
                ecdsa = ECDsa.Create(new ECParameters { Curve = curve, Q = q, D = SshReader.PadLeft(secret.Mpint(), size) });
            }
            else if (type == "ssh-rsa")
            {
                // The private part's order: n, e, d, iqmp (q^-1 mod p), p, q.
                var (n, e, d, iqmp, p, q) = (secret.Mpint(), secret.Mpint(), secret.Mpint(), secret.Mpint(), secret.Mpint(), secret.Mpint());
                rsa = RSA.Create(RsaParameters(n, e, d, iqmp, p, q));
            }
            else throw new SshException("unsupported key type " + type);
            var key = new SshPrivateKey(ecdsa, rsa, secret.Text());
            if (!key.PublicBlob.AsSpan().SequenceEqual(publicBlob))
            {
                key.Dispose();
                throw new SshException("bad key file");
            }
            return key;
        }
        catch (Exception error) when (error is FormatException or CryptographicException || error.Message == "malformed message")
        {
            throw new SshException("bad key file");
        }
    }

    // PEM: PKCS #8 (either kind), SEC 1 "EC PRIVATE KEY" or PKCS #1 "RSA PRIVATE KEY", through the platform's importers.
    private static SshPrivateKey ParsePem(string text)
    {
        if (!text.Contains("-----BEGIN ", StringComparison.Ordinal) || !text.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
            throw new SshException("not a private key file");
        if (text.Contains("ENCRYPTED", StringComparison.Ordinal) || text.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.Ordinal))
            throw new SshException("encrypted keys are not supported");
        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(text);
            var oid = ecdsa.ExportParameters(false).Curve.Oid?.Value;
            if (oid is not ("1.2.840.10045.3.1.7" or "1.3.132.0.34" or "1.3.132.0.35"))
                throw new SshException("unsupported curve, only nistp256, nistp384 and nistp521");
            return new SshPrivateKey(ecdsa, null, "");
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException)
        {
            ecdsa.Dispose();
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(text);
            if (rsa.KeySize < 2048) throw new SshException("RSA keys need at least 2048 bits");
            return new SshPrivateKey(null, rsa, "");
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException)
        {
            rsa.Dispose();
            throw new SshException("not a usable ECDSA or RSA key");
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    /// <summary>A signature blob: the algorithm name, then r and s as mpints (ECDSA) or the PKCS #1 v1.5 signature (RSA).</summary>
    internal byte[] Sign(string algorithm, byte[] data)
    {
        if (ecdsa is not null)
        {
            var (_, hash, size) = SshCurve.Of(KeyType["ecdsa-sha2-".Length..]);
            var rs = ecdsa.SignData(data, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var pair = new SshWriter().Mpint(rs.AsSpan(0, size)).Mpint(rs.AsSpan(size)).ToArray();
            return new SshWriter().String(KeyType).String(pair).ToArray();
        }
        var digest = algorithm switch
        {
            "rsa-sha2-512" => HashAlgorithmName.SHA512,
            "rsa-sha2-256" => HashAlgorithmName.SHA256,
            _ => throw new ArgumentException("not an RSA signature algorithm", nameof(algorithm)),
        };
        return new SshWriter().String(algorithm).String(rsa!.SignData(data, digest, RSASignaturePadding.Pkcs1)).ToArray();
    }

    public void Dispose()
    {
        ecdsa?.Dispose();
        rsa?.Dispose();
    }

    /// <summary>The .NET form of an RSA private key, which also wants d mod (p-1) and d mod (q-1), every part at its
    /// exact length.</summary>
    private static RSAParameters RsaParameters(byte[] n, byte[] e, byte[] d, byte[] iqmp, byte[] p, byte[] q)
    {
        var half = (n.Length + 1) / 2;
        static BigInteger Number(byte[] value) => new(value, isUnsigned: true, isBigEndian: true);
        byte[] Part(BigInteger value) => SshReader.PadLeft(value.ToByteArray(isUnsigned: true, isBigEndian: true), half);
        return new RSAParameters
        {
            Modulus = n,
            Exponent = e,
            D = SshReader.PadLeft(d, n.Length),
            P = SshReader.PadLeft(p, half),
            Q = SshReader.PadLeft(q, half),
            InverseQ = SshReader.PadLeft(iqmp, half),
            DP = Part(Number(d) % (Number(p) - 1)),
            DQ = Part(Number(d) % (Number(q) - 1)),
        };
    }
}
