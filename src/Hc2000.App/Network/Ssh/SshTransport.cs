using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Hc2000.App.Network.Ssh;

/// <summary>The SSH binary packet protocol (RFC 4253 §4.2, §6) over a byte channel: the version exchange, then packets
/// with random padding, a sequence number each way and the cipher in force each way. One reader at a time; writers
/// hold <see cref="WriteLock"/> around <see cref="WritePacketAsync"/>, or call <see cref="SendAsync"/>, which does.
/// The client and the test server both run on it.</summary>
internal sealed class SshTransport(IX6Channel io) : IDisposable
{
    /// <summary>The largest packet taken (OpenSSH's limit; RFC 4253 asks for at least 35000 bytes).</summary>
    public const int MaximumPacket = 256 * 1024;
    private readonly byte[] input = new byte[16 * 1024];
    private int inputStart, inputEnd;
    private SshPacketCipher sendCipher = new SshPlainCipher(), receiveCipher = new SshPlainCipher();

    public SemaphoreSlim WriteLock { get; } = new(1, 1);
    public uint SendSequence { get; private set; }
    public uint ReceiveSequence { get; private set; }

    /// <summary>Sends our version line and returns the peer's, without its CR LF. Lines before it are skipped (a server
    /// may send some).</summary>
    public async Task<string> ExchangeVersionsAsync(string ours, CancellationToken cancellation)
    {
        await io.WriteAsync(Encoding.ASCII.GetBytes(ours + "\r\n"), cancellation).ConfigureAwait(false);
        for (var lines = 0; lines < 32; lines++)
        {
            var line = await ReadLineAsync(cancellation).ConfigureAwait(false);
            if (!line.StartsWith("SSH-", StringComparison.Ordinal)) continue;
            if (line.StartsWith("SSH-2.0-", StringComparison.Ordinal) || line.StartsWith("SSH-1.99-", StringComparison.Ordinal))
                return line;
            throw new SshException("not an SSH-2 server");
        }
        throw new SshException("not an SSH server");
    }

    /// <summary>Writes one packet. The caller holds <see cref="WriteLock"/>.</summary>
    public async Task WritePacketAsync(byte[] payload, CancellationToken cancellation)
    {
        var cipher = sendCipher;
        var align = cipher.BlockSize;
        var padding = align - (5 + payload.Length - (cipher.LengthInClear ? 4 : 0)) % align;
        if (padding < 4) padding += align;
        var length = 1 + payload.Length + padding;
        var packet = new byte[4 + length + cipher.MacLength];
        BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)length);
        packet[4] = (byte)padding;
        payload.CopyTo(packet, 5);
        RandomNumberGenerator.Fill(packet.AsSpan(5 + payload.Length, padding));
        cipher.Seal(SendSequence++, packet.AsSpan(0, 4 + length), packet.AsSpan(4 + length));
        await io.WriteAsync(packet, cancellation).ConfigureAwait(false);
    }

    /// <summary>Writes one packet under <see cref="WriteLock"/>.</summary>
    public async Task SendAsync(byte[] payload, CancellationToken cancellation)
    {
        await WriteLock.WaitAsync(cancellation).ConfigureAwait(false);
        try { await WritePacketAsync(payload, cancellation).ConfigureAwait(false); }
        finally { WriteLock.Release(); }
    }

    /// <summary>Reads one packet and returns its payload, message number first.</summary>
    public async Task<byte[]> ReadPacketAsync(CancellationToken cancellation)
    {
        var cipher = receiveCipher;
        var first = new byte[cipher.FirstBlock];
        await ReadExactAsync(first, cancellation).ConfigureAwait(false);
        var length = cipher.ReadLength(first);
        if (length < 6 || length > MaximumPacket || (4 + length - (cipher.LengthInClear ? 4 : 0)) % cipher.BlockSize != 0)
            throw new SshException("bad packet length");
        var packet = new byte[4 + length + cipher.MacLength];
        first.CopyTo(packet, 0);
        await ReadExactAsync(packet.AsMemory(first.Length), cancellation).ConfigureAwait(false);
        var body = packet.AsSpan(0, 4 + (int)length);
        if (!cipher.Open(ReceiveSequence++, body, packet.AsSpan(body.Length))) throw new SshException("corrupt packet");
        var padding = packet[4];
        if (padding < 4 || padding > length - 2) throw new SshException("bad packet padding");
        return packet[5..(4 + (int)length - padding)];
    }

    /// <summary>The cipher for what we send, from our NEWKEYS on; strict key exchange restarts the count.</summary>
    public void UseSendCipher(SshPacketCipher cipher, bool restartSequence)
    {
        sendCipher.Dispose();
        sendCipher = cipher;
        if (restartSequence) SendSequence = 0;
    }

    /// <summary>The cipher for what we receive, from the peer's NEWKEYS on.</summary>
    public void UseReceiveCipher(SshPacketCipher cipher, bool restartSequence)
    {
        receiveCipher.Dispose();
        receiveCipher = cipher;
        if (restartSequence) ReceiveSequence = 0;
    }

    public void Dispose() => io.Dispose();

    private async Task<string> ReadLineAsync(CancellationToken cancellation)
    {
        var line = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            await ReadExactAsync(one, cancellation).ConfigureAwait(false);
            if (one[0] == '\n') break;
            if (line.Count == 255) throw new SshException("not an SSH server");
            line.Add(one[0]);
        }
        if (line.Count > 0 && line[^1] == '\r') line.RemoveAt(line.Count - 1);
        return Encoding.Latin1.GetString(line.ToArray());
    }

    private async ValueTask ReadExactAsync(Memory<byte> destination, CancellationToken cancellation)
    {
        while (!destination.IsEmpty)
        {
            if (inputStart == inputEnd)
            {
                inputStart = 0;
                inputEnd = await io.ReadAsync(input, cancellation).ConfigureAwait(false);
                if (inputEnd == 0) throw new SshException("connection closed");
            }
            var count = Math.Min(destination.Length, inputEnd - inputStart);
            input.AsMemory(inputStart, count).CopyTo(destination);
            inputStart += count;
            destination = destination[count..];
        }
    }
}

/// <summary>One direction's packet protection: how a packet is padded, sealed and opened.</summary>
internal abstract class SshPacketCipher : IDisposable
{
    /// <summary>What the padded packet aligns to.</summary>
    public abstract int BlockSize { get; }

    /// <summary>The MAC or tag after each packet.</summary>
    public abstract int MacLength { get; }

    /// <summary>The bytes read before the packet length is known.</summary>
    public virtual int FirstBlock => BlockSize;

    /// <summary>True when the length goes in clear, as associated data (AES-GCM).</summary>
    public virtual bool LengthInClear => false;

    /// <summary>The packet length, from the first block, which it decrypts in place where it must.</summary>
    public abstract uint ReadLength(Span<byte> first);

    /// <summary>Encrypts a whole packet (from its length on) in place and writes its MAC or tag.</summary>
    public abstract void Seal(uint sequence, Span<byte> packet, Span<byte> mac);

    /// <summary>Decrypts the rest of a packet in place (its first block is done) and checks its MAC or tag.</summary>
    public abstract bool Open(uint sequence, Span<byte> packet, ReadOnlySpan<byte> mac);

    public virtual void Dispose() { }
}

/// <summary>Before the first NEWKEYS: no encryption and no MAC, packets aligned to 8.</summary>
internal sealed class SshPlainCipher : SshPacketCipher
{
    public override int BlockSize => 8;
    public override int MacLength => 0;
    public override uint ReadLength(Span<byte> first) => BinaryPrimitives.ReadUInt32BigEndian(first);
    public override void Seal(uint sequence, Span<byte> packet, Span<byte> mac) { }
    public override bool Open(uint sequence, Span<byte> packet, ReadOnlySpan<byte> mac) => true;
}

/// <summary>aes128-gcm@openssh.com and aes256-gcm@openssh.com (RFC 5647 as OpenSSH does it): the length goes in clear
/// as associated data, the rest is encrypted, a 16-byte tag follows, and the 12-byte nonce is the derived IV whose
/// last 8 bytes count packets. The negotiated MAC is not used.</summary>
internal sealed class SshGcmCipher(byte[] key, byte[] iv) : SshPacketCipher
{
    private readonly AesGcm gcm = new(key, 16);

    public override int BlockSize => 16;
    public override int MacLength => 16;
    public override int FirstBlock => 4;
    public override bool LengthInClear => true;

    public override uint ReadLength(Span<byte> first) => BinaryPrimitives.ReadUInt32BigEndian(first);

    public override void Seal(uint sequence, Span<byte> packet, Span<byte> mac)
    {
        var plain = packet[4..].ToArray();
        gcm.Encrypt(iv, plain, packet[4..], mac, packet[..4]);
        NextNonce();
    }

    public override bool Open(uint sequence, Span<byte> packet, ReadOnlySpan<byte> mac)
    {
        var sealedPart = packet[4..].ToArray();
        try { gcm.Decrypt(iv, sealedPart, mac, packet[4..], packet[..4]); }
        catch (CryptographicException) { return false; }
        NextNonce();
        return true;
    }

    public override void Dispose() => gcm.Dispose();

    private void NextNonce() =>
        BinaryPrimitives.WriteUInt64BigEndian(iv.AsSpan(4), BinaryPrimitives.ReadUInt64BigEndian(iv.AsSpan(4)) + 1);
}

/// <summary>aes128-ctr and aes256-ctr (RFC 4344, the counter a 128-bit big-endian number running across packets) with
/// hmac-sha2-256 or hmac-sha2-512 (RFC 6668) over the sequence number and the plain packet.</summary>
internal sealed class SshCtrCipher : SshPacketCipher
{
    private readonly Aes aes = Aes.Create();
    private readonly byte[] counter;
    private readonly IncrementalHash hmac;
    private readonly int macLength;

    public SshCtrCipher(byte[] key, byte[] iv, string mac, byte[] macKey)
    {
        aes.Key = key;
        counter = iv;
        (var hash, macLength) = mac == "hmac-sha2-512" ? (HashAlgorithmName.SHA512, 64) : (HashAlgorithmName.SHA256, 32);
        hmac = IncrementalHash.CreateHMAC(hash, macKey);
    }

    public override int BlockSize => 16;
    public override int MacLength => macLength;

    public override uint ReadLength(Span<byte> first)
    {
        Transform(first);
        return BinaryPrimitives.ReadUInt32BigEndian(first);
    }

    public override void Seal(uint sequence, Span<byte> packet, Span<byte> mac)
    {
        Mac(sequence, packet, mac);
        Transform(packet);
    }

    public override bool Open(uint sequence, Span<byte> packet, ReadOnlySpan<byte> mac)
    {
        Transform(packet[FirstBlock..]);
        Span<byte> expected = stackalloc byte[macLength];
        Mac(sequence, packet, expected);
        return CryptographicOperations.FixedTimeEquals(expected, mac);
    }

    public override void Dispose()
    {
        aes.Dispose();
        hmac.Dispose();
    }

    private void Mac(uint sequence, ReadOnlySpan<byte> packet, Span<byte> destination)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, sequence);
        hmac.AppendData(number);
        hmac.AppendData(packet);
        hmac.GetHashAndReset(destination);
    }

    /// <summary>XORs whole blocks with the key stream: the counter blocks, encrypted.</summary>
    private void Transform(Span<byte> data)
    {
        var blocks = new byte[data.Length];
        for (var offset = 0; offset < blocks.Length; offset += 16)
        {
            counter.CopyTo(blocks, offset);
            for (var i = 15; i >= 0 && ++counter[i] == 0; i--) { }
        }
        var stream = aes.EncryptEcb(blocks, PaddingMode.None);
        for (var i = 0; i < data.Length; i++) data[i] ^= stream[i];
    }
}
