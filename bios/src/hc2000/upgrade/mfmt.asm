; MFMT.COM: initialise the M: RAM drive and prove it with one known file.
; Assembled by Hc2000.Asm. Calls the stable BIOS JP table, never page zero.
; SELDSK D31B, SETTRK D31E, SETSEC D321, SETDMA D324, WRITE D32A stay C3.
ORG 0x0100

Bdos     EQU 5
SelDsk   EQU 0xD31B
SetTrk   EQU 0xD31E
SetSec   EQU 0xD321
SetDma   EQU 0xD324
BiosWr   EQU 0xD32A
MDrive   EQU 12
DirSecs  EQU 64
DmaBuf   EQU 0x0400
Fcb      EQU 0x005C

    LD SP,0x7EF0
    LD C,MDrive
    CALL SelDsk
    LD A,H
    OR L
    JP Z,Absent

    LD HL,DmaBuf
    LD DE,DmaBuf+1
    LD BC,127
    LD (HL),0xE5
    LDIR
    LD C,0
Fill:
    PUSH BC
    LD BC,0
    CALL SetTrk
    POP BC
    PUSH BC
    CALL SetSec
    LD BC,DmaBuf
    CALL SetDma
    LD C,1
    CALL BiosWr
    POP BC
    INC C
    LD A,C
    CP DirSecs
    JR C,Fill

    LD C,13
    CALL Bdos
    LD C,14
    LD E,MDrive
    CALL Bdos
    ; BDOS 13 put the DMA back at 0080. The pattern and the compare
    ; both use DmaBuf, so the file calls must too.
    LD C,26
    LD DE,DmaBuf
    CALL Bdos

    LD HL,Fcb
    LD B,33
ZeroFcb:
    LD (HL),0
    INC HL
    DJNZ ZeroFcb
    LD A,MDrive+1
    LD (Fcb),A
    LD HL,Name
    LD DE,Fcb+1
    LD BC,11
    LDIR

    LD DE,Fcb
    LD C,22
    CALL Bdos
    INC A
    JP Z,VerifyFail
    XOR A
    LD (Fcb+32),A
    LD HL,DmaBuf
    LD B,0
Pat:
    LD A,B
    XOR 0x53
    LD (HL),A
    INC HL
    INC B
    LD A,B
    CP 128
    JR C,Pat
    LD A,3
WriteRec:
    PUSH AF
    LD DE,Fcb
    LD C,21
    CALL Bdos
    OR A
    JR NZ,WriteBad
    POP AF
    DEC A
    JR NZ,WriteRec
    LD DE,Fcb
    LD C,16
    CALL Bdos

    XOR A
    LD (Fcb+12),A
    LD (Fcb+14),A
    LD (Fcb+32),A
    LD DE,Fcb
    LD C,15
    CALL Bdos
    INC A
    JR Z,VerifyFail
    ; Drop the pattern so a read that never reaches DmaBuf cannot pass.
    LD HL,DmaBuf
    LD B,0
Wipe:
    LD (HL),0
    INC HL
    INC B
    LD A,B
    CP 128
    JR C,Wipe
    LD A,3
ReadRec:
    PUSH AF
    LD DE,Fcb
    LD C,20
    CALL Bdos
    OR A
    JR NZ,ReadBad
    LD HL,DmaBuf
    LD B,0
Cmp:
    LD A,B
    XOR 0x53
    CP (HL)
    JR NZ,ReadBad
    INC HL
    INC B
    LD A,B
    CP 128
    JR C,Cmp
    POP AF
    DEC A
    JR NZ,ReadRec

    LD DE,Done
    CALL Print
    LD DE,Ok
    CALL Print
    LD C,14
    LD E,0
    CALL Bdos
    RET

WriteBad:
    POP AF
    JR VerifyFail
ReadBad:
    POP AF
VerifyFail:
    LD DE,Done
    CALL Print
    LD DE,Bad
    CALL Print
    LD C,14
    LD E,0
    CALL Bdos
    RET

Absent:
    LD DE,NoM
    CALL Print
    RET

Print:
    LD C,9
    CALL Bdos
    RET

Name:
    DB "TEST    TST"
Done:
    DB "M: FORMATTED",13,10,"$"
Ok:
    DB "M: FILE VERIFY OK",13,10,"$"
Bad:
    DB "M: FILE VERIFY FAIL",13,10,"$"
NoM:
    DB "M: ABSENT",13,10,"$"

    ASSERT $ < 0x8000
