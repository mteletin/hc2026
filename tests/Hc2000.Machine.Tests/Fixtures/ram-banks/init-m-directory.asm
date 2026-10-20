; Test-only guest initializer for the M: RAM disk. It runs under CP/M and
; writes 64 real 128-byte records of E5h through the BIOS vectors for drive 12.
; This is not a synthetic RAM-bank fixture: all writes execute on the Z80 and
; pass through the M: BIOS into the profile-backed bank window.
ORG 0x0100
    LD C,0x0C
    CALL 0xD31B ; SELDSK M:
    LD BC,0x0080
    CALL 0xD324 ; SETDMA to the command-tail area, no longer needed by CP/M
    LD HL,0x0080
    LD (HL),0xE5
    LD DE,0x0081
    LD BC,0x007F
    LDIR
    LD BC,0x0000
    CALL 0xD31E ; SETTRK 0
    XOR A
    LD (Sector),A
FormatDirectory:
    LD A,(Sector)
    LD C,A
    LD B,0
    CALL 0xD321 ; SETSEC
    LD C,1
    CALL 0xD32A ; WRITE directory record
    LD A,(Sector)
    INC A
    LD (Sector),A
    CP 64
    JR NZ,FormatDirectory
    JP 0x0000 ; warm boot to the CP/M prompt
Sector:
    DB 0
