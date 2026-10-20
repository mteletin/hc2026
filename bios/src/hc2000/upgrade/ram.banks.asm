; Clearly labelled reconstruction of the CP/M 2.2 disk BIOS for the virtual
; hc2000-v2-ram-banks-v1 profile. This is newly authored, not recovered firmware.
; Input baseline: the supplied CPM22-HC.DSK BIOS image D300-DAFF, extracted by
; the reproducible UpgradeBiosBuilder from sectors 0/3,5,7,9,2,4,6,8 and
; 1/1,3,5,7,9,2,4. Baseline BIOS SHA256:
; ad2bdb9053de9ec5fb971e7b660c3e07ace1fee8382cfa8f1c4f4610e48bcea4
;
; The overlays retain the CP/M BIOS entry-table locations and supplied A:/B:
; paths. Drive M: is CP/M drive number 12, a 1-MiB, 128-bank RAM disk. Each
; 128-record BIOS request maps one CP/M sector to one 128-byte slice of the
; selected 8-KiB bank. A labelled test-only guest INITM.COM initializes M's
; directory through BIOS WRITE; supplied PIP.COM exercises reads/writes. The
; supplied FORMAT.COM rejects M:, and this reconstruction is opt-in/test-only.
;
; Pinned contract: hc2000-ram-banks-abi-v2. Port 8B selects 00..7F and FF
; restores base memory; A000-BFFF is the 8-KiB window. Port 8D is not used.
;
; The original BIOS keeps its D681-D683 status shim (RET NC; SCF; RET). The
; remainder of its diagnostic/error table at D684-D7B6 is the bounded overlay
; area used by this reconstruction; A:/B: normal I/O code and buffers are kept.

ORG 0xD492
    JP MSelectDispatch
ORG 0xD53E
    JP MWriteDispatch
ORG 0xD542
    JP MReadDispatch

ORG 0xD684
MSelectDispatch:
    LD A,C
    CP 0x0C
    JP Z,MDriveSelect
    CP 0x02
    JP NC,MDriveInvalid
    JP 0xD499 ; continue the supplied A:/B: selector after its range check
MDriveSelect:
    LD A,0x0C
    LD (0xD871),A
    LD HL,MDPH
    RET
MDriveInvalid:
    LD HL,0x0000
    RET

MWriteDispatch:
    LD A,(0xD871)
    CP 0x0C
    JP Z,MWrite
    LD A,0x01
    JP 0xD545 ; supplied floppy writer, retaining C's CP/M write type
MReadDispatch:
    LD A,(0xD871)
    CP 0x0C
    JP Z,MRead
    XOR A
    LD C,0x00
    JP 0xD545 ; supplied floppy reader

MRead:
    LD A,(0xD872) ; track == bank index (one 64-sector track per bank)
    CP 0x80
    JR NC,MTransferError
    OUT (0x8B),A
    LD A,(0xD873) ; sector 0..63, each is 128 bytes
    CP 0x40
    JR NC,MReadErrorRestore
    LD L,A
    LD H,0x00
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    LD DE,0xA000
    ADD HL,DE
    LD DE,(0xD875) ; CP/M DMA destination
    LD BC,0x0080
    LDIR
    LD A,0xFF
    OUT (0x8B),A
    XOR A
    RET
MReadErrorRestore:
    LD A,0xFF
    OUT (0x8B),A
    JR MTransferError

MWrite:
    LD A,(0xD872)
    CP 0x80
    JR NC,MTransferError
    OUT (0x8B),A
    LD A,(0xD873)
    CP 0x40
    JR NC,MWriteErrorRestore
    LD L,A
    LD H,0x00
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    LD DE,0xA000
    ADD HL,DE
    EX DE,HL
    LD HL,(0xD875) ; CP/M DMA source
    LD BC,0x0080
    LDIR
    LD A,0xFF
    OUT (0x8B),A
    XOR A
    RET
MWriteErrorRestore:
    LD A,0xFF
    OUT (0x8B),A
MTransferError:
    LD A,0x01
    RET

; The D681-D683 RET NC / SCF / RET status shim is retained byte-for-byte.
; Standard 16-byte CP/M DPH shares the supplied BIOS directory buffer at D895.
; MDPB uses 4-KiB blocks (DSM=255), so all 128 banks remain usable with a 32-byte ALV.
MDPH:
    DW 0x0000,0x0000,0x0000,0x0000,0xD895,MDPB,0x0000,MAlloc
MDPB:
    DW 64
    DB 5,31,3
    DW 255,255
    DB 0xC0,0x00
    DW 0,0
MAlloc:
    DB 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0
    DB 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0
