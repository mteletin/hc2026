# HC2000 emulator

**English** · [Română](README.ro.md)

An emulator of the **ICE Felix HC2000**, the Romanian home computer of the 1990s, written from scratch in C# on .NET 10.
The HC2000 is ZX Spectrum compatible and has a built-in 3.5" floppy drive (Intel 8272 controller). It runs CP/M 2.2 on
its graphic screen, at 64 x 24 characters with a 4x8 font.

The goal is a faithful machine: the original ROMs, BIOS and CP/M software run unmodified, the way the real hardware runs
them. On top of that, an **extended HC2000** you can switch on piece by piece adds what the real machine never had: hard
drives, megabytes of paged RAM, a VGA card as a second screen, sound, extra Z80s for CP/M programs to use, networking and
an SSH terminal. The emulator comes with a desktop app for Windows and Linux, the `hc` command-line tools for disk, tape
and ROM images, a Z80 assembler and disassembler, and the HC2000's software as readable source.

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
- On Windows, native disk-image admission requires a fixed local NTFS drive. Ordinary inherited image permissions are supported; network paths and reparse-point paths are refused.
- A Defender malware detection is separate from a SmartScreen publisher warning. Report the exact installer version and threat name for investigation; do not disable antivirus protection.
- To install a given version: `$env:HC2000_VERSION = '0.1.118'` before the command.
- To see the setup wizard instead of a silent install: `$env:HC2000_WIZARD = '1'` before the command.

| what | where |
|---|---|
| program | `%LOCALAPPDATA%\Programs\HC2000` (you can choose another folder in the wizard) |
| shortcuts | Start menu and desktop: **HC2000 Emu** |
| command-line tools | `hc`, added to your user `PATH` |
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
| command-line tools | `hc` (in `~/.local/bin`) |
| your data: settings, ROMs, disk, tape and snapshot images | `~/.config/hc2000` |

If the `hc2000` command is not found right after the first install, log out and back in (Ubuntu adds `~/.local/bin` to
your `PATH` at login), or run `~/.local/bin/hc2000`.

### Upgrades and your data

Running either installer again upgrades the program in place. Your settings, ROMs and disk, tape and snapshot images are
never changed or deleted, not even by an uninstall. ROMs and the CP/M system disk `CPM22-HC.DSK` are copied into your
data folder only when they are not already there.

## Using the emulator

- **Machines:** create one or more HC2000 machines (**New machine**), each in its own tab with its own configuration and
  ROM set, and reset or run them again.
- **Media and disks:** floppy drives A: and B: (Extended CPC DSK images, `.DSK`), hard-disk images and tape.
- **Media explorer:** browse your folders and your floppy, hard-disk and tape images as if they were folders, preview
  files, and copy files between an image and your PC.
- **Disk library:** a catalogue of your floppy and hard-disk images (name, format, contents, checksum), searchable,
  mounted in one action and swapped while the machine runs.
- **Tape deck:** a cassette recorder on the EAR/MIC line, with play, record, rewind and counter. It reads and writes
  TAP, TZX, PZX, CSW and WAV.
- **Snapshots:** save and restore a machine's whole state (`.hcs`), large paged memory and HDD contents included. Restoring a snapshot does not revive host-file write bindings.
- **Keyboard and paste:** choose **Symbolic** (the character you type on your PC is the one the HC2000 gets) or
  **Positional** (the physical key position) mapping, and paste text into the machine.
- **Finer text (optional):** a display option that draws BASIC's 32 columns and CP/M's 64 columns with a crisper font.
  The machine itself is unchanged; switch it off to see the original pixels.
- **Debugger:** disassembly with labels, breakpoints (including memory paging changes), a memory view by page, a floppy
  controller panel and execution-map recording.
- **Settings:** machine profile and ROM set, speed, display scale, sound, key map, and the folders for your disk, tape and
  snapshot images.

## The extended HC2000

New installations and new machine instances use the **full workstation** profile: CP/M boots from C:, with C:–F: hard-drive volumes, 1 GiB of paged RAM, the X2 VGA console, X3 sound, two auxiliary Z80 drivers and X6 networking with the gateway enabled. Existing saved machines keep their configurations.

Choose the **Classic** preset for the faithful original machine and its BASIC/floppy workflow. Under **Hardware** (or with the matching command-line options) you can choose other profiles and extensions. The full workstation reserves 1 GiB of guest RAM; Classic uses substantially less host memory. A hardware change starts a new machine.

| upgrade | what you get |
|---|---|
| hard drives | CP/M drives C: to F: on hard-disk images, installed with `HDINST` from the hard-drive CP/M floppy that ships with the emulator; `HDFMT` erases one volume |
| paged memory | 1 GiB of paged RAM in the full workstation, with smaller RAM profiles also available, and the **M:** RAM drive under CP/M (`MFMT` formats it, `RAMSTAT` shows its space) |
| tape as storage | a CP/M drive on a tape image, through the deck's control port (`TAPEINIT`), with commit and discard |
| faster CPU | 2x, 4x or 8x Z80 speed |
| X2 VGA | a VGA graphics and text display; the full workstation uses it as its main 640x480 CP/M screen. Under CP/M, `X2INST` makes it a device: `STAT CON:=UC1:` moves the console to it at 80 columns, `STAT CON:=CRT:` brings it back, and `LST:` can print to it as a second monitor |
| X3 sound, X4 math | a sound card (with a small MOD player, `X3MOD`) and a maths coprocessor |
| side Z80s | auxiliary Z80 units a CP/M program can hand work to (their number depends on the profile): `SWCYCLE` sends a job and checks the answer, and the wireframe-3D and map path-finding demos run their sums there |
| CP/NET | a network adapter with CP/NET for CP/M 2.2: `NETINST`, `NETLOGIN` and `NETMAP` map a server's drive to a local letter |
| X6 network and SSH | see below |

`SYSINFO` tells you, from CP/M, which machine you are on and what it has. `DRVSEL` lists which drives answer, and `SUM`
prints a file's size and checksum.

A ready **C: tools** hard-disk image comes with the emulator, with these commands and a manifest of what is on it, alongside
D:, E: and F: volumes.

### SSH from CP/M

With the X6 network card, CP/M can open an SSH session to a real computer and run a Linux terminal program on it: a
shell, Midnight Commander, editors. The encryption and the network stack run inside the emulator, on a side Z80 unit;
CP/M only sends and receives plain text through a simple API, and the terminal turns the remote VT100/xterm output into
HC2000 colours, cursor moves and full-screen mode.

1. Check the network gateway: the toggle in the status bar or **Ctrl+Shift+O**. New full-workstation instances enable it; existing saved machines keep their setting. The machine reaches the network through your PC, which acts as its router.
2. Set your default connection once in **Network · X6 gateway** (`user@host`, optionally a port and a key; passwords are
   never stored), or start the app with `--ssh-default user@host[:port][,key:name]`.
3. In CP/M type `SSH`. It starts the network driver itself and logs in. `SSH /M` shows the connection menu instead, and
   `SSH /H` uses the HC2000's own 64-column screen instead of the X2 VGA's 80 x 30.

`--network-gateway` switches the gateway on when the app starts.

## The `hc` command-line tools

The installers also put `hc` on your `PATH` (`~/.local/bin/hc` on Ubuntu, `<install folder>\hc` on Windows). Every
command has `--help`.

| command | what it does |
|---|---|
| `hc fs check` | check one CP/M disk image's file system and say what is wrong, without changing it |
| `hc fs survey` | the same over a whole folder of images: healthy, damaged, or cannot be checked |
| `hc fs locate` | find a CP/M file by name across a folder of images |
| `hc fs duplicates` | find identical files across images, even under different names or user areas |
| `hc fs variants` | find files that share a name but have different contents |
| `hc fs compare` | list the files that differ between two images |
| `hc fs export` / `hc fs restore` | export every file of a disk to a new host folder, with an inventory, and later rebuild a 40- or 80-track disk from it |
| `hc fs from-folder` | make a new CP/M data disk from a folder of files |
| `hc fs repack` | move a disk's files to a new 40- or 80-track disk |
| `hc dsk`, `hc hdd`, `hc tap`, `hc tape`, `hc rom` | create, inspect and change floppy, hard-disk, tape and ROM images |
| `hc asm`, `hc disasm` | the Z80 assembler (Zilog syntax, macros, `.bin`/Intel HEX, listing, symbols) and the tracing disassembler |
| `hc backup` | back up disk, tape and snapshot files to one archive with a manifest, verify it, and restore it |
| `hc settings` | preview and restore the default app settings, keeping a copy of the old ones |
| `hc support` | write a local support report (version, platform, configuration check) with nothing private in it |

None of the inspecting commands changes your images; the ones that write make new files.

## Included guest software

The repository contains the C# emulator, published BIOS and device examples, and compiled guest software.
Public builds use the guest programs and disk payloads in `build/guest-payload/` and verify their SHA-256 manifest
before assembling the workstation media.

## Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), and on Linux `libfontconfig1`.

```bash
git clone https://github.com/mteletin/hc2026.git
cd hc2026
dotnet build Hc2000.slnx
dotnet run --project src/Hc2000.App
```

Sound uses SDL2 2.32.10, and the app checks for exactly that version. The installers include it. For a build from source,
put SDL2 2.32.10 ([libsdl.org](https://www.libsdl.org/)) in the app's output folder under `runtimes/win-x64/native/`
(`SDL2.dll`) or `runtimes/linux-x64/native/` (`libSDL2-2.0.so.0`); without it the app runs without sound.
