; RAMSTAT.COM: report whether the paged M: RAM drive is present and how
; many of its 1024 blocks are free. Free is a zero bit in the BIOS ALV.
ORG 0x0100

Bdos    EQU 5
Alv     EQU 0xDB34
Present EQU 0x40

    LD SP,0x7EF0
    LD BC,0x7CFD
    IN A,(C)
    CP Present
    JR Z,HaveM
    LD DE,Absent
    CALL Print
    RET
HaveM:
    LD DE,Here
    CALL Print
    LD HL,Alv
    LD BC,0
    LD D,128
Byte:
    LD A,(HL)
    LD E,8
Bit:
    RRA
    JR C,Used
    INC BC
Used:
    DEC E
    JR NZ,Bit
    INC HL
    DEC D
    JR NZ,Byte
    LD DE,Free
    CALL Print
    CALL DecBC
    LD DE,Of
    CALL Print
    RET

; Print BC as decimal, no leading zeros. BC is 0..1024.
DecBC:
    LD HL,1000
    CALL Digit
    LD HL,100
    CALL Digit
    LD HL,10
    CALL Digit
    LD A,C
    ADD A,48
    LD E,A
    LD C,2
    CALL Bdos
    RET
; HL = divisor (never zero), BC = dividend (0..1024). Every digit here is 0..9.
; On return BC holds the remainder and HL the divisor.
Digit:
    PUSH HL
    POP DE
    PUSH BC
    POP HL
    LD A,255
DigSub:
    INC A
    SBC HL,DE
    JR NC,DigSub
    JR Z,DigZero
    OR A
    ADD HL,DE
    JR DigStore
DigZero:
    INC A
    LD HL,0
    JR DigStore
DigStore:
    LD (Dig),A
    PUSH HL
    POP BC
    PUSH DE
    POP HL
    PUSH BC
    PUSH HL
    LD A,(Dig)
    OR A
    JR NZ,DigHas
    LD A,(Lead)
    OR A
    JR NZ,DigHas
    POP HL
    POP BC
    RET
DigHas:
    LD A,1
    LD (Lead),A
    LD A,(Dig)
    ADD A,48
    LD E,A
    LD C,2
    CALL Bdos
    POP HL
    POP BC
    RET

Print:
    LD C,9
    CALL Bdos
    RET

Lead:
    DB 0
Dig:
    DB 0
Here:
    DB 77,58,32,80,82,69,83,69,78,84,13,10,36
Absent:
    DB 77,58,32,65,66,83,69,78,84,13,10,36
Free:
    DB 77,58,32,70,82,69,69,32,36
Of:
    DB 47,49,48,50,52,13,10,36

    ASSERT $ < 0x8000
