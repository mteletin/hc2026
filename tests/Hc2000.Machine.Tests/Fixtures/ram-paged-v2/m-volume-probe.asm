; AC6: test-only CP/M COM for hc2000-v2-paged-m-v2.
; Raw M: BIOS drive 12 is zero-based; FCB drive 13 is one-based (M:).
; BDOS CALL 5 uses the CP/M 2.2 decimal function numbers, NOT hex:
; MAKE 22, WRITE 21, CLOSE 16, OPEN 15, READ 20, SETDMA 26.
; 32,769 records x 128 B = 4 MiB + 128 B; record r byte i = (r+i)&255.
; SPT is in 128-byte logical records: BSH 6 means 8 KiB blocks.
ORG 0x0100
    LD BC,0x7CFD
    IN A,(C)
    LD (PortControl),A
    CP 0x40
    JP NZ,NG
    LD BC,0x7DFD
    IN A,(C)
    LD (PortGroup),A
    CP 2
    JP NC,NG

    ; Initialise the single 8 KiB directory block through the BIOS.
    LD A,0x46
    LD (Stage),A
    LD C,12
    CALL 0xD31B
    LD BC,0x0080
    CALL 0xD324
    LD HL,0x0080
    LD (HL),0xE5
    LD DE,0x0081
    LD BC,127
    LDIR
    LD BC,0
    CALL 0xD31E
    XOR A
    LD (Sector),A
FormatDirectory:
    LD A,(Sector)
    LD C,A
    LD B,0
    CALL 0xD321
    LD C,1
    CALL 0xD32A
    OR A
    JP NZ,NG
    LD A,(Sector)
    INC A
    LD (Sector),A
    CP 64
    JR NZ,FormatDirectory

    LD DE,0x0080
    LD C,26
    CALL 5
    LD A,0x43
    LD (Stage),A
    LD DE,FCB
    LD C,22
    CALL 5
    CP 0xFF
    JP Z,NG

    LD A,0x57
    LD (Stage),A
    LD HL,0
    LD (Record),HL
WriteLoop:
    LD HL,(Record)
    LD A,L
    LD HL,0x0080
    LD B,128
WriteFill:
    LD (HL),A
    INC A
    INC HL
    DJNZ WriteFill
    LD DE,FCB
    LD C,21
    CALL 5
    OR A
    JP NZ,NG
    LD HL,(Record)
    INC HL
    LD (Record),HL
    LD DE,(RecordLimit)
    OR A
    SBC HL,DE
    JR NZ,WriteLoop

    LD A,0x4B
    LD (Stage),A
    LD DE,FCB
    LD C,16
    CALL 5
    CP 0xFF
    JP Z,NG

    ; Reopen from extent/record zero; OPEN does not reset these for us.
    LD HL,FCB+12
    LD B,24
    XOR A
ClearFcb:
    LD (HL),A
    INC HL
    DJNZ ClearFcb
    LD A,0x4F
    LD (Stage),A
    LD DE,FCB
    LD C,15
    CALL 5
    CP 0xFF
    JP Z,NG

    LD A,0x52
    LD (Stage),A
    LD HL,0
    LD (Record),HL
ReadLoop:
    LD DE,FCB
    LD C,20
    CALL 5
    OR A
    JP NZ,NG
    LD HL,(Record)
    LD A,L
    LD HL,0x0080
    LD B,128
ReadCheck:
    CP (HL)
    JP NZ,NG
    INC A
    INC HL
    DJNZ ReadCheck
    LD HL,(Record)
    INC HL
    LD (Record),HL
    LD DE,(RecordLimit)
    OR A
    SBC HL,DE
    JR NZ,ReadLoop

    LD A,0x58
    LD (Stage),A
    LD DE,FCB
    LD C,16
    CALL 5
    CP 0xFF
    JP Z,NG
    LD DE,OkMsg
    JR Report
NG:
    LD (Error),A
    LD DE,NgMsg
Report:
    LD C,9
    CALL 5
    JP 0
FCB:
    DB 13
    DB "BIGFILE "
    DB "BIN"
    DS 24,0
PortControl:
    DB 0
PortGroup:
    DB 0
RecordLimit:
    DW 32769
Record:
    DW 0
Error:
    DB 0
Sector:
    DB 0
OkMsg:
    DB "M2 OK",13,10,0x24
NgMsg:
    DB "M2 NG "
Stage:
    DB 0x53,13,10,0x24
