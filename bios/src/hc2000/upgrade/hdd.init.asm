; Normal A/B-loaded COM installer; never patches media or ROM.
; WIP source artifact: HDD operations remain fail-closed until the resident
; census/deblock/switch implementation is qualified with the native receiver.
INCLUDE "hdd.layout.inc"
ORG 0x0100
HddInstall:
    ; Guard every word/opcode before copying/zeroing or exposing any hook.
    LD HL,(0x0006)
    LD DE,HddOriginalBdos
    OR A
    SBC HL,DE
    JP NZ,HddInstallRefused
    LD HL,HddPatchTable
HddPreflight:
    LD E,(HL)
    INC HL
    LD D,(HL)
    INC HL
    LD A,D
    OR E
    JR Z,HddPreflightDone
    LD A,(DE)
    CP (HL)
    JP NZ,HddInstallRefused
    INC HL
    INC DE
    LD A,(DE)
    CP (HL)
    JP NZ,HddInstallRefused
    INC HL
    INC HL
    INC HL
    JR HddPreflight
HddPreflightDone:
    LD A,(0x0005)
    CP 0xC3
    JP NZ,HddInstallRefused
    LD A,(0xC3F4)
    CP 0x11
    JP NZ,HddInstallRefused
    LD A,(0xC406)
    CP 0xCD
    JP NZ,HddInstallRefused
    LD A,(0xD466)
    CP 0xC3
    JP NZ,HddInstallRefused
    LD A,(0xD465)
    CP 0xFB
    JP NZ,HddInstallRefused
    LD HL,HddOpcodeSites
    LD B,8
HddOpcodeGuard:
    LD E,(HL)
    INC HL
    LD D,(HL)
    INC HL
    LD A,(DE)
    CP 0xC3
    JP NZ,HddInstallRefused
    DJNZ HddOpcodeGuard
    LD A,I
    PUSH AF
    DI
    LD HL,HddPayload
    LD DE,0x9000
    LD BC,HddPayloadEnd-HddPayload
    LDIR
    ; Zero only assigned data; code partitions never overlap these ranges.
    LD HL,0xA000
    LD DE,0xA001
    LD BC,0x14FF
    LD (HL),0
    LDIR
    LD A,0xA5
    LD (HddStack),A
    ; Nonzero stack/slack guards make accidental writes observable.
    LD HL,0xB800
    LD DE,0xB801
    LD BC,0x04FF
    LD (HL),0xA5
    LDIR
    LD HL,HddDphBytes
    LD DE,HddDph
    LD BC,0x50
    LDIR
    CALL HddFinalize
    OR A
    JR NZ,HddInstallCopiedRefused
    CALL HddApplyPatches
    POP AF
    JP PO,HddInstallNoEi
    EI
HddInstallNoEi:
    XOR A
    LD HL,0
    RET
HddInstallCopiedRefused:
    POP AF
    JP PO,HddInstallRefused
    EI
HddInstallRefused:
    LD A,1
    LD HL,0xFFFF
    RET
HddApplyPatches:
    LD HL,HddPatchTable
HddPatchNext:
    LD E,(HL)
    INC HL
    LD D,(HL)
    INC HL
    LD A,D
    OR E
    RET Z
    INC HL
    INC HL
    LD A,(HL)
    LD (DE),A
    INC DE
    INC HL
    LD A,(HL)
    LD (DE),A
    INC HL
    JR HddPatchNext
HddOpcodeSites:
    DW 0xD303,0xD318,0xD31B,0xD31E,0xD321,0xD327,0xD32A,0xD300
HddPatchTable:
    ; operand address, original target, resident target
    DW 0x0006,HddOriginalBdos,0x9006
    DW 0xC3F5,0xBD00,0x9000
    DW 0xC407,0xC166,HddEof
    DW 0xD467,0xBD00,0x9000
    DW 0xD304,0xD3C1,0x900C
    DW 0xD319,0xD52D,HddHome
    DW 0xD31C,0xD492,HddSelect
    DW 0xD31F,0xD52F,HddTrack
    DW 0xD322,0xD534,HddSector
    DW 0xD328,0xD542,HddRead
    DW 0xD32B,0xD53E,HddWrite
    DW 0
HddDphBytes:
    DW 0,0,0,0,HddDirectory,HddDpb,0xA400,0xA000
    DW 0,0,0,0,HddDirectory,HddDpb,0xA500,0xA100
    DW 0,0,0,0,HddDirectory,HddDpb,0xA600,0xA200
    DW 0,0,0,0,HddDirectory,HddDpb,0xA700,0xA300
    DB 0x40,0,5,0x1F,1,0xFF,7,0xFF,3,0xFF,0,0,1,0,0,0
HddPayload:
PHASE 0x9000
INCLUDE "hdd.resident.asm"
DEPHASE
HddPayloadEnd:
ASSERT HddPayloadEnd < 0x9000
