# Disk images

All 77 images come from `00-RoHC/DISKS/pack_dsk.zip`. Every one is in the **Extended CPC DSK** format (`EXTENDED CPC DSK File`), the same container Amstrad and +3 emulators use. Format references are in `references/formats/`.

- `cpm/`: 12 CP/M disks (`CPM*.DSK`, `CPM22-HC`, `CPMZ80`, `CPM-VERDE`, `CPM91*`).
- `basic/`: 65 HC BASIC-format disks, mostly game collections named after their owners.
- `catalog.txt`: the pack's own directory listing of every disk.

Image sizes, as a hint to the geometry. These are still to be confirmed from the track headers.

| bytes | count | likely |
|---|---|---|
| 696,576 | most `basic/` | 3.5", 80T x 2H x 16 x 256 = 640K, plus DSK headers |
| 348,416 | `ANDREI*`, `BASDEMO525` | 5.25" 40-track, or single-sided |
| 389,376 | `CPM1..7`, `CPM91*`, `CPM-VERDE` | CP/M format with different sectors per track |
| 778,496 | `CPM22-HC`, `CPMZ80` | larger CP/M format |

Byte-identical pairs (kept under both names): ALBA8 = CRISTIG2, BASIC10 = CRISTIG3, BASIC2 = CRISTIG4, CPM1 = CPM91, CRISTIG1 = CRISTIORANGE1.

Not here yet: your own `cpm_1998/disk1` (HC2000 CP/M 56K (C) 1998) from github.com/mteletin/hc2000.
