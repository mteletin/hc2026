#!/usr/bin/env python3
"""Real X11 settings Save/reopen/restart smoke (explicit desktop test tier).

Build Release first; run: timeout 300 xvfb-run -a python3 scripts/test-app-settings-ui.py
Requires libX11, libXtst, Xvfb and a tesseract 5 build with eng.traineddata.
HC2000_XTST_LIBRARY overrides the XTest library location; HC2000_TESSERACT
overrides the self-contained tesseract prefix (default ~/opt/tess/root);
HC2000_UI_ARTIFACTS retains PNGs. Never uses the user's real config.
No headless Avalonia, reflection, direct handler invocation or config pre-seeding.

Main-window controls are resolved by their rendered label, not a fixed pixel:
Avalonia 11.3.2 ships no X11/AT-SPI accessibility backend (it landed in 12.0),
so the Settings... button is located with OCR (tesseract --psm 11) of the
rendered main window and the click lands on the word box; a role check (the
button sits directly above Debugger in the left column) rejects off-label
candidates. The test also deliberately shifts the main-window layout (wheel
scroll) before a second open and asserts the resolved point tracks the shift
where the stock fixed (65,100) pixel clicks a different control, and that the
bottom-scroll position resolves identically on reopen and after a process
restart. The settings dialog itself keeps its stable y rows inside the known
560x700 window.
"""
import ctypes as c
import importlib.util
import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile
import time
import zlib

spec = importlib.util.spec_from_file_location("startup", Path(__file__).with_name("test-app-linux-startup.py"))
x = importlib.util.module_from_spec(spec)
spec.loader.exec_module(x)
xt = c.CDLL(os.environ.get("HC2000_XTST_LIBRARY", "libXtst.so.6"))
xt.XTestFakeButtonEvent.argtypes = [c.c_void_p, c.c_uint, c.c_int, c.c_ulong]
xt.XTestFakeKeyEvent.argtypes = [c.c_void_p, c.c_uint, c.c_int, c.c_ulong]
keycode = x.bind("XKeysymToKeycode", c.c_uint, c.c_void_p, c.c_ulong)
keysym = x.bind("XStringToKeysym", c.c_ulong, c.c_char_p)
focus = x.bind("XSetInputFocus", c.c_int, c.c_void_p, c.c_ulong, c.c_int, c.c_ulong)
warp = x.bind("XWarpPointer", c.c_int, c.c_void_p, c.c_ulong, c.c_ulong,
              c.c_int, c.c_int, c.c_uint, c.c_uint, c.c_int, c.c_int)
tess_prefix = Path(os.environ.get("HC2000_TESSERACT", str(Path.home() / "opt/tess/root")))


def main():
    display = x.open_display(None)
    if not display:
        raise RuntimeError("Run under xvfb-run -a")
    process = None
    dll = Path(__file__).resolve().parents[1] / "src/Hc2000.App/bin/Release/net10.0/Hc2000.App.dll"
    artifacts = os.environ.get("HC2000_UI_ARTIFACTS")
    if artifacts:
        Path(artifacts).mkdir(parents=True, exist_ok=True)

    def find(title):
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise RuntimeError(f"App exited: {process.returncode}")
            window = next((w for w in x.windows(display, x.root_window(display))
                           if x.title(display, w) == title and x.viewable(display, w)), None)
            if window:
                return window
            time.sleep(.1)
        raise RuntimeError(f"Missing mapped window: {title}")

    def click(window, a, b):
        focus(display, window, 1, 0)
        warp(display, 0, window, 0, 0, 0, 0, a, b)
        x.flush(display)
        time.sleep(.1)
        for down in (1, 0):
            assert xt.XTestFakeButtonEvent(display, 1, down, 0)
            x.flush(display)
            time.sleep(.05)
        time.sleep(.2)

    def key(name, modifier=None):
        code = keycode(display, keysym(name.encode()))
        assert code, name
        mod = keycode(display, keysym(modifier)) if modifier else 0
        if mod:
            assert xt.XTestFakeKeyEvent(display, mod, 1, 0)
        assert xt.XTestFakeKeyEvent(display, code, 1, 0)
        assert xt.XTestFakeKeyEvent(display, code, 0, 0)
        if mod:
            assert xt.XTestFakeKeyEvent(display, mod, 0, 0)
        x.flush(display)
        time.sleep(.04)

    def replace(window, y, text):
        click(window, 200, y)
        key("a", b"Control_L")
        for ch in text:
            name = {"/": "slash", "-": "minus", "{": "braceleft", "}": "braceright",
                    '"': "quotedbl", ":": "colon"}.get(ch, ch.lower())
            key(name, b"Shift_L" if ch.isupper() or ch in '{}":' else None)

    def close(window):
        event = x.Event()
        event.client = x.ClientMessage(33, 0, 1, display, window,
            x.intern(display, b"WM_PROTOCOLS", 0), 32,
            (c.c_long * 5)(x.intern(display, b"WM_DELETE_WINDOW", 0), 0, 0, 0, 0))
        assert x.send(display, window, 0, 0, c.byref(event))
        x.flush(display)
        time.sleep(.3)

    def capture(window, name):
        time.sleep(.5)
        width, height = x.size(display, window)
        assert (width, height) == (560, 700), (width, height)
        image = x.get_image(display, window, 0, 0, width, height, c.c_ulong(-1).value, 2)
        assert image
        data = bytearray()
        try:
            for y in range(height):
                data.append(0)  # PNG scanline filter
                for a in range(width):
                    pixel = x.get_pixel(image, a, y)
                    data.extend(((pixel >> 16) & 255, (pixel >> 8) & 255, pixel & 255))
        finally:
            x.destroy_image(image)
        if artifacts:
            def chunk(tag, body):
                return struct.pack("!I", len(body)) + tag + body + struct.pack("!I", zlib.crc32(tag + body) & 0xffffffff)
            Path(artifacts, name + ".png").write_bytes(b"\x89PNG\r\n\x1a\n" +
                chunk(b"IHDR", struct.pack("!IIBBBBB", width, height, 8, 2, 0, 0, 0)) +
                chunk(b"IDAT", zlib.compress(data)) + chunk(b"IEND", b""))
        # First five field rows, excludes reformatted JSON and changing status text.
        fields = bytes(data[82 * (1 + 3 * width):375 * (1 + 3 * width)])
        return bytes(data), fields

    def scroll(window, notches, down=True):
        # Proven wheel pattern: warp to h//3 without pre-focusing, flush per notch.
        w, h = x.size(display, window)
        warp(display, 0, window, 0, 0, 0, 0, w // 2, h // 3)
        x.flush(display)
        time.sleep(.1)
        button = 5 if down else 4
        for _ in range(notches):
            assert xt.XTestFakeButtonEvent(display, button, 1, 0)
            assert xt.XTestFakeButtonEvent(display, button, 0, 0)
            x.flush(display)
            time.sleep(.05)
        time.sleep(.3)

    def ocr_words(window):
        """Rendered labels of the window as (text, center_x, center_y, conf),
        in window pixels. Binarizes the capture, upscales 2x and runs
        tesseract --psm 11; only words with conf >= 80 are returned."""
        w, h = x.size(display, window)
        image = x.get_image(display, window, 0, 0, w, h, c.c_ulong(-1).value, 2)
        assert image
        half = bytearray(w * h)
        try:
            for y in range(h):
                d = y * w
                for a in range(w):
                    p = x.get_pixel(image, a, y)
                    lum = ((p >> 16) & 255) + ((p >> 8) & 255) + (p & 255)
                    half[d + a] = 0 if lum < 600 else 255
        finally:
            x.destroy_image(image)
        big = bytearray(w * 2 * h * 2)  # 2x nearest-neighbour upscale improves small-text OCR
        for y in range(h):
            for t in (0, 1):
                o = (y * 2 + t) * w * 2
                s = y * w
                for a in range(w):
                    v = half[s + a]
                    big[o + 2 * a] = v
                    big[o + 2 * a + 1] = v
        fd, path = tempfile.mkstemp(suffix=".pgm")
        with os.fdopen(fd, "wb") as f:
            f.write(b"P5 %d %d 255\n" % (w * 2, h * 2) + bytes(big))
        try:
            out = subprocess.run([str(tess_prefix / "usr/bin/tesseract"), path, "-", "--psm", "11", "tsv"],
                                 env=dict(os.environ,
                                          TESSDATA_PREFIX=str(tess_prefix / "usr/share/tesseract-ocr/5/tessdata"),
                                          LD_LIBRARY_PATH=str(tess_prefix / "usr/lib/x86_64-linux-gnu")),
                                 capture_output=True, text=True, timeout=60)
        finally:
            os.unlink(path)
        assert out.returncode == 0, out.stderr[-500:]
        words = []
        for line in out.stdout.splitlines()[1:]:
            cols = line.split("\t")
            if len(cols) < 12 or not cols[11].strip():
                continue
            try:
                conf = float(cols[10])
            except ValueError:
                continue
            if conf < 80:
                continue
            left, top, width, height = int(cols[6]), int(cols[7]), int(cols[8]), int(cols[9])
            words.append((cols[11].strip(), (left + width / 2) / 2, (top + height / 2) / 2, conf))
        return words

    def find_settings_button(window):
        """Resolve the Settings... button by rendered label; None if absent.
        Label check: a 'settings' word in the left column; role check: it must
        sit directly above the 'debugger' word in that column (the button row),
        which rejects any off-label body text."""
        def norm(text):
            return text.lower().replace(".", "").replace("\u2026", "").replace(" ", "")

        words = ocr_words(window)
        debuggers = [e for e in words if norm(e[0]) == "debugger" and e[1] < 150]
        for cand in (e for e in words if norm(e[0]) in ("settings", "setting") and e[1] < 150):
            if any(abs(d[1] - cand[1]) < 30 and 20 < d[2] - cand[2] < 70 for d in debuggers):
                return int(round(cand[1])), int(round(cand[2]))
        return None

    def open_settings(window):
        """Click the main window's Settings... button, resolved from its
        rendered label; returns the clicked (x, y). Scans the current
        viewport first so a deliberately shifted layout is tracked, then
        falls back to the bottom-scroll position where the button row renders,
        then nearby positions; fails loudly rather than click a guess."""
        for attempt in range(4):
            if attempt == 1:
                scroll(window, 40, down=True)
            elif attempt > 1:
                scroll(window, 3, down=False)
            point = find_settings_button(window)
            if point:
                w, h = x.size(display, window)
                assert 0 < point[0] < w and 0 < point[1] < h, point
                click(window, point[0], point[1])
                return point
        raise RuntimeError("Settings... button label not resolved in rendered main window")

    try:
        for atom in (b"WM_PROTOCOLS", b"WM_DELETE_WINDOW"):
            x.intern(display, atom, 0)
        x.flush(display)
        with tempfile.TemporaryDirectory(prefix="hc2000-settings-ui-") as config:
            env = dict(os.environ, XDG_CONFIG_HOME=config)
            process = subprocess.Popen(["dotnet", str(dll)], env=env)
            main_window = find("HC2000")
            p1 = open_settings(main_window)
            assert p1 != (65, 100), f"layout shifted back to the legacy fixed pixel: {p1}"
            settings = find("HC2000 settings")
            original, _ = capture(settings, "defaults")
            for y, value in ((100, "hc2000-v2"), (165, "roms/hc2000/eprom-dump-hc2k-2"), (225, "400"),
                             (295, "3"), (360, "offline"), (425, '{"F1":"SPACE"}')):
                replace(settings, y, value)
            click(settings, 45, 555)  # real Save button -> handler -> store
            saved, saved_fields = capture(settings, "saved")
            assert original != saved
            config_file = Path(config, "hc2000/settings.json")
            expected = {"profile": "hc2000-v2", "romSet": "roms/hc2000/eprom-dump-hc2k-2", "speedPercent": 400,
                        "scale": 3, "audioPreference": "offline", "keyMap": {"F1": "SPACE"}}
            assert json.loads(config_file.read_text()) == expected
            persisted = config_file.read_bytes()
            click(settings, 185, 555)  # close/discard button
            # Deliberate layout shift: scroll the main window up so the button
            # renders lower; label-based resolution must track the shift where
            # a fixed pixel would click a different control.
            scroll(main_window, 3, down=False)
            p2 = open_settings(main_window)
            assert p2 != p1, f"resolution did not track the deliberate layout shift: {p1} {p2}"
            settings = find("HC2000 settings")
            reopened, reopened_fields = capture(settings, "reopened")
            assert saved_fields == reopened_fields, "Saved fields did not reload into UI"
            close(settings)
            close(main_window)
            assert process.wait(timeout=5) == 0
            process = subprocess.Popen(["dotnet", str(dll)], env=env)
            main_window = find("HC2000")
            p3 = open_settings(main_window)
            assert p3 == p1, f"bottom-scroll layout not stable across restart: {p1} {p3}"
            settings = find("HC2000 settings")
            restarted, _ = capture(settings, "restarted")
            assert reopened == restarted, "UI changed across process restart"
            assert config_file.read_bytes() == persisted
            replace(settings, 225, "300")  # invalid speed
            click(settings, 45, 555)
            capture(settings, "invalid")
            assert config_file.read_bytes() == persisted, "Invalid Save changed config"
            click(settings, 185, 555)
            p4 = open_settings(main_window)
            assert p4 == p1, f"bottom-scroll layout not stable across reopen: {p1} {p4}"
            settings = find("HC2000 settings")
            discarded, _ = capture(settings, "discarded")
            assert discarded == reopened, "Discard did not restore saved UI values"
            close(settings)
            close(main_window)
            assert process.wait(timeout=5) == 0
            print("Settings UI PASS: all six fields Save -> JSON -> reopen -> process restart; "
                  "field pixels match, invalid Save preserves JSON, discard restores UI, both exits 0")
    finally:
        if process and process.poll() is None:
            process.kill()
            process.wait(timeout=5)
        x.close_display(display)


if __name__ == "__main__":
    main()