; COPYCHK source destination -- non-destructive sequential copy and verify.
; Both files remain available for independent inspection.
ORG 0x100
Bdos EQU 5
    LD HL,0x005C
    LD DE,Src
    LD BC,12
    LDIR
    LD HL,0x006C
    LD DE,Dst
    LD BC,12
    LDIR
    LD HL,Src
    CALL Validate
    JP NZ,Usage
    LD HL,Dst
    CALL Validate
    JP NZ,Usage
    ; Resolve default drive so explicit and implicit names compare equally.
    LD C,25
    CALL Bdos
    INC A
    LD B,A
    LD A,(Src)
    OR A
    JR NZ,SrcDrive
    LD A,B
    LD (Src),A
SrcDrive:
    LD A,(Dst)
    OR A
    JR NZ,DstDrive
    LD A,B
    LD (Dst),A
DstDrive:
    LD HL,Src
    LD DE,Dst
    LD B,12
SameLoop:
    LD A,(DE)
    CP (HL)
    JR NZ,NotSame
    INC HL
    INC DE
    DJNZ SameLoop
    JP Usage
NotSame:
    LD DE,Dst
    LD C,15
    CALL Bdos
    CP 0xFF
    JR Z,NoCollision
    LD DE,Dst
    LD C,16
    CALL Bdos
    LD DE,Collision
    JP Finish
NoCollision:
    CALL ResetDst
    LD DE,Src
    LD C,15
    CALL Bdos
    CP 0xFF
    JP Z,Failed
    LD A,1
    LD (SrcOpen),A
    LD DE,Dst
    LD C,22
    CALL Bdos
    CP 0xFF
    JP Z,Failed
    LD A,1
    LD (DstOpen),A
    LD DE,Buf1
    LD C,26
    CALL Bdos
CopyLoop:
    LD DE,Src
    LD C,20
    CALL Bdos
    OR A
    JP Z,WriteRecord
    CP 1
    JP NZ,Failed
    CALL CloseBoth
    JP NZ,Failed
    CALL ResetSrc
    CALL ResetDst
    LD DE,Src
    LD C,15
    CALL Bdos
    CP 0xFF
    JP Z,Failed
    LD A,1
    LD (SrcOpen),A
    LD DE,Dst
    LD C,15
    CALL Bdos
    CP 0xFF
    JP Z,Failed
    LD A,1
    LD (DstOpen),A
VerifyLoop:
    LD DE,Buf1
    LD C,26
    CALL Bdos
    LD DE,Src
    LD C,20
    CALL Bdos
    CP 2
    JP NC,Failed
    LD (ReadResult),A
    LD DE,Buf2
    LD C,26
    CALL Bdos
    LD DE,Dst
    LD C,20
    CALL Bdos
    CP 2
    JP NC,Failed
    LD B,A
    LD A,(ReadResult)
    CP B
    JP NZ,Failed
    OR A
    JR NZ,Verified
    LD HL,Buf1
    LD DE,Buf2
    LD B,128
Compare:
    LD A,(DE)
    CP (HL)
    JP NZ,Failed
    INC HL
    INC DE
    DJNZ Compare
    JR VerifyLoop
Verified:
    CALL CloseBoth
    JP NZ,Failed
    LD DE,Success
    JR Finish
WriteRecord:
    LD DE,Dst
    LD C,21
    CALL Bdos
    OR A
    JP NZ,Failed
    JP CopyLoop
Failed:
    CALL CloseBoth
    LD DE,Failure
    JR Finish
Usage:
    LD DE,UsageText
Finish:
    PUSH DE
    LD DE,0x80
    LD C,26
    CALL Bdos
    POP DE
    LD C,9
    CALL Bdos
    RET
; Require a name and reject '?' in name or extension.
Validate:
    INC HL
    LD A,(HL)
    CP 0x20
    JR Z,Invalid
    LD B,11
CheckName:
    LD A,(HL)
    CP '?'
    JR Z,Invalid
    INC HL
    DJNZ CheckName
    XOR A
    RET
Invalid:
    LD A,1
    OR A
    RET
; Clear extent, record count and random record fields, retaining drive/name.
ResetSrc:
    LD HL,Src+12
    JR Reset
ResetDst:
    LD HL,Dst+12
Reset:
    LD B,24
    XOR A
ResetLoop:
    LD (HL),A
    INC HL
    DJNZ ResetLoop
    RET
; Always attempt both closes; nonzero result means a close failed.
CloseBoth:
    XOR A
    LD (CloseError),A
    LD A,(SrcOpen)
    OR A
    JR Z,CloseDst
    XOR A
    LD (SrcOpen),A
    LD DE,Src
    LD C,16
    CALL Bdos
    CP 0xFF
    JR NZ,CloseDst
    LD A,1
    LD (CloseError),A
CloseDst:
    LD A,(DstOpen)
    OR A
    JR Z,CloseDone
    XOR A
    LD (DstOpen),A
    LD DE,Dst
    LD C,16
    CALL Bdos
    CP 0xFF
    JR NZ,CloseDone
    LD A,1
    LD (CloseError),A
CloseDone:
    LD A,(CloseError)
    OR A
    RET
SrcOpen: DB 0
DstOpen: DB 0
CloseError: DB 0
ReadResult: DB 0
Success: DB 13,10,"COPYCHK: COPY AND VERIFY OK",13,10,"$"
Collision: DB 13,10,"COPYCHK: DESTINATION EXISTS; NO WRITES",13,10,"$"
Failure: DB 13,10,"COPYCHK: FAILED; FILE MAY REMAIN",13,10,"$"
UsageText: DB 13,10,"COPYCHK: USE COPYCHK SOURCE DEST (DISTINCT, NO WILDCARDS)",13,10,"$"
Src: DS 36
Dst: DS 36
Buf1: DS 128
Buf2: DS 128
