#!/usr/bin/env python3
"""Real X11 startup smoke: mapped HC2000 window, resize, graceful UI close.

Build first: dotnet build -c Release
Run: timeout 30 xvfb-run -a python3 scripts/test-app-linux-startup.py
Requires Python 3, libX11 and Xvfb; no DBus/IME disabling or headless Avalonia.
"""
import ctypes as c
from pathlib import Path
import subprocess
import sys
import time

x11 = c.CDLL("libX11.so.6")
Window = c.c_ulong
Display = c.c_void_p


def bind(name, result, *args):
    fn = getattr(x11, name)
    fn.restype = result
    fn.argtypes = args
    return fn


open_display = bind("XOpenDisplay", Display, c.c_char_p)
root_window = bind("XDefaultRootWindow", Window, Display)
query_tree = bind("XQueryTree", c.c_int, Display, Window, c.POINTER(Window),
                  c.POINTER(Window), c.POINTER(c.POINTER(Window)), c.POINTER(c.c_uint))
fetch_name = bind("XFetchName", c.c_int, Display, Window, c.POINTER(c.c_void_p))
free = bind("XFree", c.c_int, c.c_void_p)
geometry = bind("XGetGeometry", c.c_int, Display, Window, c.POINTER(Window),
                c.POINTER(c.c_int), c.POINTER(c.c_int), c.POINTER(c.c_uint),
                c.POINTER(c.c_uint), c.POINTER(c.c_uint), c.POINTER(c.c_uint))
resize = bind("XResizeWindow", c.c_int, Display, Window, c.c_uint, c.c_uint)
flush = bind("XFlush", c.c_int, Display)
intern = bind("XInternAtom", c.c_ulong, Display, c.c_char_p, c.c_int)
send = bind("XSendEvent", c.c_int, Display, Window, c.c_int, c.c_long, c.c_void_p)
close_display = bind("XCloseDisplay", c.c_int, Display)
get_image = bind("XGetImage", c.c_void_p, Display, Window, c.c_int, c.c_int,
                 c.c_uint, c.c_uint, c.c_ulong, c.c_int)
get_pixel = bind("XGetPixel", c.c_ulong, c.c_void_p, c.c_int, c.c_int)
destroy_image = bind("XDestroyImage", c.c_int, c.c_void_p)


class Attributes(c.Structure):
    _fields_ = [("x", c.c_int), ("y", c.c_int), ("width", c.c_int),
                ("height", c.c_int), ("border_width", c.c_int), ("depth", c.c_int),
                ("visual", c.c_void_p), ("root", Window), ("window_class", c.c_int),
                ("bit_gravity", c.c_int), ("win_gravity", c.c_int),
                ("backing_store", c.c_int), ("backing_planes", c.c_ulong),
                ("backing_pixel", c.c_ulong), ("save_under", c.c_int),
                ("colormap", c.c_ulong), ("map_installed", c.c_int),
                ("map_state", c.c_int), ("all_event_masks", c.c_long),
                ("your_event_mask", c.c_long), ("do_not_propagate_mask", c.c_long),
                ("override_redirect", c.c_int), ("screen", c.c_void_p)]


attributes = bind("XGetWindowAttributes", c.c_int, Display, Window, c.POINTER(Attributes))


def viewable(display, window):
    attrs = Attributes()
    return attributes(display, window, c.byref(attrs)) and attrs.map_state == 2


class ClientMessage(c.Structure):
    _fields_ = [("type", c.c_int), ("serial", c.c_ulong), ("send_event", c.c_int),
                ("display", Display), ("window", Window), ("message_type", c.c_ulong),
                ("format", c.c_int), ("data", c.c_long * 5)]


class Event(c.Union):
    _fields_ = [("client", ClientMessage), ("pad", c.c_long * 24)]


def windows(display, parent):
    root, ancestor = Window(), Window()
    children = c.POINTER(Window)()
    count = c.c_uint()
    if not query_tree(display, parent, c.byref(root), c.byref(ancestor),
                      c.byref(children), c.byref(count)):
        return []
    result = list(children[:count.value])
    if children:
        free(children)
    return result


def title(display, window):
    name = c.c_void_p()
    if not fetch_name(display, window, c.byref(name)) or not name.value:
        return ""
    try:
        return c.string_at(name).decode("utf-8", errors="replace")
    finally:
        free(name)


def size(display, window):
    root = Window()
    x, y = c.c_int(), c.c_int()
    width, height, border, depth = (c.c_uint() for _ in range(4))
    if not geometry(display, window, c.byref(root), c.byref(x), c.byref(y),
                    c.byref(width), c.byref(height), c.byref(border), c.byref(depth)):
        return (0, 0)
    return width.value, height.value


def main():
    display = open_display(None)
    if not display:
        raise RuntimeError("No X11 display; run under xvfb-run -a")
    dll = Path(__file__).resolve().parents[1] / "src/Hc2000.App/bin/Release/net10.0/Hc2000.App.dll"
    # Xvfb has no window manager to create these atoms. Avalonia reads existing
    # atoms at initialization, so provide the normal WM protocol before launch.
    intern(display, b"WM_PROTOCOLS", 0)
    intern(display, b"WM_DELETE_WINDOW", 0)
    flush(display)
    process = subprocess.Popen(["dotnet", str(dll), *sys.argv[1:]])
    try:
        deadline = time.monotonic() + 15
        window = None
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise RuntimeError(f"App exited before UI appeared: {process.returncode}")
            window = next((w for w in windows(display, root_window(display))
                           if title(display, w).startswith("HC2000") and viewable(display, w)), None)
            if window:
                break
            time.sleep(0.1)
        if not window:
            raise RuntimeError("HC2000 window did not appear within 15s")
        assert size(display, window) == (800, 750), size(display, window)
        # Sample the skeleton heading/body area: a mapped but blank window must
        # not count as a usable UI. No exact font/theme-dependent screenshot hash.
        time.sleep(1)
        image = get_image(display, window, 0, 0, 400, 150, c.c_ulong(-1).value, 2)
        if not image:
            raise RuntimeError("Could not capture the mapped UI")
        try:
            colors = {get_pixel(image, x, y) for x in range(0, 400, 2)
                      for y in range(0, 150, 2)}
            assert len(colors) > 2, f"UI did not render content: {len(colors)} colors"
        finally:
            destroy_image(image)
        resize(display, window, 640, 480)
        flush(display)
        time.sleep(0.5)
        assert size(display, window) == (640, 480), size(display, window)
        event = Event()
        event.client = ClientMessage(33, 0, 1, display, window,
                                     intern(display, b"WM_PROTOCOLS", 0), 32,
                                     (c.c_long * 5)(intern(display, b"WM_DELETE_WINDOW", 0), 0, 0, 0, 0))
        assert send(display, window, 0, 0, c.byref(event))
        flush(display)
        assert process.wait(timeout=5) == 0, process.returncode
        print(f"Linux UI smoke passed: mapped HC2000 800x750, rendered {len(colors)} colors, "
              "resized 640x480, UI close exit 0")
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=5)
        close_display(display)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"Linux UI smoke FAILED: {error}", file=sys.stderr)
        sys.exit(1)
