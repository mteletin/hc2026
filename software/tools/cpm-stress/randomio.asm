; RANDOMIO [A:/C:/M:] uses current drive if omitted and current user.
; Retains RANDOMIO.DAT (129 records, about 17 KB); never replaces it.
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
    LD C,32
    LD E,0xFF
    CALL Bdos
    LD (OldUser),A
    CALL ResetFcb
    LD DE,Fcb
    LD C,15
    CALL Bdos
    CP 0xFF
    JR Z,Create
    LD DE,Fcb
    LD C,16
    CALL Bdos
    CP 0xFF
    JP Z,Failed
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
    XOR A
    LD (Record),A
    LD (Replacement),A
Sequential:
    CALL Pattern
    LD DE,Fcb
    LD C,21
    CALL Bdos
    OR A
    JP NZ,Failed
    LD A,(Record)
    INC A
    LD (Record),A
    CP 129
    JR NZ,Sequential
    CALL Reopen
    JP NZ,Failed
    LD A,1
    LD (Replacement),A
    LD HL,Order
    LD (OrderPtr),HL
    LD A,4
    LD (Remaining),A
RandomWrite:
    LD HL,(OrderPtr)
    LD A,(HL)
    LD (Record),A
    INC HL
    LD (OrderPtr),HL
    CALL Pattern
    CALL SetRandom
    LD DE,Fcb
    LD C,34
    CALL Bdos
    OR A
    JP NZ,Failed
    LD A,(Remaining)
    DEC A
    LD (Remaining),A
    JR NZ,RandomWrite
    CALL Reopen
    JP NZ,Failed
    LD A,128
    LD (Record),A
RandomRead:
    CALL SetRandom
    LD DE,Fcb
    LD C,33
    CALL Bdos
    OR A
    JP NZ,Failed
    LD A,(Record)
    CP 128
    JR Z,Changed
    CP 0
    JR Z,Changed
    CP 127
    JR Z,Changed
    CP 64
    JR Z,Changed
    XOR A
    JR SetKind
Changed:
    LD A,1
SetKind:
    LD (Replacement),A
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
    LD A,(Record)
    OR A
    JR Z,Verified
    DEC A
    LD (Record),A
    JR RandomRead
Verified:
    LD DE,Fcb
    LD C,16
    CALL Bdos
    CP 0xFF
    JP Z,Failed
    LD DE,Success
    JR Finish
Failed:
    LD DE,Failure
Finish:
    PUSH DE
    LD DE,0x80
    LD C,26
    CALL Bdos
    LD A,(OldUser)
    LD E,A
    LD C,32
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
Reopen:
    LD DE,Fcb
    LD C,16
    CALL Bdos
    CP 0xFF
    JR Z,ReopenFailed
    CALL ResetFcb
    LD DE,Fcb
    LD C,15
    CALL Bdos
    CP 0xFF
    JR Z,ReopenFailed
    XOR A
    RET
ReopenFailed:
    LD A,1
    OR A
    RET
SetRandom:
    LD A,(Record)
    LD (Fcb+33),A
    XOR A
    LD (Fcb+34),A
    LD (Fcb+35),A
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
PatternLoop:
    CALL Expected
    LD (HL),A
    INC HL
    INC B
    LD A,B
    CP 128
    JR NZ,PatternLoop
    RET
Expected:
    LD A,(Record)
    XOR B
    XOR 0x53
    LD D,A
    LD A,(Replacement)
    OR A
    LD A,D
    RET Z
    XOR 0xA5
    RET
Drive: DB 0
OldUser: DB 0
Record: DB 0
Replacement: DB 0
Remaining: DB 0
OrderPtr: DW 0
Order: DB 128,0,127,64
Name: DB "RANDOMIODAT"
Success: DB 13,10,"RANDOMIO: RANDOM VERIFY OK",13,10,"$"
Collision: DB 13,10,"RANDOMIO: RANDOMIO.DAT EXISTS; NO WRITES",13,10,"$"
Failure: DB 13,10,"RANDOMIO: FAILED; FILE MAY REMAIN",13,10,"$"
Usage: DB 13,10,"RANDOMIO: USE RANDOMIO [A:/C:/M:] (CURRENT USER/DRIVE)",13,10,"$"
Fcb: DS 36
Buffer: DS 128
