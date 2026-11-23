; Linked into the stable BIOS page. No page ownership is implied.
; CALL MTransfer with DE = caller-authorized physical page 0..1023,
; HL = aligned record address 8000..bf80, A = 0 read / 1 write.
; Read puts 128 bytes in MBuffer; write takes them from MBuffer (7e00).
; Returns A=0 success, A=1 failure; clobbers AF,BC,DE,HL. IFF restored.
; Caller must ensure exclusive ownership of the page, a writable resident
; allocation, CP/M base view and no NMI/reentry. Caller stack and return word
; must be outside 7800-bfff. No BDOS, BCB or DMA assumptions are made.
; A gate or latch readback failure is an error; a hardware failure to unmap
; cannot be repaired here. Nothing establishes ownership of pages 882..1023.
MBuffer EQU 0x7E00
MTransfer:
    LD (MOp),A
    LD (MPage),DE
    LD (MAddr),HL
    LD A,I
    DI
    PUSH AF
    LD A,(MOp)
    CP 2
    JP NC,MReject
    LD A,D
    CP 4
    JP NC,MReject
    LD A,H
    CP 0x80
    JP C,MReject
    CP 0xc0
    JP NC,MReject
    LD A,L
    AND 0x7f
    JP NZ,MReject
    LD BC,0x7cfd
    IN A,(C)
    CP 0x40
    JP NZ,MReject
    POP AF
    LD A,0
    JP PO,MIffDone
    INC A
MIffDone:
    LD (MIff),A
    LD (MSp),SP
    LD SP,MStackTop
    LD A,1
    LD (MStatus),A
    LD BC,0x7efd
    IN A,(C)
    LD (MOldLow),A
    LD B,0x7d
    IN A,(C)
    LD (MOldHigh),A
    LD B,0x7e
    LD A,(MPage)
    OUT (C),A
    IN A,(C)
    LD HL,MPage
    CP (HL)
    JR NZ,MRestore
    LD B,0x7d
    LD A,(MPage+1)
    OUT (C),A
    IN A,(C)
    LD HL,MPage+1
    CP (HL)
    JR NZ,MRestore
    LD B,0x7c
    LD A,1
    OUT (C),A
    IN A,(C)
    CP 0x41
    JR NZ,MRestore
    LD HL,(MAddr)
    LD DE,MBuffer
    LD A,(MOp)
    OR A
    JR Z,MCopy
    EX DE,HL
MCopy:
    LD BC,128
    LDIR
    XOR A
    LD (MStatus),A
MRestore:
    LD BC,0x7cfd
    XOR A
    OUT (C),A
    IN A,(C)
    CP 0x40
    JR Z,MRestoreBanks
    LD A,1
    LD (MStatus),A
MRestoreBanks:
    LD B,0x7e
    LD A,(MOldLow)
    OUT (C),A
    IN A,(C)
    LD HL,MOldLow
    CP (HL)
    JR Z,MRestoreHigh
    LD A,1
    LD (MStatus),A
MRestoreHigh:
    LD B,0x7d
    LD A,(MOldHigh)
    OUT (C),A
    IN A,(C)
    LD HL,MOldHigh
    CP (HL)
    JR Z,MExit
    LD A,1
    LD (MStatus),A
MExit:
    LD SP,(MSp)
    LD A,(MIff)
    OR A
    LD A,(MStatus)
    RET Z
    EI
    RET
MReject:
    POP AF
    LD A,1
    RET PO
    EI
    RET
MOp: DB 0
MPage: DW 0
MAddr: DW 0
MSp: DW 0
MIff: DB 0
MStatus: DB 0
MOldLow: DB 0
MOldHigh: DB 0
; Leave DF00 and up to the console ROM. This stack is private to the copy.
MStackBottom: DS 32
MStackTop:
ASSERT $ <= 0xDF00
