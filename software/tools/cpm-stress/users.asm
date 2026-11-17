; USERS.COM: exercise four records in each CP/M user area on a selected drive.
; Usage: USERS M: (or C:, A:); no drive argument uses the current drive.
; Refuses to start if STRESS01.DAT exists in any user area. All created
; files are retained for independent inspection and manual cleanup.
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
    LD C,32
    LD E,0xFF
    CALL Bdos
    LD (OldUser),A
    XOR A
    LD (User),A

Preflight:
    CALL SetUser
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
    LD A,(User)
    INC A
    LD (User),A
    CP 16
    JR NZ,Preflight
    XOR A
    LD (User),A
    LD C,26
    LD DE,Buffer
    CALL Bdos

NextUser:
    CALL SetUser
    CALL ResetFcb
    LD DE,Fcb
    LD C,22
    CALL Bdos
    INC A
    JP Z,Failed
    XOR A
    LD (Record),A
WriteRecord:
    CALL Pattern
    LD DE,Fcb
    LD C,21
    CALL Bdos
    OR A
    JP NZ,Failed
    LD A,(Record)
    INC A
    LD (Record),A
    CP 4
    JR NZ,WriteRecord
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
    LD A,3
    LD (Record),A
ReadRecord:
    XOR A
    LD (Fcb+34),A
    LD (Fcb+35),A
    LD A,(Record)
    LD (Fcb+33),A
    LD HL,Buffer
    LD B,128
Clear:
    LD (HL),0
    INC HL
    DJNZ Clear
    LD DE,Fcb
    LD C,33
    CALL Bdos
    OR A
    JP NZ,Failed
    LD HL,Buffer
    LD B,0
Compare:
    LD A,(User)
    XOR B
    LD D,A
    LD A,(Record)
    XOR D
    XOR 0x53
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
    JR ReadRecord
Verified:
    LD DE,Fcb
    LD C,16
    CALL Bdos
    INC A
    JP Z,Failed
    LD A,(User)
    INC A
    LD (User),A
    CP 16
    JP NZ,NextUser
    LD DE,Success
    JR Finish
Failed:
    LD DE,Failure
Finish:
    PUSH DE
    LD C,26
    LD DE,0x0080
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
SetUser:
    LD A,(User)
    LD E,A
    LD C,32
    JP Bdos
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
    RET
Pattern:
    LD HL,Buffer
    LD B,0
PatternByte:
    LD A,(User)
    XOR B
    LD D,A
    LD A,(Record)
    XOR D
    XOR 0x53
    LD (HL),A
    INC HL
    INC B
    LD A,B
    CP 128
    JR NZ,PatternByte
    RET

Drive: DB 0
OldUser: DB 0
User: DB 0
Record: DB 0
Name: DB "STRESS01DAT"
Success: DB 13,10,"USERS: 16 USERS RANDOM VERIFY OK",13,10,"$"
Collision: DB 13,10,"USERS: STRESS01.DAT EXISTS; NO WRITES",13,10,"$"
Failure: DB 13,10,"USERS: FAILED; FILE MAY REMAIN",13,10,"$"
Usage: DB 13,10,"USERS: USE USERS [DRIVE:]",13,10,"$"
Fcb: DS 36
Buffer: DS 128
