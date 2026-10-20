using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Hc2000.XvfbHarness;

/// <summary>
/// XTEST without libXtst. A minimal X protocol client: it opens its own connection to the same
/// X server, asks for the XTEST extension and sends XTestFakeKeyEvent requests. The server injects
/// these at its input layer, so the app receives ordinary KeyPress/KeyRelease events with
/// send_event = false - the property that separates XTEST from XSendEvent, and the reason the
/// acceptance asks for XTEST. Written by hand because host-02 has no libXtst, no compiler and no
/// way to install either.
/// </summary>
internal sealed class XTestRaw : IDisposable
{
    const byte OpQueryExtension = 98;   // core X protocol
    const byte OpChangeWindowAttributes = 2;
    // Measured on this box's Xvfb (XTEST 2.0) with no XTEST headers available: the only accepted
    // fake-key request is XTestFakeInput, minor 2, 36 bytes (declared length 9). Minor 6 answers
    // BadRequest and lengths 2..8 and 10 answer BadLength, so this number is not a guess.
    const byte XTestFakeInputMinor = 2;
    const int KeyPress = 2, KeyRelease = 3;

    // The XTEST request framing is not documented in any header on this box, so it is settled by
    // measurement: HC_XTEST_MINOR picks the extension's minor opcode and HC_XTEST_FRAMING the
    // request layout, and --xtest-probe reports which combination a window actually sees a key from.
    static readonly int Framing = int.TryParse(Environment.GetEnvironmentVariable("HC_XTEST_FRAMING"), out var f) ? f : 0;
    static readonly int Minor = int.TryParse(Environment.GetEnvironmentVariable("HC_XTEST_MINOR"), out var m) ? m : XTestFakeInputMinor;
    // XTEST 2.2 injects through a server device, and device id 0 is not one - the server answers
    // BadValue. The core keyboard is normally id 3; HC_XTEST_DEVID lets the probe find it.
    static int DevId = int.TryParse(Environment.GetEnvironmentVariable("HC_XTEST_DEVID"), out var v) ? v : 0;
    public static void SetDevId(int v) => DevId = v;

    readonly Socket _sock;
    readonly NetworkStream _io;
    readonly byte _opcode;              // XTEST major opcode, as the server reports it
    int _verMajor, _verMinor;           // the XTEST version this connection negotiated
    public readonly List<string> LastErrors = new();
    public string VersionText => _verMajor == 0 ? "not negotiated" : $"{_verMajor}.{_verMinor}";

    XTestRaw(Socket sock, byte opcode)
    {
        _sock = sock;
        _io = new NetworkStream(sock, ownsSocket: true);
        _opcode = opcode;
    }

    /// <summary>
    /// Connects to <paramref name="display"/> (":97", ":97.0" or "host:97") and finds XTEST.
    /// Returns null and a reason when the server refuses the connection or has no XTEST, so the
    /// caller can fall back to XSendEvent and say so in the receipt.
    /// </summary>
    public static XTestRaw? Connect(string? display, out string why)
    {
        why = "";
        var d = display ?? Environment.GetEnvironmentVariable("DISPLAY") ?? "";
        var colon = d.LastIndexOf(':');
        if (colon < 0) { why = $"cannot parse DISPLAY '{d}'"; return null; }
        var host = d[..colon];
        var numText = d[(colon + 1)..];
        var dot = numText.IndexOf('.');
        if (dot >= 0) numText = numText[..dot];
        if (!int.TryParse(numText, out var num)) { why = $"cannot parse DISPLAY '{d}'"; return null; }

        Socket sock;
        try
        {
            if (host.Length == 0)
            {
                // Local display: the unix socket the server owns.
                sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                sock.Connect(new UnixDomainSocketEndPoint($"/tmp/.X11-unix/X{num}"));
            }
            else
            {
                sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sock.Connect(host, 6000 + num);
            }
            sock.SendTimeout = 5000;
            sock.ReceiveTimeout = 5000;
        }
        catch (Exception e)
        {
            why = $"connect to display '{d}' failed: {e.GetType().Name}: {e.Message}";
            return null;
        }

        var step = "connect";
        try
        {
            var io = new NetworkStream(sock, ownsSocket: false);
            // Connection setup request: little-endian, protocol 11.0, no authorization. Xvfb started
            // by this harness runs without xauth, so an unauthenticated connection is accepted.
            step = "setup request";
            var setup = new byte[12];
            setup[0] = (byte)'l';
            BinaryPrimitives.WriteUInt16LittleEndian(setup.AsSpan(2, 2), 11);
            io.Write(setup);
            io.Flush();
            step = "setup reply";
            var reply = Read(io, 8);
            var extra = BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(6, 2)) * 4;
            var body = Read(io, extra);
            if (reply[0] != 1)
            {
                var len = reply[1];
                var reason = len > 0 && body.Length >= len ? Encoding.UTF8.GetString(body[..len]) : "no reason given";
                why = $"server refused the connection: {reason}";
                sock.Dispose();
                return null;
            }

            // QueryExtension("XTEST"): opcode 98, then the name. The reply is the generic 8-byte
            // header plus 24 bytes of fixed data, in which byte 8 is the set flag and byte 9 the
            // extension's major opcode.
            step = "QueryExtension";
            var name = "XTEST"u8.ToArray();
            var req = new byte[8 + ((name.Length + 3) / 4) * 4];
            req[0] = OpQueryExtension;
            BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(2, 2), (ushort)(2 + (name.Length + 3) / 4));
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(4, 4), (uint)name.Length);
            name.CopyTo(req.AsSpan(8));
            io.Write(req);
            io.Flush();
            step = "QueryExtension reply";
            var ext = Read(io, 32);
            var extExtra = (int)BinaryPrimitives.ReadUInt32LittleEndian(ext.AsSpan(4, 4)) * 4;
            if (extExtra > 0) Read(io, extExtra);
            if (ext[0] != 1) { why = "QueryExtension got no reply"; sock.Dispose(); return null; }
            if (ext[8] == 0 || ext[9] == 0) { why = "this X server has no XTEST extension"; sock.Dispose(); return null; }

            // XTestGetVersion (minor 0): ask for 2.2. The 2.2 requests carry a device id, and a
            // server that still thinks this client speaks 1.0 answers the fake-key request with
            // BadValue, which is exactly the failure the probe was chasing.
            step = "XTestGetVersion";
            var vreq = new byte[8];
            vreq[0] = ext[9];
            BinaryPrimitives.WriteUInt16LittleEndian(vreq.AsSpan(2, 2), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(vreq.AsSpan(4, 2), 2);   // majorVersion
            BinaryPrimitives.WriteUInt16LittleEndian(vreq.AsSpan(6, 2), 2);   // minorVersion
            io.Write(vreq);
            io.Flush();
            var vrep = Read(io, 32);
            var raw = new XTestRaw(sock, ext[9]);
            if (vrep[0] == 1)
            {
                raw._verMajor = BinaryPrimitives.ReadUInt16LittleEndian(vrep.AsSpan(8, 2));
                raw._verMinor = BinaryPrimitives.ReadUInt16LittleEndian(vrep.AsSpan(10, 2));
            }
            else
            {
                why = $"XTestGetVersion failed (server error code {vrep[0]}); negotiated nothing";
            }
            return raw;
        }
        catch (Exception e)
        {
            try { sock.Shutdown(SocketShutdown.Both); } catch { }
            sock.Dispose();
            why = $"XTEST handshake failed at {step}: {e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    static byte[] Read(NetworkStream io, int n)
    {
        var buf = new byte[n];
        var got = 0;
        while (got < n)
        {
            var k = io.Read(buf.AsSpan(got, n - got));
            if (k <= 0) throw new IOException($"server closed the connection after {got} of {n} bytes");
            got += k;
        }
        return buf;
    }

    /// <summary>
    /// One XTEST fake key event, in the framing this server accepts. The request header is the
    /// extension's major opcode, the minor opcode, then the request length as a CARD16 at bytes 2-3
    /// in 4-byte units (36 bytes = 9). The event type sits at byte 4 (2 = KeyPress, 3 = KeyRelease),
    /// the keycode at byte 5, flags at 6, the timestamp at 8 (0 = CurrentTime) and the device id at
    /// 20 (0 = the server's core keyboard). A declared length that does not match what follows
    /// desynchronizes the connection, which looks exactly like "the keys never arrived", so the
    /// layout was read off the server rather than guessed: every other length answers BadLength and
    /// keycode 0 answers BadValue, which pins those fields to those offsets.
    /// </summary>
    public void FakeKey(int keycode, bool press)
    {
        var req = new byte[36];
        req[0] = _opcode;
        req[1] = XTestFakeInputMinor;
        BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(2, 2), 9);
        req[4] = (byte)(press ? KeyPress : KeyRelease);
        req[5] = (byte)keycode;
        if (DevId != 0) BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(20, 2), (ushort)DevId);
        _io.Write(req);
        _io.Flush();
    }


    public void Dispose()
    {
        try { _io.Dispose(); } catch { }
        try { _sock.Dispose(); } catch { }
    }
}
