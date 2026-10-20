; A/B-loaded COM, explicit opt-in plain-paged tape profile only.
; Header is supplied from the owner-mounted session, never a second image.
INCLUDE "tape.layout.inc"
ORG 0x0100
    JP TapeInstall
    DB "TPV1"
TapeInstallMetadata:
    DW 0,0
    DB 0,0
TapeInstall:
    ; Compare against the owner binding; the COM never writes this descriptor.
    LD A,(TapeDescriptor)
    CP 1
    JP NZ,TapeInstallRefused
    LD HL,TapeInstallMetadata
    LD DE,TapeDescriptor+2
    LD B,6
TapeCheckBinding:
    LD A,(DE)
    CP (HL)
    JP NZ,TapeInstallRefused
    INC HL
    INC DE
    DJNZ TapeCheckBinding
    ; A zero/unbound header must fail before changing memory or vectors.
    LD HL,(TapeInstallMetadata)
    LD A,H
    OR A
    JP Z,TapeInstallRefused
    LD HL,(TapeInstallMetadata+2)
    LD DE,4
    OR A
    SBC HL,DE
    JP C,TapeInstallRefused
    LD DE,2045
    OR A
    SBC HL,DE
    JP NC,TapeInstallRefused
    ; Last bank must fit the existing 16-bit latch.
    LD HL,(TapeInstallMetadata+2)
    DEC HL
    SRL H
    RR L
    SRL H
    RR L
    LD DE,(TapeInstallMetadata)
    ADD HL,DE
    JP C,TapeInstallRefused
    LD A,(TapeInstallMetadata+4)
    CP 2
    JP C,TapeInstallRefused
    CP 16
    JP NC,TapeInstallRefused
    CP 12
    JP Z,TapeInstallRefused
    LD A,(TapeInstallMetadata+5)
    CP 2
    JP NC,TapeInstallRefused
    LD BC,0x7FFD
    IN A,(C)
    CP 0xFF
    JP Z,TapeInstallRefused
    AND 0x20
    JP NZ,TapeInstallRefused
    LD HL,TapePatchTable
TapePreflight:
    LD E,(HL)
    INC HL
    LD D,(HL)
    INC HL
    LD A,D
    OR E
    JR Z,TapePreflightDone
    LD A,(DE)
    CP (HL)
    JP NZ,TapeInstallRefused
    INC HL
    INC DE
    LD A,(DE)
    CP (HL)
    JP NZ,TapeInstallRefused
    INC HL
    INC HL
    INC HL
    JR TapePreflight
TapePreflightDone:
    LD HL,TapeOpcodeSites
    LD B,8
TapeGuardOpcode:
    LD E,(HL)
    INC HL
    LD D,(HL)
    INC HL
    LD A,(DE)
    CP 0xC3
    JP NZ,TapeInstallRefused
    DJNZ TapeGuardOpcode
    LD A,(0xC3F4)
    CP 0x11
    JP NZ,TapeInstallRefused
    LD A,(0xD465)
    CP 0xFB
    JP NZ,TapeInstallRefused
    LD A,(0xD466)
    CP 0xC3
    JP NZ,TapeInstallRefused
    LD A,I
    PUSH AF
    DI
    LD HL,TapePayload
    LD DE,0x6000
    LD BC,TapePayloadEnd-TapePayload
    LDIR
    LD HL,(TapeBlocks)
    DEC HL
    LD (TapeDpb+5),HL
    LD A,H
    OR A
    LD A,1
    JR NZ,TapeLargeDpb
    LD A,3
TapeLargeDpb:
    LD (TapeDpb+4),A
    LD HL,TapePatchTable
TapePatchNext:
    LD E,(HL)
    INC HL
    LD D,(HL)
    INC HL
    LD A,D
    OR E
    JR Z,TapeInstalled
    INC HL
    INC HL
    LD A,(HL)
    LD (DE),A
    INC HL
    INC DE
    LD A,(HL)
    LD (DE),A
    INC HL
    JR TapePatchNext
TapeInstalled:
    POP AF
    JP PO,TapeInstallNoEi
    EI
TapeInstallNoEi:
    ; Bind replaced the CCP's BD00-BFFF mapping. Reload the original CCP
    ; through the retained warmboot instead of returning into the old bank.
    JP 0
TapeInstallRefused:
    LD A,1
    RET
TapeOpcodeSites:
    DW 0x0005,0xD303,0xD318,0xD31B,0xD31E,0xD321,0xD327,0xD32A
TapePatchTable:
    DW 0x0006,TapeOriginalBdos,0x6003
    DW 0xC3F5,0xBD00,0x6000
    DW 0xD467,0xBD00,0x6000
    DW 0xD304,0xD3C1,TapeWarm
    DW 0xD319,0xD52D,TapeHome
    DW 0xD31C,0xD492,TapeSelect
    DW 0xD31F,0xD52F,TapeTrack
    DW 0xD322,0xD534,TapeSector
    DW 0xD328,0xD542,TapeRead
    DW 0xD32B,0xD53E,TapeWrite
    DW 0
TapePayload:
PHASE 0x6000
INCLUDE "tape.resident.asm"
DEPHASE
TapePayloadEnd:
ASSERT TapePayloadEnd < 0x6000
