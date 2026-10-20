# ROMs

Every file's sha256 is in `/MANIFEST.sha256`. Duplicates in the original dump were collapsed to one file.

## HC2000 (`hc2000/`)

The machine has three ROM images: BASIC (16 KB), CP/M boot (8 KB used), and IF1 with the disk commands (16 KB).
Two generations exist in the dump.

| file | size | origin in the dump | notes |
|---|---|---|---|
| `v1/basic.rom` | 16K | `hc2000.v1` = `HC2K_OLD/basic_old.rom` = `HC2K-ALL/HC2K1-0.ROM` | 98% the same as v2 |
| `v1/cpm.rom` | 16K | `HC2K_OLD/cpm.rom` = `HC2K-ALL/HC2K1-1.ROM` | `HC2K-ALL/HC2K1.ROM` (32K) = `v1/basic.rom` + this file |
| `v1/if1.rom` | 16K | `HC2K-ALL/HC2KI0.ROM` | |
| `v1/if1_old-10k.rom` | 10K | `HC2K_OLD/if1_old.rom` | 97% the same as the first 10K of `v1/if1.rom`; probably a partial or earlier dump |
| `v2/basic.rom` | 16K | `hc2000.v2` = `HC2kROMs/BAS` = `HC2K-ALL/HC2K2-0.ROM` | |
| `v2/cpm.rom` | 8K | `CPM.rom` = `HC2kROMs/CPM` = `HC2K-ALL/HC2K2-1.ROM` | **bad dump, do not use**: a leading `00`, then `cpm-hc2kzip.bin[0..8190]` (shifted by one byte, the last byte `55` lost; checked 2026-10-02) |
| `v2/cpm-hc2kzip.bin` | 8K | `00-RoHC/ROM/hc2k.zip: hc2k/v2/CPM.BIN` | **differs** from `v2/cpm.rom`; it is the one found in the EPROM dump below |
| `v2/if1.rom` | 16K | `IF1.rom` = `HC2kROMs/IF1` = `HC2K-ALL/HC2KI1.ROM` | |
| `eprom-dump-hc2k-2/M27C512.bin` | 64K | `HC2K_#2/M27C512@DIP28.BIN` | a real 27C512 read from a board: see the map below |

Map of the 27C512 dump, by 8K slice, by byte comparison:

| offset | matches |
|---|---|
| 0x0000-0x3FFF | `if1_old` (100% over its 10K) |
| 0x4000-0x7FFF | `v1/if1.rom` (99.99%) |
| 0x8000-0xBFFF | `v2/basic.rom` (exact) |
| 0xC000-0xDFFF | close to the second half of `v1/cpm.rom` (84%) |
| 0xE000-0xFFFF | `v2/cpm-hc2kzip.bin` (exact) |

**Open questions** (answer from the schematics and the manual):
- Which ROM set is on which chip on a real board?
- How do the address lines select the slices? HC Ports says port 0x7E b0 drives EPROM A14 (BASIC/CP/M), and b1 maps it at 0000-3FFF or E000-FFFF.
- Which pair of `v2` CP/M images was released?

## Other HC models (`other-hc/`)

These are reference material, not targets:
- HC85, HC90 and HC91.
- HC88 boot (2K).
- HC128: two different dumps, `hc128-a` and `hc128-b0/b1`, only about 1% the same.
- Sintez 48 and Sintez-M.

## ZX Spectrum 128 (`zx-spectrum/`)

Sinclair 128 ROMs: `zx128_0/1` and the Spanish `zx128s0/s1`. They came inside `hc2000.zip`.
