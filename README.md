# HC2000 emulator

**English** · [Română](README.ro.md)

An emulator of the **ICE Felix HC2000**, the Romanian home computer of the 1990s, written from scratch in C# on .NET 10.
The HC2000 is ZX Spectrum compatible and has a built-in 3.5" floppy drive (Intel 8272 controller). It runs CP/M 2.2 on
its graphic screen, at 64 x 24 characters with a 4x8 font.

The goal is a faithful machine: the original ROMs, BIOS and CP/M software run unmodified, the way the real hardware runs
them. The emulator comes with a desktop app for Windows and Linux and a set of Z80 tools.

## Install

### Windows 10 / 11 (x64)

Open **PowerShell** and run:

```powershell
irm https://raw.githubusercontent.com/mteletin/hc2026/main/install.ps1 | iex
```

The script downloads the latest `setup.exe` from [Releases](https://github.com/mteletin/hc2026/releases), checks its
SHA-256 against the digest GitHub publishes for it, and installs it for your user. It needs no administrator rights.

- To install by hand, download `hc2000-emu-<version>-setup.exe` from Releases and run it. The installer is not signed, so
  Windows SmartScreen may say "Windows protected your PC": choose **More info**, then **Run anyway**.
- To install a given version: `$env:HC2000_VERSION = '0.1.118'` before the command.
- To see the setup wizard instead of a silent install: `$env:HC2000_WIZARD = '1'` before the command.

| what | where |
|---|---|
| program | `%LOCALAPPDATA%\Programs\HC2000` (you can choose another folder in the wizard) |
| shortcuts | Start menu and desktop: **HC2000 Emu** |
| your data: settings, ROMs, disk, tape and snapshot images | `%APPDATA%\hc2000` |
| uninstall | Settings > Apps > **HC2000 Emu** |

### Ubuntu (x64)

```bash
curl -fsSL https://raw.githubusercontent.com/mteletin/hc2026/main/install.sh | bash
```

The script downloads the latest `linux-x64.run` from Releases, checks its SHA-256 against the digest GitHub publishes for
it, and runs it. It needs no `sudo`: everything goes under your home folder. .NET and SDL2 are bundled. The app also needs
`libfontconfig1` from the system: Ubuntu Desktop has it; on a minimal or server install, run
`sudo apt install libfontconfig1` first.

- Options go to the installer: `... | bash -s -- --prefix ~/apps/hc2000` installs into another folder, and
  `... | bash -s -- --uninstall` removes the program.
- To install a given version: `... | HC2000_VERSION=0.1.118 bash`.
- To install by hand, download `hc2000-emu-<version>-linux-x64.run` from Releases and run
  `bash hc2000-emu-<version>-linux-x64.run`.

| what | where |
|---|---|
| program | `~/.local/opt/hc2000` |
| launcher | `hc2000` (in `~/.local/bin`), and **HC2000** in the applications menu |
| your data: settings, ROMs, disk, tape and snapshot images | `~/.config/hc2000` |

If the `hc2000` command is not found right after the first install, log out and back in (Ubuntu adds `~/.local/bin` to
your `PATH` at login), or run `~/.local/bin/hc2000`.

### Upgrades and your data

Running either installer again upgrades the program in place. Your settings, ROMs and disk, tape and snapshot images are
never changed or deleted, not even by an uninstall. ROMs and the CP/M system disk `CPM22-HC.DSK` are copied into your
data folder only when they are not already there.

## Using the emulator

- **Machines:** create one or more HC2000 machines (**New machine**), each with its own configuration and ROM set, and
  reset or run them again.
- **Media and disks:** floppy drives A: and B: (Extended CPC DSK images, `.DSK`), hard-disk images and tape.
- **Snapshots:** save and restore a machine's whole state (`.hcs`).
- **Keyboard and paste:** choose **Symbolic** (the character you type on your PC is the one the HC2000 gets) or
  **Positional** (the physical key position) mapping, and paste text into the machine.
- **Debugger:** disassembly with labels, breakpoints (including memory paging changes), a memory view by page, a floppy
  controller panel and execution-map recording.
- **Settings:** machine profile and ROM set, speed, display scale, sound, key map, and the folders for your disk, tape and
  snapshot images.

## Command-line tools

The `hc` tool works on HC2000 images and Z80 code. Run it from the source tree with
`dotnet run --project src/Hc2000.Tools -- <subcommand> ...`:

| subcommand | job |
|---|---|
| `asm` | Zilog-syntax Z80 assembler (reassembles the ROM disassemblies byte for byte) |
| `disasm` | tracing disassembler with control files and execution maps |
| `dsk` | DSK floppy images |
| `hdd` | hard-disk images |
| `fs` | CP/M and HC BASIC file systems inside images |
| `tap`, `tape` | tape images |
| `rom` | ROM checks |
| `paging` | memory paging |

## Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), and on Linux `libfontconfig1` for the app and
its tests.

```bash
git clone https://github.com/mteletin/hc2026.git
cd hc2026
dotnet build Hc2000.slnx
dotnet run --project src/Hc2000.App          # the desktop app
dotnet test Hc2000.slnx --filter "speed!=slow"   # the test suites, without the slow ones
dotnet test Hc2000.slnx --filter speed=slow      # the slow ones: ZEXALL, full boots
```

- Some tests need files this repository does not include (third-party test sets, the BASIC game disks), so a run from
  this repository shows skipped tests and can show failures in those tests.
- Sound uses SDL2 2.32.10, and the app checks for exactly that version. The installers bundle it. A source build runs
  without sound until you copy `deploy/emu/sdl2/SDL2.dll` (Windows) or `deploy/emu/sdl2/linux-x64/libSDL2-2.0.so.0`
  (Linux) into the app's output folder under `runtimes/win-x64/native/` or `runtimes/linux-x64/native/`.

## What is in this repository

| folder | contents |
|---|---|
| `src/Hc2000.Z80` | the Z80 CPU core, shared by everything |
| `src/Hc2000.Machine` | the HC2000 itself: memory and paging, video, keyboard, floppy controller, serial, Interface 1 |
| `src/Hc2000.App` | the Avalonia desktop app and its debugger |
| `src/Hc2000.Asm`, `src/Hc2000.Disasm` | the assembler and the tracing disassembler |
| `src/Hc2000.Tools` | the `hc` command-line tool |
| `bios/` | the disassembled HC2000 BIOS, which reassembles byte for byte |
| `roms/` | the HC2000 ROM images; see `roms/README.md` |
| `disks/cpm/` | CP/M 2.2 system disks; see `disks/README.md` |
| `tests/`, `tools/` | test suites and build tools |
| `deploy/emu/` | the Windows and Ubuntu installers |
| `docs/notes/` | technical notes |

Original manuals, schematics, BASIC game disks and third-party reference code are not published here.

## Versions

Each stable version is published here as a one-way release: a snapshot of the source, a `v<version>` tag and a GitHub
Release with the Windows and Ubuntu installers. Development happens in a separate repository, so pull requests here are
not merged. The current version is in [VERSION.md](VERSION.md).
