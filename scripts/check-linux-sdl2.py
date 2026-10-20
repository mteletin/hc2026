#!/usr/bin/env python3
"""Check the actual archive/install native payload; no audio device is opened."""
import ctypes
import hashlib
import pathlib
import subprocess
import sys

native = pathlib.Path(sys.argv[1]).resolve() / "runtimes/linux-x64/native"
subprocess.run(["sha256sum", "--strict", "-c", "SDL2-MANIFEST.sha256"], cwd=native, check=True)
provenance = dict(line.split("=", 1) for line in (native / "SDL2-provenance.txt").read_text().splitlines())
assert provenance["version"] == "2.32.10"
assert provenance["source_commit"] == "5d249570393f7a37e037abf22cd6012a4cc56a71"
assert provenance["source_sha256"] == "5f5993c530f084535c65a6879e9b26ad441169b3e25d789d83287040a9ca5165"
assert provenance["rid"] == "linux-x64" and provenance["compiler"]
assert "Sam Lantinga" in (native / "SDL2-LICENSE.txt").read_text()
configuration = (native / "SDL2-config.h").read_text()
assert any(f"#define SDL_AUDIO_DRIVER_{driver} 1" in configuration for driver in ("ALSA", "PULSEAUDIO")), "no desktop audio backend"
elf = (native / "libSDL2-2.0.so.0").read_bytes()
assert elf[:5] == b"\x7fELF\x02" and elf[18:20] == b"\x3e\x00", "not ELF64 x86-64"
assert hashlib.sha256(elf).hexdigest() == "7444ff99ff7af2f6e3cc86155c524cd7237490a58a8f5664b784458b4ff5bd91", "not the pinned prebuilt library"
# The old build sysroot in provenance is history, not a runtime prerequisite.
# No binutils/compiler needed: inspect loader dependencies and then load the ABI.
dependencies = subprocess.run(["ldd", str(native / "libSDL2-2.0.so.0")], check=True, capture_output=True, text=True, timeout=10).stdout
assert "not found" not in dependencies, dependencies
assert not any(name in dependencies for name in ("libasound", "libpulse", "/home/")), dependencies
print("Loader dependencies (optional ALSA/Pulse are not mandatory):\n" + dependencies, flush=True)
lib = ctypes.CDLL(str(native / "libSDL2-2.0.so.0"))
version = (ctypes.c_uint8 * 3)()
lib.SDL_GetVersion.argtypes = [ctypes.POINTER(ctypes.c_uint8)]
lib.SDL_GetVersion(version)
assert tuple(version) == (2, 32, 10), tuple(version)
for symbol in ("SDL_OpenAudioDevice", "SDL_QueueAudio", "SDL_GetQueuedAudioSize", "SDL_ClearQueuedAudio", "SDL_PauseAudioDevice", "SDL_CloseAudioDevice"):
    getattr(lib, symbol)
print("Native execution: SDL_GetVersion=2.32.10; ELF64 x86-64; queued-audio exports resolved; no audio device/output claim", flush=True)
