using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Hc2000.XvfbHarness;

/// <summary>
/// The X11 side of the harness: find the app window, read the picture the server holds for it,
/// and deliver keys. Only libX11 is required. XTEST (libXtst) is used when the box provides it;
/// XSendEvent is the dependency-free fallback and is recorded as such in the receipt.
/// </summary>
internal sealed class X11 : IDisposable
{
    const int KeyPress = 2;
    const int KeyRelease = 3;
    // EventMask bits, read off the server: on the wire 1<<0 selects KeyPress and 1<<1 KeyRelease
    // (measured on Xvfb :95, 2026-10-18). Getting these wrong looks exactly like "the injected
    // keys never arrived": the server delivers releases and silently drops presses.
    const uint KeyPressMask = 1u << 0;
    const uint KeyReleaseMask = 1u << 1;
    const int ZPixmap = 2;
    const nuint AllPlanes = 0xFFFFFFFFu;
    const int Success = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct XImage
    {
        public int width, height, xoffset, format;
        public nint data;
        public int byte_order, bitmap_unit, bitmap_pad, bitmap_bit_order, depth, bytes_per_line, bits_per_pixel;
        public nuint red_mask, green_mask, blue_mask;
    }

    delegate int XErrorHandler(nint eventc);
    static readonly XErrorHandler _ignoreXError = _ => 0;

    [DllImport("libX11.so.6")] static extern nint XSetErrorHandler(XErrorHandler handler);
    [DllImport("libX11.so.6")] static extern nint XOpenDisplay(string? displayName);
    [DllImport("libX11.so.6")] static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] static extern nuint XDefaultRootWindow(nint display);
    [DllImport("libX11.so.6")] static extern int XQueryTree(nint display, nuint window,
        out nuint rootReturn, out nuint parentReturn, out nint childrenReturn, out uint nChildrenReturn);
    [DllImport("libX11.so.6")] static extern nint XInternAtom(nint display, string atomName, bool onlyIfExists);
    [DllImport("libX11.so.6")] static extern int XGetWindowProperty(nint display, nuint window, nint property,
        long longOffset, long longLength, bool delete, nint reqType, out nint actualTypeReturn,
        out int actualFormatReturn, out nuint nitemsReturn, out nuint bytesAfterReturn, out nint propReturn);
    [DllImport("libX11.so.6")] static extern int XFree(nint data);
    [DllImport("libX11.so.6")] static extern int XGetGeometry(nint display, nuint drawable, out nuint rootReturn,
        out int xReturn, out int yReturn, out uint widthReturn, out uint heightReturn,
        out uint borderWidthReturn, out uint depthReturn);
    [DllImport("libX11.so.6")] static extern nint XGetImage(nint display, nuint drawable, int x, int y,
        uint width, uint height, nuint planeMask, int format);
    [DllImport("libX11.so.6")] static extern int XDestroyImage(nint image);
    [DllImport("libX11.so.6")] static extern nuint XStringToKeysym(string keystring);
    [DllImport("libX11.so.6")] static extern int XKeysymToKeycode(nint display, nuint keysym);
    [DllImport("libX11.so.6")] static extern int XSendEvent(nint display, nuint window, bool propagate,
        nuint eventMask, nint eventPtr);
    [DllImport("libX11.so.6")] static extern int XSetInputFocus(nint display, nuint window, int revertTo, nuint time);
    [DllImport("libX11.so.6")] static extern int XGetInputFocus(nint display, out nuint focusReturn, out int revertToReturn);
    [DllImport("libX11.so.6")] static extern nuint XCreateSimpleWindow(nint display, nuint parent, int x, int y,
        uint width, uint height, uint borderWidth, nuint border, nuint background);
    [DllImport("libX11.so.6")] static extern int XMapWindow(nint display, nuint window);
    [DllImport("libX11.so.6")] static extern int XDestroyWindow(nint display, nuint window);
    [DllImport("libX11.so.6")] static extern int XSync(nint display, bool discard);
    [DllImport("libX11.so.6")] static extern int XFlush(nint display);
    [DllImport("libX11.so.6")] static extern int XSelectInput(nint display, nuint window, nuint eventMask);
    [DllImport("libX11.so.6")] static extern int XCheckMaskEvent(nint display, nuint eventMask, nint eventPtr);

    // XTEST: server-level injection, the path closest to real hardware input.
    [DllImport("libXtst.so.6")] static extern int XTestQueryExtension(nint display,
        out int eventBaseReturn, out int errorBaseReturn, out int majorVersionReturn, out int minorVersionReturn);
    [DllImport("libXtst.so.6")] static extern int XTestFakeKeyEvent(nint display, int keycode,
        bool isPress, nuint delay);

    readonly nint _d;
    readonly string _display;
    XTestRaw? _raw;
    readonly Dictionary<char, (string Keysym, bool Shift)> _keycodes = new();

    X11(nint display, string? name)
    {
        _d = display;
        _display = name ?? Environment.GetEnvironmentVariable("DISPLAY") ?? "";
    }

    /// <summary>Opens a display ("<c>:97</c>" or null for $DISPLAY) and reports whether XTEST is usable.</summary>
    public static X11 Open(string? display, out string injector)
    {
        var d = XOpenDisplay(display);
        if (d == nint.Zero)
            throw new HarnessException($"cannot open DISPLAY '{display ?? Environment.GetEnvironmentVariable("DISPLAY")}'");
        // A window that is not viewable yet makes XGetImage raise BadMatch, which by default aborts the
        // process. Swallow it: XGetImage then returns NULL and Capture reports it as a harness error.
        _ = XSetErrorHandler(_ignoreXError);
        var x = new X11(d, display);
        x.QueryXTest();
        injector = x.HasXTest ? "xtest" : "sendevent";
        return x;
    }

    public bool HasXTest => XTestTransport != "none";

    /// <summary>How XTEST reaches the server: <c>libXtst</c>, <c>raw-protocol</c> or <c>none</c>.</summary>
    public string XTestTransport { get; private set; } = "none";

    /// <summary>Why XTEST is unavailable, when it is.</summary>
    public string XTestWhy { get; private set; } = "";

    /// <summary>The XTEST version the raw connection negotiated, or the transport name for libXtst.</summary>
    public string XTestVersion => _raw?.VersionText ?? XTestTransport;

    void QueryXTest()
    {
        try
        {
            if (XTestQueryExtension(_d, out _, out _, out _, out _) != 0) XTestTransport = "libXtst";
            return;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        // No libXtst on this box (host-02 has none and no way to install one). XTEST is a server-side
        // extension, so ask for it over the protocol directly: the injection still happens at the
        // server's input layer, which is the property the acceptance asks for.
        _raw = XTestRaw.Connect(_display, out var why);
        if (_raw is null) { XTestWhy = why; return; }
        XTestTransport = "raw-protocol";
    }

    public nuint Root => XDefaultRootWindow(_d);

    /// <summary>Depth-first search for the app window by title substring. XQueryTree returns 0 on failure.</summary>
    public nuint FindWindow(nuint w, string needle)
    {
        var name = WindowName(w);
        if (name is not null && name.Contains(needle, StringComparison.OrdinalIgnoreCase)) return w;
        if (XQueryTree(_d, w, out _, out _, out var kids, out var n) == 0 || kids == nint.Zero) return 0;
        for (var i = 0; i < (int)n; i++)
        {
            var hit = FindWindow((nuint)Marshal.ReadIntPtr(kids, i * IntPtr.Size), needle);
            if (hit != 0) return hit;
        }
        return 0;
    }

    public string? WindowName(nuint w)
    {
        foreach (var atom in new[] { "_NET_WM_NAME", "WM_NAME" })
        {
            var a = XInternAtom(_d, atom, false);
            if (XGetWindowProperty(_d, w, a, 0, 256, false, 0, out _, out _, out var nitems, out _, out var prop) != Success
                || prop == nint.Zero || (long)nitems == 0)
                continue;
            var s = Marshal.PtrToStringUTF8(prop);
            XFree(prop);
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return null;
    }

    /// <summary>
    /// Reads the server-side picture of the window: what the app has actually presented, which is
    /// the surface the wedge shows on. Hash is over the raw pixels, so a stale frame hashes stale.
    /// </summary>
    public Frame Capture(nuint w, bool keepPixels)
    {
        if (XGetGeometry(_d, w, out _, out _, out _, out var gw, out var gh, out _, out _) == 0)
            throw new HarnessException($"XGetGeometry failed for window 0x{w:x}");
        var img = XGetImage(_d, w, 0, 0, gw, gh, AllPlanes, ZPixmap);
        if (img == nint.Zero)
            throw new HarnessException($"XGetImage failed for window 0x{w:x} {gw}x{gh}");
        try
        {
            var x = Marshal.PtrToStructure<XImage>(img);
            var len = x.bytes_per_line * x.height;
            var pixels = new byte[len];
            Marshal.Copy(x.data, pixels, 0, len);
            var hash = Convert.ToHexString(SHA256.HashData(pixels))[..12];
            return new Frame(x.width, x.height, x.bits_per_pixel, (uint)x.red_mask, (uint)x.green_mask,
                (uint)x.blue_mask, x.bytes_per_line, hash, keepPixels ? pixels : null);
        }
        finally { XDestroyImage(img); }
    }

    /// <summary>Focuses <paramref name="w"/> and returns the window the server actually gave the
    /// keyboard to. XTEST injects at the server's input layer, so a key goes to the focus window -
    /// with no window manager running (Xvfb) nothing else sets that focus, and if it is not the app
    /// window the app never sees the key.</summary>
    public nuint Focus(nuint w)
    {
        XSetInputFocus(_d, w, 1 /* RevertToParent */, 0 /* CurrentTime */);
        XSync(_d, false);
        XGetInputFocus(_d, out var focus, out _);
        return focus;
    }

    /// <summary>A plain mapped window of our own, for the --xtest-probe delivery test.</summary>
    public nuint ProbeWindow()
    {
        var w = XCreateSimpleWindow(_d, Root, 0, 0, 64, 64, 0, 0xFFFFFF, 0x101010);
        XMapWindow(_d, w);
        XSync(_d, false);
        return w;
    }

    public void DestroyWindow(nuint w) { XDestroyWindow(_d, w); XSync(_d, false); }

    /// <summary>What one window's key selection saw: presses/releases, and whether any of them came
    /// with the send_event flag set (a synthetic XSendEvent does; a server-generated XTEST key does
    /// not). That flag is the difference the acceptance ruling turns on.</summary>
    public readonly record struct KeySight(int Press, int Release, bool SendEventFlag);

    readonly HashSet<nuint> _watched = new();

    /// <summary>Selects KeyPress|KeyRelease on <paramref name="w"/> on this connection, exactly the way
    /// xev does, so the run can show the injected keys arriving at that window with send_event clear.
    /// This is a second selection on the window and does not disturb the app's own selection.</summary>
    public void WatchKeys(nuint w)
    {
        if (w == 0 || !_watched.Add(w)) return;
        XSelectInput(_d, w, KeyPressMask | KeyReleaseMask);
        XSync(_d, false);
    }

    /// <summary>Errors the server sent back on the XTEST connection since the last drain.</summary>
    public List<string> RawErrors => _raw?.LastErrors ?? new List<string>();

    /// <summary>Reads the key events this connection has been sent since the last drain, grouped by the
    /// window whose selection caught them. XEvent is a union of 24 longs, so on a 64-bit build the
    /// XKeyEvent view puts type at 0, send_event at 16, the event window at 32 and the keycode at 80;
    /// XCheckMaskEvent dequeues only key events, so nothing else is swallowed.</summary>
    public (Dictionary<nuint, KeySight> ByWindow, int ProtocolErrors) DrainKeys(int maxEvents = 4096)
    {
        var by = new Dictionary<nuint, KeySight>();
        var buf = Marshal.AllocHGlobal(192);
        try
        {
            for (var i = 0; i < maxEvents && XCheckMaskEvent(_d, KeyPressMask | KeyReleaseMask, buf) != 0; i++)
            {
                var type = Marshal.ReadInt32(buf, 0);
                var win = (nuint)Marshal.ReadInt64(buf, 32);
                var sendEvent = Marshal.ReadInt32(buf, 16) != 0;
                var s = by.TryGetValue(win, out var old) ? old : new KeySight(0, 0, false);
                by[win] = s with
                {
                    Press = s.Press + (type == KeyPress ? 1 : 0),
                    Release = s.Release + (type == KeyRelease ? 1 : 0),
                    SendEventFlag = s.SendEventFlag || sendEvent,
                };
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return (by, RawErrors.Count);
    }

    public int KeycodeFor(char c)
    {
        var (keysym, _) = KeySym(c);
        return XKeysymToKeycode(_d, XStringToKeysym(keysym));
    }

    public void SendKey(nuint w, char c, string injector)
    {
        var (keysym, shift) = KeySym(c);
        var code = XKeysymToKeycode(_d, XStringToKeysym(keysym));
        if (code == 0) throw new HarnessException($"no keycode for '{c}' (keysym '{keysym}')");
        var shiftCode = shift ? XKeysymToKeycode(_d, XStringToKeysym("shiftleft")) : 0;
        if (shift && shiftCode == 0) throw new HarnessException("no keycode for shiftleft");

        if (injector == "xtest")
        {
            if (shiftCode != 0) { Fake(shiftCode, true); Thread.Sleep(20); }
            Fake(code, true); Thread.Sleep(60);
            Fake(code, false); Thread.Sleep(20);
            if (shiftCode != 0) Fake(shiftCode, false);
            XSync(_d, false);
            return;
        }

        var ev = Marshal.AllocHGlobal(192);
        try
        {
            for (var i = 0; i < 192; i++) Marshal.WriteByte(ev, i, 0);
            if (shiftCode != 0) SendSynthetic(ev, w, shiftCode, true);
            SendSynthetic(ev, w, code, true);
            SendSynthetic(ev, w, code, false);
            if (shiftCode != 0) SendSynthetic(ev, w, shiftCode, false);
            XSync(_d, false);
        }
        finally { Marshal.FreeHGlobal(ev); }
    }

    /// <summary>One XTEST fake key event, through libXtst when the box has it, else over the protocol.</summary>
    void Fake(int keycode, bool press)
    {
        if (_raw is not null) { _raw.FakeKey(keycode, press); return; }
        XTestFakeKeyEvent(_d, keycode, press, 0);
        XFlush(_d);
    }

    void SendSynthetic(nint ev, nuint w, int keycode, bool press)
    {
        Marshal.WriteInt32(ev, 0, press ? KeyPress : KeyRelease);
        Marshal.WriteIntPtr(ev, 32, (nint)w);      // window
        Marshal.WriteInt32(ev, 80, 1);             // same_screen
        Marshal.WriteInt32(ev, 84, keycode);       // keycode
        XSendEvent(_d, w, false, press ? KeyPressMask : KeyReleaseMask, ev);
        XFlush(_d);
        Thread.Sleep(60);
    }

    static (string Keysym, bool Shift) KeySym(char c) => c switch
    {
        >= 'a' and <= 'z' => (c.ToString(), false),
        >= 'A' and <= 'Z' => (char.ToLowerInvariant(c).ToString(), true),
        >= '0' and <= '9' => (c.ToString(), false),
        ' ' => ("space", false),
        '.' => ("period", false),
        ',' => ("comma", false),
        '\n' or '\r' => ("Return", false),
        '\t' => ("Tab", false),
        _ => throw new HarnessException($"key '{c}' is not mapped; use letters, digits, space, '.', ',', Enter"),
    };

    public void Dispose()
    {
        _raw?.Dispose();
        if (_d != nint.Zero) XCloseDisplay(_d);
    }
}

internal sealed record Frame(int Width, int Height, int BitsPerPixel, uint RedMask, uint GreenMask,
    uint BlueMask, int BytesPerLine, string Hash, byte[]? Pixels)
{
    public void WritePng(string path)
    {
        if (Pixels is null) throw new HarnessException("frame pixels were not kept");
        Png.Write(path, Pixels, Width, Height, BitsPerPixel, RedMask, GreenMask, BlueMask, BytesPerLine);
    }
}

internal sealed class HarnessException(string message) : Exception(message);
