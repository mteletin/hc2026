; DRVSEL.COM: show A, B, C, D, E, F and M, or select one of them.
; A letter outside that set is refused with the reason, and the logged
; drive is left unchanged. A listing probes SELDSK, except the floppy that
; is not the logged drive: the stock A/B selector warm-boots on a not-ready
; floppy instead of returning HL=0, so that probe is reported NOT READY.
ORG 0x0100

Bdos   EQU 5
SelDsk EQU 0xD31B
RowCh  EQU RowTxt

    LD SP,0x7EF0
    LD A,(0x0080)
    OR A
    JP Z,List
    LD HL,0x0081
    LD B,A
Skip:
    LD A,(HL)
    CP 32
    JR NZ,Got
    INC HL
    DJNZ Skip
    JP List
Got:
    LD (BadCh),A
    CP 65
    JR C,Refuse
    CP 71
    JR C,Letter
    CP 77
    JR NZ,Refuse
    LD A,12
    JR Probe
Letter:
    SUB 65
Probe:
    LD (Drv),A
    LD C,A
    CALL SelDsk
    LD A,H
    OR L
    JR Z,NotReady
    LD A,(Drv)
    LD E,A
    LD C,14
    CALL Bdos
    LD DE,Sel
    CALL Print
    LD A,(BadCh)
    LD E,A
    LD C,2
    CALL Bdos
    LD DE,CrLf
    CALL Print
    RET
NotReady:
    LD DE,Nr
    CALL Print
    LD A,(BadCh)
    LD E,A
    LD C,2
    CALL Bdos
    LD DE,NrTail
    CALL Print
    RET
Refuse:
    LD DE,Why
    CALL Print
    LD A,(BadCh)
    LD E,A
    LD C,2
    CALL Bdos
    LD DE,WhyTail
    CALL Print
    RET

List:
    LD HL,Names
    LD B,7
Row:
    PUSH BC
    LD A,(HL)
    INC HL
    LD (RowCh),A
    LD C,(HL)
    INC HL
    PUSH HL
    LD A,C
    CP 2
    JR NC,ListProbe
    LD A,(0x0004)
    AND 0x0F
    CP C
    JR Z,ListProbe
    LD HL,0
    JR ListSkip
ListProbe:
    CALL SelDsk
ListSkip:
    LD A,H
    OR L
    LD DE,Ready
    JR NZ,Say
    LD DE,Down
Say:
    PUSH DE
    LD DE,RowTxt
    CALL Print
    POP DE
    CALL Print
    POP HL
    POP BC
    DJNZ Row
    RET

Print:
    LD C,9
    CALL Bdos
    RET

Drv:
    DB 0
BadCh:
    DB 0
Names:
    DB 65,0,66,1,67,2,68,3,69,4,70,5,77,12
RowTxt:
    DB 88,58,32,36
Ready:
    DB 82,69,65,68,89,13,10,36
Down:
    DB 78,79,84,32,82,69,65,68,89,13,10,36
Sel:
    DB 68,82,86,83,69,76,58,32,36
Nr:
    DB 68,82,86,83,69,76,58,32,36
NrTail:
    DB 58,32,78,79,84,32,82,69,65,68,89,13,10,36
Why:
    DB 68,82,86,83,69,76,58,32,36
WhyTail:
    DB 32,79,85,84,32,79,70,32,82,65,78,71,69,32,40,118,97,108,105,100,58,32,65,32,66,32,67,32,68,32,69,32,70,32,77,41,13,10,36
CrLf:
    DB 13,10,36

    ASSERT $ < 0x8000
