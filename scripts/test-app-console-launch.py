#!/usr/bin/env python3
"""Cross-build launch contract: Windows GUI PE; Linux console/CLI unchanged.

Run from the repository root: timeout 240 python3 scripts/test-app-console-launch.py
No Windows runtime or database required. Windows console API policy is covered by
WindowsConsoleTests; running the Windows installer is an optional owner check.
"""
from pathlib import Path
import struct
import subprocess

app = Path("src/Hc2000.App/bin/Release/net10.0")
for rid in ("win-x64", "linux-x64"):
    subprocess.run(["dotnet", "build", "src/Hc2000.App", "-c", "Release",
                    "-r", rid, "--self-contained", "false", "--nologo"],
                   check=True, timeout=120)

# Check the actual native apphost, not just the managed DLL or an MSBuild property.
pe = (app / "win-x64/Hc2000.App.exe").read_bytes()
header = struct.unpack_from("<I", pe, 0x3c)[0]
assert pe[header:header + 4] == b"PE\0\0"
subsystem = struct.unpack_from("<H", pe, header + 24 + 68)[0]
assert subsystem == 2, f"Windows apphost subsystem: expected GUI (2), got {subsystem}"
print("PASS Windows apphost is GUI subsystem")

linux = app / "linux-x64/Hc2000.App"
assert linux.read_bytes()[:4] == b"\x7fELF"
for args, code, stdout, stderr in (
    (["--help"], 0, "--disk", ""),
    (["--unknown-option"], 1, "", "Unknown option:"),
    (["--cpu-turbo", "2"], 0, "multiplier=2", ""),
    (["--cpu-turbo", "invalid"], 2, "", "usage: hc2000 --cpu-turbo"),
):
    result = subprocess.run([str(linux.resolve()), *args], capture_output=True,
                            text=True, timeout=15)
    assert result.returncode == code, (args, result.returncode, result.stderr)
    for actual, expected in ((result.stdout, stdout), (result.stderr, stderr)):
        assert expected in actual if expected else not actual, (args, actual, expected)
    print(f"PASS Linux CLI {args}: exit={code}, expected stdout/stderr")
print("PASS console launch contract (5 checks)")
