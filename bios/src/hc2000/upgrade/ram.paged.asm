; Sole source-stage M firmware, hc2000-m-base-view-v1 / base-view v3.
; NOT runtime qualified. Builder MUST reject code beyond DE7F (no spill).
; Retain original D300-DB14 except these standard vector operands.
; SETDMA D324 remains original D539: GLOBAL D875, not per drive.
ORG 0xD301
    DW MCold
ORG 0xD304
    DW MWarm
ORG 0xD319
    DW MHome
ORG 0xD31C
    DW MSelect
ORG 0xD31F
    DW MTrack
ORG 0xD322
    DW MSector
ORG 0xD328
    DW MRead
ORG 0xD32B
    DW MWrite

Selected EQU 0xDE80
Operation EQU 0xDE81
Track EQU 0xDE82
Sector EQU 0xDE84
Dma EQU 0xDE86
SavedSP EQU 0xDE88
Bank EQU 0xDE8A
Iff EQU 0xDE8C
Gate EQU 0xDE8D
Pending EQU 0xDE8E
Status EQU 0xDE8F
Control EQU 0xDE90
Window EQU 0xDE91
Bounce EQU 0xDBB4

ORG 0xDB15
DirBuf EQU 0xDC34
MDPH:
    DW 0,0,0,0,DirBuf,MDPB,0,0xDB34
MDPB:
    DW 128
    DB 5,31,1
    DW 1023,255
    DB 0xC0,0
    DW 0,0
; ALV, bounce, directory, scratch and stack are private zero-initialized
; extension storage, not flattened holes in the retained original BIOS.

ORG 0xDCB4
MCodeStart:
MCold:
    LD HL,0xD3A6
    JR MInit
MWarm:
    LD HL,0xD3C1
MInit:
    LD BC,0x7CFD
    XOR A
    OUT (C),A
    LD (Selected),A
    LD (Pending),A
    LD (Track),A
    LD (Track+1),A
    LD (Sector),A
    LD (Sector+1),A
    JP (HL)
MHome:
    LD HL,0
    LD (Track),HL
    JP 0xD52D
MTrack:
    LD (Track),BC
    JP 0xD52F
MSector:
    LD (Sector),BC
    JP 0xD534
MSelect:
    LD A,C
    CP 12
    JR Z,MSelectM
    CP 2
    JR NC,MInvalid
    XOR A
    LD (Selected),A
    JP 0xD492
MSelectM:
    LD A,1
    LD (Selected),A
    LD HL,MDPH
    RET
MInvalid:
    LD HL,0
    RET
MWrite:
    LD A,(Selected)
    OR A
    JP Z,0xD53E
    LD E,1
    JR MRequest
MRead:
    LD A,(Selected)
    OR A
    JP Z,0xD542
    LD E,0
MRequest:
    ; IFF2 captured before DI; preflight runs on ORIGINAL caller stack.
    ; PUSH AF is balanced on every refusal/admission, preserving parity.
    LD A,I
    DI
    PUSH AF
    LD A,(Pending)
    OR A
    JP NZ,MReject
    LD BC,0x7CFD
    IN A,(C)
    CP 0x40 ; exact presence, BASE, unlocked, no reserved bits
    JP NZ,MReject
    LD B,0x7D
    IN A,(C)
    OR A
    JP NZ,MReject
    LD A,(Track+1)
    OR A
    JP NZ,MReject
    LD HL,(Sector)
    LD A,H
    OR A
    JP NZ,MReject
    BIT 7,L
    JP NZ,MReject
    ; Trusted CP/M BIOS BASE entry: original 7E setup makes low RAM writable.
    ; No caller data in OS C500-FFFF or page-zero control 0000-007F, except
    ; this BIOS's own directory buffer, which BDOS is required to use.
    LD HL,(0xD875) ; capture GLOBAL DMA exactly once for this request
    LD (Dma),HL
    LD A,H
    OR A
    JR NZ,MDmaLowOK
    BIT 7,L
    JP Z,MReject
MDmaLowOK:
    LD BC,127
    ADD HL,BC
    JP C,MReject
    LD A,H
    CP 0xC5
    JR C,MDmaSpanOK
    ; BDOS directory I/O sets the global DMA to this BIOS's own directory
    ; buffer. Admit that exact 128-byte span (ends one below the code) and
    ; keep refusing every other caller span in the OS tail. E is the live
    ; operation, so the compare uses BC.
    LD BC,(Dma)
    LD A,B
    CP HIGH(DirBuf)
    JP NZ,MReject
    LD A,C
    CP LOW(DirBuf)
    JP NZ,MReject
MDmaSpanOK:
    ; Reject overlap with saved IFF and caller return word [SP,SP+4).
    ; HL = last DMA byte; stack must not wrap.
    LD (Window),HL ; temporary end, only after nested-entry refusal
    LD HL,0
    ADD HL,SP
    LD BC,4
    ADD HL,BC
    JP C,MReject
    LD BC,(Dma)
    OR A
    SBC HL,BC ; SP+4-DMA
    JR C,MDmaStackOK
    JR Z,MDmaStackOK
    LD HL,0
    ADD HL,SP
    LD BC,(Window)
    OR A
    SBC HL,BC ; SP-last; positive => no overlap
    JP C,MReject
    JP Z,MReject
MDmaStackOK:
    LD A,E
    LD (Operation),A
    POP AF
    LD A,0
    JP PO,MIffSaved
    INC A
MIffSaved:
    LD (Iff),A
    LD (SavedSP),SP
    LD SP,0xDF00
MPrivateStart:
    ; No PUSH, recursion, ROM/FDC/console or interrupt entry here.
    ; Each CALL below has depth one (2 bytes), helper has no CALL/PUSH.
    LD A,1
    LD (Pending),A
    LD (Status),A
    XOR A
    LD (Gate),A
    LD (Bank+1),A
    LD BC,0x7EFD
    IN A,(C)
    LD (Bank),A
    LD B,0x7F
    IN A,(C)
    LD (Control),A
    LD A,(Operation)
    OR A
    JR Z,MMap
    LD HL,(Dma)
    LD DE,Bounce
    CALL MCopy
MMap:
    LD HL,(Sector)
    ; sector*128 + 8000, no wrap for admitted sector0..127.
    LD A,L
    RRCA
    LD H,A
    AND 0x80
    LD L,A
    LD A,H
    AND 0x3F
    OR 0x80
    LD H,A
    LD (Window),HL
    LD BC,0x7EFD
    LD A,(Track)
    OUT (C),A
    IN A,(C)
    LD HL,Track
    CP (HL)
    JR NZ,MRestore
    LD B,0x7C
    LD A,1
    OUT (C),A
    IN A,(C)
    CP 0x41
    JR NZ,MRestore
    LD A,(Operation)
    OR A
    LD HL,(Window)
    LD DE,Bounce
    JR Z,MPageCopy
    EX DE,HL
MPageCopy:
    CALL MCopy
    XOR A
    LD (Status),A
MRestore:
    LD BC,0x7CFD
    XOR A
    OUT (C),A
    IN A,(C)
    CP 0x40
    JR Z,MBaseRestored
    LD A,1
    LD (Status),A
MBaseRestored:
    LD B,0x7E
    LD A,(Bank)
    OUT (C),A
    LD A,(Status)
    OR A
    JR NZ,MExit
    LD A,(Operation)
    OR A
    JR NZ,MExit
    LD HL,Bounce
    LD DE,(Dma)
    CALL MCopy
MExit:
    XOR A
    LD (Pending),A
    LD SP,(SavedSP)
MPrivateEnd:
    LD A,(Iff)
    OR A
    LD A,(Status)
    RET Z
    EI
    RET ; EI delay: caller return completes before IRQ acceptance
MReject:
    POP AF
    LD A,1
    RET PO
    EI
    RET
MCopy:
    LD BC,128
    LDIR
    RET
MCodeEnd:
; No ORG-to-scratch here: an oversized candidate remains visibly oversized.
; Builder records actual segments/symbols then fails closed before media output.
