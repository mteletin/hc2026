; BIGFILE [A:/C:/M:] -- sequential large-file stress in the current user.
; No formatting or replacement: BIGFILE.DAT is retained for inspection.
; Record r starts with low(r), high(r); remaining bytes mix r and offset.
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
    OR A
    JR Z,DriveOk
    CP 1
    JR Z,DriveOk
    CP 3
    JR Z,DriveOk
    CP 13
    JP NZ,BadArgument
DriveOk:
    LD (Drive),A
    CALL ResetFcb
    LD DE,Fcb
    LD C,15
    CALL Bdos
    CP 0xFF
    JR Z,Create
    LD DE,Fcb
    LD C,16
    CALL Bdos
    LD DE,Collision
    JP Finish
Create:
    CALL ResetFcb
    LD DE,Buffer
    LD C,26
    CALL Bdos
    LD DE,Fcb
    LD C,22
    CALL Bdos
    CP 0xFF
    JP Z,Failed
    LD A,1
    LD (Opened),A
    LD HL,0
    LD (Record),HL
WriteLoop:
    CALL Pattern
    LD DE,Fcb
    LD C,21
    CALL Bdos
    OR A
    JP NZ,Failed
    CALL Advance
    JR NZ,WriteLoop
    CALL CloseFile
    JP Z,Failed
    CALL ResetFcb
    LD DE,Fcb
    LD C,15
    CALL Bdos
    CP 0xFF
    JP Z,Failed
    LD A,1
    LD (Opened),A
    LD HL,0
    LD (Record),HL
ReadLoop:
    LD DE,Fcb
    LD C,20
    CALL Bdos
    OR A
    JP NZ,Failed
    LD HL,Buffer
    LD B,0
Compare:
    CALL Expected
    CP (HL)
    JP NZ,Failed
    INC HL
    INC B
    LD A,B
    CP 128
    JR NZ,Compare
    CALL Advance
    JR NZ,ReadLoop
    CALL CloseFile
    JP Z,Failed
    LD DE,Success
    JR Finish
Failed:
    LD A,(Opened)
    OR A
    CALL NZ,CloseFile
    LD DE,Failure
Finish:
    PUSH DE
    LD DE,0x0080
    LD C,26
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
CloseFile:
    XOR A
    LD (Opened),A
    LD DE,Fcb
    LD C,16
    CALL Bdos
    CP 0xFF
    RET
Advance:
    LD HL,(Record)
    INC HL
    LD (Record),HL
    LD DE,(RecordLimit)
    OR A
    SBC HL,DE
    RET
ResetFcb:
    LD HL,Fcb
    LD B,36
ZeroFcb:
    LD (HL),0
    INC HL
    DJNZ ZeroFcb
    LD A,(Drive)
    LD (Fcb),A
    LD HL,Name
    LD DE,Fcb+1
    LD BC,11
    LDIR
    RET
Pattern:
    LD HL,Buffer
    LD B,0
Fill:
    CALL Expected
    LD (HL),A
    INC HL
    INC B
    LD A,B
    CP 128
    JR NZ,Fill
    RET
Expected:
    LD A,B
    OR A
    JR Z,RecordLow
    CP 1
    JR Z,RecordHigh
    LD A,(Record)
    XOR B
    LD D,A
    LD A,(Record+1)
    XOR D
    XOR 0x53
    RET
RecordLow:
    LD A,(Record)
    RET
RecordHigh:
    LD A,(Record+1)
    RET
Drive: DB 0
Opened: DB 0
Record: DW 0
RecordLimit: DW 65000
Name: DB "BIGFILE DAT"
Success: DB 13,10,"BIGFILE: WRITE AND VERIFY OK",13,10,"$"
Collision: DB 13,10,"BIGFILE: BIGFILE.DAT EXISTS; NO WRITES",13,10,"$"
Failure: DB 13,10,"BIGFILE: FAILED; FILE MAY REMAIN",13,10,"$"
Usage: DB 13,10,"BIGFILE: USE BIGFILE [A:/C:/M:]",13,10,"$"
Fcb: DS 36
Buffer: DS 128
