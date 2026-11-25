; Experimental nonbanked CP/M 3 BIOS fragment; system integration still required.
; D300h is a provisional test origin; no loader or SPR is supplied.
; The system builder includes cpm3.boot.asm at 7800h.
; Console requires the HC2000 CP/M ROM mapped at E000h and initialized by
; its E000h entry. It uses RAM at DB00h/DF00h and changes interrupt state.
INCLUDE "time/time.inc"
ORG 0xD300
    JP 0x7800
    JP 0x7803
    JP Const
    JP Conin
    JP Conout
    JP List
    JP Auxout
    JP Auxin
    JP Home
    JP Seldsk
    JP Settrk
    JP Setsec
    JP Setdma
    JP Read
    JP Write
    JP Listst
    JP Sectrn
    JP Conost
    JP Auxist
    JP Auxost
    JP Devtbl
    JP Devini
    JP Drvtbl
    JP Multio
    JP Flush
    JP Move
    JP Time
    JP Selmem
    JP Setbnk
    JP Xmove
    JP Userf
    JP Reserv1
    JP Reserv2

; ROM table in bios/src/hc2000/v2/cpm.asm: status returns A=00/FF,
; input returns A, output takes C. ROM must already be initialized.
Const:
    JP 0xE003
Conin:
    JP 0xE006
Conout:
    JP 0xE009
; No printer/auxiliary device implementation. Status reports not ready;
; input returns CP/M EOF and output discards data (these calls have no error ABI).
List:
Auxout:
    RET
Auxin:
    LD A,0x1A
    RET
Listst:
Auxist:
Auxost:
    XOR A
    RET
Conost:
    LD A,0xFF
    RET
; Remember full 16-bit values for the next transfer.  HOME sets track zero.
; HDD transfers are physical 512-byte sectors.
Home:
    LD BC,0
Settrk:
    LD (CurrentTrack),BC
    RET
Setsec:
    LD (CurrentSector),BC
    RET
Setdma:
    LD (CurrentDma),BC
    RET
Seldsk:
    LD A,0xFF
    LD (CurrentDrive),A
    LD A,C
    CP 2
    JP C,SelectFloppy
    CP 12
    JP Z,SelectM
    CP 6
    JP NC,SelAbsent
    ; (DSM/4)+2 must fit within 129 ALV bytes: DSM <= 511.
    ; C still holds the requested drive; inspect the static C-F DPB before
    ; identifying or selecting the device.
    LD A,(HddDpb+6)
    CP 2
    JP NC,SelAbsent
    LD A,C
    LD (Descriptor+2),A
    ; Identify even on later selections, but never replace a pinned generation.
    CALL ClearDescriptor
    LD A,3
    LD (Descriptor+1),A
    LD HL,IdentifyBuffer
    LD (Descriptor+8),HL
    CALL Doorbell
    OR A
    JP NZ,SelAbsent
    LD A,(IdentifyBuffer+8)
    AND 1
    JP Z,SelAbsent
    CALL GenerationAddress
    LD A,(HL)
    OR A
    JR NZ,CompareGeneration
    LD (HL),1
    INC HL
    EX DE,HL
    LD HL,IdentifyBuffer+4
    LD BC,4
    LDIR
    JR SelectReady
CompareGeneration:
    INC HL
    LD DE,IdentifyBuffer+4
    LD B,4
CompareLoop:
    LD A,(DE)
    CP (HL)
    JP NZ,SelAbsent
    INC DE
    INC HL
    DJNZ CompareLoop
SelectReady:
    LD A,(Descriptor+2)
    LD (CurrentDrive),A
SelectDrive:
    LD (CurrentDrive),A
    LD C,A
    LD B,0
    LD HL,DriveTable
    ADD HL,BC
    ADD HL,BC
    LD E,(HL)
    INC HL
    LD D,(HL)
    EX DE,HL
    RET
; M requires exclusive pages 0..511 and the reserved 7E00 bounce record.
; Gate readback rejects machines without the paging base view.
SelectM:
    ; An integration loader must set this only after installing the resident
    ; image, reserving its pages and establishing the stack contract.
    LD A,(MResidentReady)
    CP 0xA5
    JP NZ,SelAbsent
    LD BC,0x7CFD
    IN A,(C)
    CP 0x40
    JP NZ,SelAbsent
    LD A,12
    JP SelectDrive
SelectFloppy:
    JP SelectDrive
SelAbsent:
    LD HL,0
    RET
; HL points to the drive's valid byte followed by four generation bytes.
GenerationAddress:
    LD A,(Descriptor+2)
    SUB 2
    LD E,A
    LD D,0
    LD H,D
    LD L,E
    ADD HL,HL
    ADD HL,HL
    ADD HL,DE
    LD DE,Generations
    ADD HL,DE
    RET
ClearDescriptor:
    ; Preserve drive, clear all reserved fields, LBA, DMA and generation.
    XOR A
    LD (Descriptor+3),A
    LD HL,Descriptor+4
    LD B,12
ClearLoop:
    LD (HL),A
    INC HL
    DJNZ ClearLoop
    RET
Doorbell:
    ; HDD-enabled ports decode all 16 bits; OUT (n),A uses A as the high byte.
    LD BC,0x0081
    LD A,Descriptor & 0xFF
    OUT (C),A
    LD BC,0x0083
    LD A,Descriptor >> 8
    OUT (C),A
    LD BC,0x0089
    LD A,1
    OUT (C),A
    IN A,(C)
    RET
Read:
    LD A,1
    JR Transfer
Write:
    LD A,2
Transfer:
    LD (Descriptor+1),A
    LD A,(CurrentDrive)
    CP 2
    JP C,FloppyTransfer
    CP 12
    JP Z,MDriveTransfer
    CP 6
    JP NC,IoError
    LD (Descriptor+2),A
    LD HL,(CurrentTrack)
    LD A,H
    CP 0x40
    JP NC,IoError
    LD HL,(CurrentSector)
    LD A,H
    OR A
    JP NZ,IoError
    LD A,L
    CP 64
    JP NC,IoError
    ; Restrict the whole 512-byte transfer below the protected BIOS at D300.
    LD HL,(CurrentDma)
    LD DE,0xD101
    OR A
    SBC HL,DE
    JP NC,IoError
    CALL ClearDescriptor
    LD HL,(CurrentDma)
    LD (Descriptor+8),HL
    ; Shift the 16-bit track into A:HL, retaining the upper four bits.
    LD HL,(CurrentTrack)
    XOR A
    ADD HL,HL
    ADC A,A
    ADD HL,HL
    ADC A,A
    ADD HL,HL
    ADC A,A
    ADD HL,HL
    ADC A,A
    ADD HL,HL
    ADC A,A
    ADD HL,HL
    ADC A,A
    LD (Descriptor+6),A
    LD DE,(CurrentSector)
    ADD HL,DE
    LD (Descriptor+4),HL
    CALL GenerationAddress
    LD A,(HL)
    OR A
    JR Z,IoError
    INC HL
    LD DE,Descriptor+12
    LD BC,4
    LDIR
    CALL Doorbell
    JP TransferResult
IoError:
    LD A,1
    RET
 ; M: one track is one page, and each sector is one 128-byte record.
; The caller must supply a stack outside 7800-BFFF with room for BIOS calls;
; The paged copy switches to its own stack on the BIOS page.
MDriveTransfer:
    LD BC,0x7CFD
    IN A,(C)
    CP 0x40
    JP NZ,IoError
    LD HL,(CurrentTrack)
    LD A,H
    CP 2
    JP NC,IoError
    EX DE,HL
    LD HL,(CurrentSector)
    LD A,H
    OR A
    JP NZ,IoError
    LD A,L
    CP 128
    JP NC,IoError
    RRCA
    AND 0x80
    LD C,A
    LD A,L
    SRL A
    OR 0x80
    LD H,A
    LD L,C
    ; Preserve page and mapped address while validating/staging the DMA.
    PUSH DE
    PUSH HL
    LD HL,(CurrentDma)
    ; DMA must not overlap the resident block or protected BIOS, or wrap.
    LD DE,0x7781
    OR A
    SBC HL,DE
    JR C,MDmaReady
    LD HL,(CurrentDma)
    LD DE,0x8000
    OR A
    SBC HL,DE
    JR C,MDmaError
    LD HL,(CurrentDma)
    LD DE,0xD281
    OR A
    SBC HL,DE
    JR NC,MDmaError
MDmaReady:
    LD A,(Descriptor+1)
    CP 2
    JR NZ,MCall
    LD HL,(CurrentDma)
    LD DE,0x7E00
    LD BC,128
    LDIR
MCall:
    POP HL
    POP DE
    LD A,(Descriptor+1)
    DEC A
    CALL MTransfer
    OR A
    JP NZ,IoError
    LD A,(Descriptor+1)
    CP 1
    JR NZ,MDone
    LD HL,0x7E00
    LD DE,(CurrentDma)
    LD BC,128
    LDIR
MDone:
    XOR A
    RET
MDmaError:
    POP HL
    POP DE
    JP IoError
; CP/M tracks alternate heads, as Dsk.LogicalBytes enumerates cylinder/side.
; SETSEC is a translated physical ID (1,3,5,7,9,2,4,6,8).
FloppyTransfer:
    LD (FloppyCommand+1),A
    LD HL,(CurrentTrack)
    LD A,H
    OR A
    JP NZ,IoError
    LD A,L
    CP 160
    JP NC,IoError
    AND 1
    LD (FloppyCommand+3),A
    SRL L
    LD A,L
    LD (FloppyCommand+2),A
    LD HL,(CurrentSector)
    LD A,H
    OR A
    JP NZ,IoError
    LD A,L
    DEC A
    CP 9
    JP NC,IoError
    INC A
    LD (FloppyCommand+4),A
    LD HL,(CurrentDma)
    LD DE,0xD101
    OR A
    SBC HL,DE
    JP NC,IoError
    LD HL,(CurrentDma)
    LD (FloppyCommand+5),HL
    LD BC,0x0081
    LD A,FloppyCommand & 0xFF
    OUT (C),A
    LD BC,0x0083
    LD A,FloppyCommand >> 8
    OUT (C),A
    LD BC,0x0089
    LD A,(Descriptor+1)
    OUT (C),A
    IN A,(C)
    JP TransferResult
; No write cache.
Flush:
    XOR A
    RET
; BC is the sector, DE is a translation table or zero for identity.
Sectrn:
    LD A,D
    OR E
    JR Z,SectIdentity
    LD H,D
    LD L,E
    ADD HL,BC
    LD L,(HL)
    LD H,0
    RET
SectIdentity:
    LD H,B
    LD L,C
    RET
; No device table or device initialization implemented.
Devtbl:
    LD HL,0
    RET
Drvtbl:
    LD HL,DriveTable
    RET
Devini:
    LD A,0xFF
    RET
; The BDOS calls READ or WRITE once for each physical sector. No batching.
Multio:
    RET
; CP/M MOVE: HL destination, DE source, BC count; both pointers advance.
; LDIR expects the reverse register order, and BC=0 would copy 64K.
Move:
    LD A,B
    OR C
    RET Z
    EX DE,HL
    LDIR
    EX DE,HL
    RET
; CP/M 3 TIME (SG 3.4.26): C=0 updates the SCB date and BCD time fields from
; the host clock port 709D; C=0FFH means the BDOS has set the SCB fields, and
; the host clock is read-only, so it is a preserve/ignore. HL and DE are
; restored on every exit.
; These placeholder SCB stores are labelled so their high-byte image offsets
; can be delivered to the system linker without scanning for FE opcodes.
; The production SCB page and linked relocation are not yet proven.
ScbBase EQU 0xFE00
ScbDate EQU ScbBase+0x58
ScbHour EQU ScbBase+0x5A
ScbMin  EQU ScbBase+0x5B
ScbSec  EQU ScbBase+0x5C
Time:
    PUSH HL
    PUSH DE
    LD A,C
    CP 0xFF
    JR Z,TimeExit
    LD HL,TimeBuffer
    CALL TimeRead
    JR C,TimeExit
    ; buffer: +0/+1 year little-endian, +2 month, +3 day, +4 hour, +5 min, +6 sec
    LD A,(TimeBuffer+1)
    LD H,A
    LD A,(TimeBuffer)
    LD L,A
    LD A,(TimeBuffer+2)
    LD D,A
    LD A,(TimeBuffer+3)
    LD E,A
    CALL TimeToCpmDay
    JR C,TimeExit
ScbWord_Date:
    LD (ScbDate),HL
    LD A,(TimeBuffer+4)
    CALL ByteToBcd
ScbWord_Hour:
    LD (ScbHour),A
    LD A,(TimeBuffer+5)
    CALL ByteToBcd
ScbWord_Min:
    LD (ScbMin),A
    LD A,(TimeBuffer+6)
    CALL ByteToBcd
ScbWord_Sec:
    LD (ScbSec),A
TimeExit:
    POP DE
    POP HL
    RET
; Nonbanked build only: these are no-ops, not bank-switching support.
Selmem:
Setbnk:
Xmove:
Userf:
Reserv1:
Reserv2:
    RET

; Disk state.
CurrentTrack:  DW 0
CurrentSector: DW 0
CurrentDma:    DW 0x0080

; C-F are fixed 8 MiB slots. ALVs, BCBs and buffers are manually allocated
; in the buffer block (cpm3.buf.asm) at 8000h; directory hashing is enabled
; for C-F (the in-scope hashed drives) and disabled for A and B.
; Absolute addresses mirrored from cpm3.buf.asm; tests compare both images.
BufDirA     EQU 0x8000
BufDirB     EQU 0x8200
BufDirCDEF  EQU 0x8400
BufDataA    EQU 0x8600
BufDataB    EQU 0x8800
BufDataCDEF EQU 0x8A00
BcbDirA     EQU 0x8C00
BcbDirB     EQU 0x8C0C
BcbDirCDEF  EQU 0x8C18
BcbDataA    EQU 0x8C24
BcbDataB    EQU 0x8C30
BcbDataCDEF EQU 0x8C3C
CsvA        EQU 0x8C48
CsvB        EQU 0x8C69
AlvA        EQU 0x8C8A
AlvB        EQU AlvA+90
AlvC        EQU AlvB+90
AlvD        EQU AlvC+129
AlvE        EQU AlvD+129
AlvF        EQU AlvE+129
HashC       EQU AlvF+129
HashD       EQU HashC+2048
HashE       EQU HashD+2048
HashF       EQU HashE+2048
AlvM        EQU HashF+2048
DirBufM     EQU AlvM+257
DataBufM    EQU DirBufM+128
BcbDirM     EQU DataBufM+128
BcbDataM    EQU BcbDirM+12
MResidentReady: DB 0
CurrentDrive: DB 0xFF
DriveTable:
    DW DphA,DphB,DphC,DphD,DphE,DphF,0,0
    DW 0,0,0,0,DphM,0,0,0
; Each 25-byte DPH: XLT, 9 scratch bytes, media flag, then DPB, CSV, ALV,
; DIRBCB, DTABCB, HASH words and the HBANK byte (DRI System Guide 3.3.2).
; DIRBCB/DTABCB/CSV/ALV point at the real buffer block; C-F share the fixed
; BCBs and buffers, A and B keep separate removable-media BCBs.
DphA:
    DW FloppySkew
    DB 0,0,0,0,0,0,0,0,0,0
    DW FloppyDpb,CsvA,AlvA,BcbDirA,BcbDataA,0xFFFF
    DB 0
DphB:
    DW FloppySkew
    DB 0,0,0,0,0,0,0,0,0,0
    DW FloppyDpb,CsvB,AlvB,BcbDirB,BcbDataB,0xFFFF
    DB 0
FloppySkew: DB 1,3,5,7,9,2,4,6,8
FloppyDpb:
    DW 36
    DB 4,15,0
    DW 351,127
    DB 0xC0,0
    DW 32,4
    DB 2,3
FloppyCommand: DB 1,0,0,0,0,0,0,0
DphC:
    DW 0
    DB 0,0,0,0,0,0,0,0,0,0
    DW HddDpb,0,AlvC,BcbDirCDEF,BcbDataCDEF,HashC
    DB 0
DphD:
    DW 0
    DB 0,0,0,0,0,0,0,0,0,0
    DW HddDpb,0,AlvD,BcbDirCDEF,BcbDataCDEF,HashD
    DB 0
DphE:
    DW 0
    DB 0,0,0,0,0,0,0,0,0,0
    DW HddDpb,0,AlvE,BcbDirCDEF,BcbDataCDEF,HashE
    DB 0
DphF:
    DW 0
    DB 0,0,0,0,0,0,0,0,0,0
    DW HddDpb,0,AlvF,BcbDirCDEF,BcbDataCDEF,HashF
    DB 0
DphM:
    DW 0
    DB 0,0,0,0,0,0,0,0,0,0
    DW MDpb,0,AlvM,BcbDirM,BcbDataM,0xFFFF
    DB 0
MDpb:
    DW 128
    DB 6,63,1
    DW 1023,255
    DB 0x80,0
    DW 0,0
    DB 0,0
; 256 logical records/track, 16 KiB blocks, one directory block.
HddDpb:
    DW 256
    DB 7,127,7
    DW 511,511
    DB 0x80,0
    DW 0x8000,0
    DB 2,3
HddDpbEnd:

; Device-owned IDENTIFY DMA and descriptor reside in the BIOS reservation.
Generations: DEFS 20,0
Descriptor: DB 2,0,0,0
    DEFS 12,0
IdentifyBuffer: DEFS 512,0
TimeBuffer: DEFS 8,0
; The original console ROM uses DB00 state and a downward stack at DB99.
ASSERT $ <= 0xDB00
ORG 0xDBA0
TIME_CODE

; Translate either receiver's write-protected status to the CP/M WRITE ABI.
; All other receiver failures remain physical errors.
TransferResult:
    OR A
    RET Z
    CP 8
    JP NZ,IoError
    LD A,(Descriptor+1)
    CP 2
    JP NZ,IoError
    LD A,2
    RET

INCLUDE "cpm3.m-transfer.inc"
