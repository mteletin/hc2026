; Executed outside the permanently enabled 8000-BFFF plain-paged window.
; No firmware call, IRQ or caller stack access while another bank is visible.
TapeResidentStart:
    JP TapeWarmResume
    JP TapeOriginalBdos
TapeHome:
    LD A,(TapeSelected)
    CP 2
    JP C,0xD52D
    LD HL,0
    LD (TapeTrackValue),HL
    RET
TapeTrack:
    LD (TapeTrackValue),BC
    LD A,(TapeSelected)
    CP 2
    RET NC
    JP 0xD52F
TapeSector:
    LD (TapeSectorValue),BC
    LD A,(TapeSelected)
    CP 2
    RET NC
    JP 0xD534
TapeSelect:
    LD A,C
    CP 2
    JR NC,TapeSelectVolume
    LD (TapeSelected),A
    JP 0xD492
TapeSelectVolume:
    LD A,(TapeDescriptor)
    CP 1
    JR NZ,TapeSelectRefused
    LD A,(TapeLetter)
    CP C
    JR NZ,TapeSelectRefused
    LD (TapeSelected),A
    LD HL,TapeDph
    RET
TapeSelectRefused:
    LD HL,0
    RET
TapeRead:
    LD A,(TapeSelected)
    CP 2
    JP C,0xD542
    XOR A
    JR TapeIo
TapeWrite:
    LD A,(TapeSelected)
    CP 2
    JP C,0xD53E
    LD A,1
TapeIo:
    LD (TapeOperation),A
    PUSH BC
    PUSH DE
    PUSH HL
    LD A,I
    PUSH AF
    DI
    LD (TapeSavedSp),SP
    LD SP,TapeStackTop
    CALL TapeTransfer
    LD (TapeResult),A
    LD SP,(TapeSavedSp)
    POP AF
    JP PO,TapeNoEi
    EI
TapeNoEi:
    POP HL
    POP DE
    POP BC
    LD A,(TapeResult)
    RET
TapeTransfer:
    ; Validate everything before changing a latch or destination byte.
    LD A,(TapeDescriptor)
    CP 1
    JP NZ,TapeRefused
    LD BC,0x7FFD
    IN A,(C)
    CP 0xFF
    JP Z,TapeRefused
    AND 0x20
    JP NZ,TapeRefused
    IN A,(C)
    LD (TapeSavedControl),A
    LD BC,0x7EFD
    IN A,(C)
    LD (TapeSavedBank),A
    LD BC,0x7DFD
    IN A,(C)
    LD (TapeSavedBank+1),A
    LD HL,(TapeSectorValue)
    LD A,H
    OR A
    JP NZ,TapeRefused
    LD A,L
    CP 128
    JP NC,TapeRefused
    ; Block index = track*4 + sector/32, compare against image/4096.
    LD A,L
    SRL A
    SRL A
    SRL A
    SRL A
    SRL A
    LD E,A
    LD D,0
    LD HL,(TapeTrackValue)
    LD A,H
    CP 2
    JP NC,TapeRefused
    ADD HL,HL
    ADD HL,HL
    ADD HL,DE
    LD DE,(TapeBlocks)
    OR A
    SBC HL,DE
    JP NC,TapeRefused
    LD HL,(0xD875)
    LD (TapeDma),HL
    ; User DMA may be TPA or a window address (via the bounce). The only
    ; resident-owned DMA accepted is its exact 128-byte directory buffer.
    LD DE,0x5F81
    OR A
    SBC HL,DE
    JR C,TapeDmaValid
    LD HL,(TapeDma)
    LD DE,TapeDirectory
    OR A
    SBC HL,DE
    JR Z,TapeDmaValid
    LD HL,(TapeDma)
    LD DE,0x8000
    OR A
    SBC HL,DE
    JP C,TapeRefused
    LD DE,0x3F81
    OR A
    SBC HL,DE
    JP NC,TapeRefused
TapeDmaValid:
    LD A,(TapeOperation)
    OR A
    JR Z,TapeMap
    LD A,(TapeProtected)
    OR A
    JP NZ,TapeRefused
    LD HL,(TapeDma)
    LD DE,TapeBounce
    LD BC,128
    LDIR
TapeMap:
    ; Preserve control, including ROM/display selection; change only bank.
    LD HL,(TapeFirstPage)
    LD DE,(TapeTrackValue)
    ADD HL,DE
    JP C,TapeRefused
    LD BC,0x7EFD
    OUT (C),L
    LD BC,0x7DFD
    OUT (C),H
    LD A,(TapeSectorValue)
    LD L,0
    SRL A
    RR L
    ADD A,0x80
    LD H,A
    LD A,(TapeOperation)
    OR A
    JR NZ,TapeStore
    LD DE,TapeBounce
    LD BC,128
    LDIR
    CALL TapeRestoreBank
    LD HL,TapeBounce
    LD DE,(TapeDma)
    LD BC,128
    LDIR
    XOR A
    RET
TapeStore:
    EX DE,HL
    LD HL,TapeBounce
    LD B,128
TapeStoreByte:
    LD A,(HL)
    LD (DE),A
    LD A,(DE)
    CP (HL)
    JR NZ,TapeStoreRefused
    INC HL
    INC DE
    DJNZ TapeStoreByte
    CALL TapeRestoreBank
    XOR A
    RET
TapeStoreRefused:
    CALL TapeRestoreBank
TapeRefused:
    LD A,1
    RET
TapeRestoreBank:
    LD BC,0x7EFD
    LD A,(TapeSavedBank)
    OUT (C),A
    LD BC,0x7DFD
    LD A,(TapeSavedBank+1)
    OUT (C),A
    RET
TapeWarm:
    ; Retained warmboot reloads only BD00+ and jumps through patched D467.
    JP 0xD3C1
TapeWarmResume:
    LD HL,(0xC3F5)
    LD DE,0xBD00
    OR A
    SBC HL,DE
    JR NZ,TapeWarmRefused
    LD HL,0x6000
    LD (0xC3F5),HL
    LD HL,0x6003
    LD (0x0006),HL
    JP 0xBD00
TapeWarmRefused:
    DI
    HALT
    JR TapeWarmRefused
TapeCodeEnd:
ASSERT TapeCodeEnd < 0x7000
DEFS 0x7000-$,0
; Binding fields live only in the owner-written common-RAM descriptor.
TapeSelected: DB 0
TapeOperation: DB 0
TapeTrackValue: DW 0
TapeSectorValue: DW 0
TapeSavedSp: DW 0
TapeDma: DW 0
TapeSavedControl: DB 0
TapeSavedBank: DW 0
TapeResult: DB 0
DEFS 0x7040-$,0
TapeDph:
    DW 0,0,0,0,TapeDirectory,TapeDpb,TapeCsv,TapeAlv
TapeDpb:
    ; SPT128, 4-KiB blocks, DSM/EXM set from the mounted image by init.
    DW 128
    DB 5,31,1
    DW 0,255
    DB 0xC0,0
    DW 64,0
DEFS 0x7340-$,0
TapeResidentEnd:
ASSERT TapeResidentEnd < 0x7400
