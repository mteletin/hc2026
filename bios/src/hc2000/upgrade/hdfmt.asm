; HDFMT.COM: initialise one HDD volume, C: to F: only.
; Requires the installed HDD resident; an unavailable drive is refused.
; The format path follows the BIOS DPH to its published logical DPB.
ORG 0x0100

Bdos   EQU 5
SelDsk EQU 0xD31B
SetTrk EQU 0xD31E
SetSec EQU 0xD321
SetDma EQU 0xD324
BiosWr EQU 0xD32A
DmaBuf EQU 0x0400

    LD SP,0x7EF0
    LD A,(0x0080)
    OR A
    JP Z,Need
    LD HL,0x0081
    LD B,A
Skip:
    LD A,(HL)
    CP 32
    JR NZ,Got
    INC HL
    DJNZ Skip
    JP Need
Got:
    LD (Arg),A
    CP 67
    JR C,Refuse
    CP 71
    JR NC,Refuse
    SUB 65
    LD (Drv),A
    LD C,A
    CALL SelDsk
    LD A,H
    OR L
    JP Z,NotReady
    JR FormatLoop

Refuse:
    LD DE,Why
    CALL Print
    LD A,(Arg)
    LD E,A
    LD C,2
    CALL Bdos
    LD DE,WhyTail
    CALL Print
    RET
NotReady:
    LD DE,Nr
    CALL Print
    LD A,(Arg)
    LD E,A
    LD C,2
    CALL Bdos
    LD DE,NrTail
    CALL Print
    RET

FormatLoop:
    ; SELDSK returns a DPH; its word at +10 points to the DPB.
    LD DE,10
    ADD HL,DE
    LD E,(HL)
    INC HL
    LD D,(HL)
    EX DE,HL
    ; Logical large-drive DPB: SPT at +0/+1, DRM (entries - 1) at +7/+8.
    ; After reading SPT, HL is at base+1, so add 6 to land on DRM low.
    LD E,(HL)
    INC HL
    LD D,(HL)
    LD (Spt),DE
    LD DE,6
    ADD HL,DE
    LD E,(HL)
    INC HL
    LD D,(HL)
    INC DE
    SRL D
    RR E
    SRL D
    RR E
    LD (DirN),DE
    ; OFF at +13/+14: preserve reserved tracks, if any.
    LD DE,5
    ADD HL,DE
    LD E,(HL)
    INC HL
    LD D,(HL)
    LD (Offs),DE
    LD HL,DmaBuf
    LD DE,DmaBuf+1
    LD BC,127
    LD (HL),0xE5
    LDIR
    LD HL,0
Sect:
    LD (Cur),HL
    LD BC,(Spt)
    CALL Div
    LD HL,(Offs)
    ADD HL,BC
    LD B,H
    LD C,L
    CALL SetTrk
    LD BC,(Rem)
    CALL SetSec
    LD BC,DmaBuf
    CALL SetDma
    LD C,1
    CALL BiosWr
    OR A
    JR NZ,WriteFailed
    LD HL,(Cur)
    INC HL
    LD DE,(DirN)
    OR A
    SBC HL,DE
    ADD HL,DE
    JR C,Sect

    LD C,13
    CALL Bdos
    LD A,(Drv)
    LD E,A
    LD C,14
    CALL Bdos
    LD C,14
    LD E,0
    CALL Bdos
    LD DE,Done
    CALL Print
    RET

WriteFailed:
    LD DE,WriteError
    CALL Print
    RET

Need:
    LD DE,NeedMsg
    CALL Print
    RET

; (Cur) / BC (sector count, never zero): BC = track, (Rem) = sector.
Div:
    LD HL,(Cur)
    LD DE,0
DivStep:
    OR A
    SBC HL,BC
    JR C,DivBack
    INC DE
    LD A,H
    OR L
    JR NZ,DivStep
    JR DivOut
DivBack:
    ADD HL,BC
DivOut:
    LD (Rem),HL
    LD B,D
    LD C,E
    RET

Print:
    LD C,9
    CALL Bdos
    RET

Arg:
    DB 0
Drv:
    DB 0
Spt:
    DW 0
Offs:
    DW 0
DirN:
    DW 0
Cur:
    DW 0
Rem:
    DW 0
Done:
    DB 72,68,70,77,84,58,32,70,79,82,77,65,84,84,69,68,13,10,36
NeedMsg:
    DB 72,68,70,77,84,58,32,78,69,69,68,32,68,82,73,86,69,32,67,45,70,13,10,36
Why:
    DB 72,68,70,77,84,58,32,36
WhyTail:
    DB 32,79,85,84,32,79,70,32,82,65,78,71,69,32,40,118,97,108,105,100,58,32,67,32,68,32,69,32,70,41,13,10,36
Nr:
    DB 72,68,70,77,84,58,32,36
NrTail:
    DB 32,78,79,84,32,82,69,65,68,89,32,40,72,68,68,32,82,69,83,73,68,69,78,84,32,78,79,84,32,82,69,65,68,89,41,13,10,36
WriteError:
    DB "HDFMT: WRITE FAILED",13,10,36

    ASSERT $ <= DmaBuf
