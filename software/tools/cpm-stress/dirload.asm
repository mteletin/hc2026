; DIRLOAD.COM: create and verify 96 files in the current CP/M user area.
; Usage: DIRLOAD M: (or C:, A:); no drive argument uses the current drive.
; Refuses before writing if STRESS00.DAT through STRESS95.DAT already exist.
; Created files are retained for independent inspection and manual cleanup.
ORG 0x0100

Bdos EQU 5
ArgFcb EQU 0x005C

    LD HL,ArgFcb+1
    LD B,11
CheckArg:
    LD A,(HL)
    CP 0x20
    JP NZ,BadArgument
    INC HL
    DJNZ CheckArg
    LD A,(ArgFcb)
    LD (Drive),A
    XOR A
    LD (Index),A
Preflight:
    CALL ResetFcb
    LD DE,Fcb
    LD C,15
    CALL Bdos
    INC A
    JR Z,PreflightNext
    LD DE,Fcb
    LD C,16
    CALL Bdos
    LD DE,Collision
    JP Finish
PreflightNext:
    LD A,(Index)
    INC A
    LD (Index),A
    CP 96
    JR NZ,Preflight
    XOR A
    LD (Index),A
    LD C,26
    LD DE,Buffer
    CALL Bdos

NextFile:
    CALL ResetFcb
    LD DE,Fcb
    LD C,22
    CALL Bdos
    INC A
    JP Z,Failed
    CALL Fill
    LD DE,Fcb
    LD C,21
    CALL Bdos
    OR A
    JP NZ,Failed
    LD DE,Fcb
    LD C,16
    CALL Bdos
    INC A
    JP Z,Failed
    CALL ResetFcb
    LD DE,Fcb
    LD C,15
    CALL Bdos
    INC A
    JP Z,Failed
    LD HL,Buffer
    LD B,128
Clear:
    LD (HL),0
    INC HL
    DJNZ Clear
    LD DE,Fcb
    LD C,20
    CALL Bdos
    OR A
    JP NZ,Failed
    LD HL,Buffer
    LD B,0
Compare:
    LD A,(Index)
    XOR B
    XOR 0xA5
    CP (HL)
    JP NZ,Failed
    INC HL
    INC B
    LD A,B
    CP 128
    JR NZ,Compare
    LD DE,Fcb
    LD C,16
    CALL Bdos
    INC A
    JP Z,Failed
    LD A,(Index)
    INC A
    LD (Index),A
    CP 96
    JP NZ,NextFile
    LD DE,Success
    JR Finish
Failed:
    LD DE,Failure
Finish:
    PUSH DE
    LD C,26
    LD DE,0x0080
    CALL Bdos
    POP DE
    LD C,9
    CALL Bdos
    RET
BadArgument:
    LD DE,Usage
    LD C,9
    CALL Bdos
    RET

ResetFcb:
    LD HL,Fcb
    LD B,36
FcbZero:
    LD (HL),0
    INC HL
    DJNZ FcbZero
    LD A,(Drive)
    LD (Fcb),A
    LD HL,Name
    LD DE,Fcb+1
    LD BC,11
    LDIR
    LD A,(Index)
    LD B,0
Tens:
    CP 10
    JR C,Digits
    SUB 10
    INC B
    JR Tens
Digits:
    ADD A,0x30
    LD (Fcb+8),A
    LD A,B
    ADD A,0x30
    LD (Fcb+7),A
    RET
Fill:
    LD HL,Buffer
    LD B,0
FillByte:
    LD A,(Index)
    XOR B
    XOR 0xA5
    LD (HL),A
    INC HL
    INC B
    LD A,B
    CP 128
    JR NZ,FillByte
    RET

Drive: DB 0
Index: DB 0
Name: DB "STRESS00DAT"
Success: DB 13,10,"DIRLOAD: 96 FILES VERIFY OK",13,10,"$"
Collision: DB 13,10,"DIRLOAD: STRESS FILE EXISTS; NO WRITES",13,10,"$"
Failure: DB 13,10,"DIRLOAD: FAILED; FILES MAY REMAIN",13,10,"$"
Usage: DB 13,10,"DIRLOAD: USE DIRLOAD [DRIVE:]",13,10,"$"
Fcb: DS 36
Buffer: DS 128
