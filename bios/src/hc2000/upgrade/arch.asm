; ARCH.COM tape engine. One edge call site so every half-pulse shares one path.
; Port 0xFE bit 3 is MIC; MIC low is held as EAR bit 6 high (0x40).
; Burns are calibrated against the ROM export windows (2168/667/735/855/1710 ref ticks).
ORG 0x0100

Bdos         EQU 5
; Burns calibrated so every pulse lands in the ROM export windows
; (2168 pilot / 667 sync1 / 735 sync2 / 855 zero / 1710 one ref ticks, +/-10%):
; width = ctx_overhead + 26 cpuT per burn iteration (measured, 3.5 MHz ref).
PilotIters   EQU 78
Sync1Iters   EQU 22
Sync2Iters   EQU 19
ZeroIters    EQU 28
OneIters     EQU 60
PilotLo      EQU 24
PilotHi      EQU 50
BitTh        EQU 18

    JP Start

; HL = block bytes, BC = length. DI across the pulses, EI before the pause.
; HL stays the live data pointer and BC the live length: the burn value goes
; in DE, never HL, so the next byte is read from the right address. The
; next-byte fetch sits in the last bit's within-bit gap (between its two
; OUTs), so the byte-boundary gap carries only the prelude, and every zero
; half-pulse stays inside 855 +/-10%.
WriteBlock:
    DI
    XOR A
    LD (FeValue),A
    LD DE,PilotIters
    LD (WordBurn),DE
    PUSH HL
    PUSH BC
    LD BC,(PilotCount)
    CALL Run
    LD DE,Sync1Iters
    LD (WordBurn),DE
    LD BC,1
    CALL Run
    LD DE,Sync2Iters
    LD (WordBurn),DE
    LD BC,1
    CALL Run
    POP BC
    POP HL
    LD A,B
    OR C
    JR Z,WbNext
    LD A,(HL)
    INC HL
    DEC BC
    LD (WbData),A
    LD A,8
    LD (WbBits),A
WbBit:
    LD A,(WbData)
    RLCA
    LD (WbData),A
    LD DE,ZeroIters
    JR NC,WbSet
    LD DE,OneIters
WbSet:
    LD (WordBurn),DE
    CALL Edge
    LD A,(WbBits)
    DEC A
    LD (WbBits),A
    JR NZ,WbSecond
    LD A,B
    OR C
    JR Z,WbLastSecond
    LD A,(HL)
    INC HL
    DEC BC
    LD (WbData),A
    LD A,8
    LD (WbBits),A
WbSecond:
    CALL Edge
    JR WbBit
WbLastSecond:
    CALL Edge
WbNext:
    LD DE,ZeroIters
    LD (WordBurn),DE
    CALL Edge
    XOR A
    LD (FeValue),A
    OUT (0xFE),A
    EI
    LD BC,(PauseCount)
    LD A,B
    OR C
    JR Z,WbDone
WbPause:
    DEC BC
    LD A,B
    OR C
    JR NZ,WbPause
WbDone:
    RET

; BC = edge count. (WordBurn) is the burn. Every edge goes through Edge.
Run:
RunLoop:
    CALL Edge
    DEC BC
    LD A,B
    OR C
    JR NZ,RunLoop
    RET

; OUT the latched MIC value, toggle it, burn (WordBurn) iterations.
; Clobbers A and F only; BC is preserved so callers keep their counters.
Edge:
    PUSH BC
    LD A,(FeValue)
    OUT (0xFE),A
    XOR 8
    LD (FeValue),A
    LD BC,(WordBurn)
EdgeBurn:
    DEC BC
    LD A,B
    OR C
    JR NZ,EdgeBurn
    POP BC
    RET

; HL = destination, BC = max length.
; A = 0 ok, 1 no block, 2 bad checksum or partial byte, 3 oversized. BC = bytes stored.
ReadBlock:
    DI
    LD (Dest),HL
    LD (MaxLen),BC
    LD HL,0
    LD (Got),HL
    XOR A
    LD (XorSum),A
    IN A,(0xFE)
    AND 0x40
    LD (LastEar),A
    LD A,0
    LD (Pilots),A
RbFind:
    CALL WidthEdge
    LD A,B
    OR A
    JP Z,RbGap
    CP PilotLo
    JP C,RbReset
    CP PilotHi
    JP NC,RbReset
    LD A,(Pilots)
    INC A
    LD (Pilots),A
    CP 16
    JR C,RbFind
RbSync:
    CALL WidthEdge
    LD A,B
    OR A
    JR Z,RbNone
    CP PilotLo
    JR NC,RbSync
    CALL WidthEdge
    XOR A
    LD (Acc),A
    LD A,8
    LD (BitsLeft),A
RbBit:
    CALL WidthEdge
    LD A,B
    OR A
    JR Z,RbEnd
    CP BitTh
    JR C,RbZero
    SCF
    JR RbShift
RbZero:
    OR A
RbShift:
    LD A,(Acc)
    RLA
    LD (Acc),A
    CALL WidthEdge
    LD A,B
    OR A
    JR Z,RbEnd
    LD A,(BitsLeft)
    DEC A
    LD (BitsLeft),A
    JR NZ,RbBit
    LD HL,(Got)
    LD DE,(MaxLen)
    OR A
    SBC HL,DE
    JR NC,RbTooLong
    LD HL,(Dest)
    LD A,(Acc)
    LD (HL),A
    LD C,A
    LD A,(XorSum)
    XOR C
    LD (XorSum),A
    INC HL
    LD (Dest),HL
    LD HL,(Got)
    INC HL
    LD (Got),HL
    XOR A
    LD (Acc),A
    LD A,8
    LD (BitsLeft),A
    JP RbBit
RbReset:
    XOR A
    LD (Pilots),A
    JP RbFind
RbGap:
    LD HL,(SearchLeft)
    DEC HL
    LD (SearchLeft),HL
    LD A,H
    OR L
    JP NZ,RbFind
RbNone:
    LD A,1
    LD BC,0
    EI
    RET
RbEnd:
    LD HL,(Got)
    LD A,H
    OR L
    JR Z,RbNone
    LD A,(BitsLeft)
    CP 8
    JR NZ,RbPartial
    LD A,(XorSum)
    OR A
    LD A,2
    JR NZ,RbBad
    XOR A
RbBad:
    LD BC,(Got)
    EI
    RET
RbPartial:
    LD A,2
    JR RbBad
RbTooLong:
    LD A,3
    JR RbBad

; B = iteration count until EAR bit 6 changes, or 0 on timeout.
; Clobbers A, C, D, H.
WidthEdge:
    LD D,0
    LD H,0
WeLoop:
    INC D
    IN A,(0xFE)
    AND 0x40
    LD C,A
    LD A,(LastEar)
    CP C
    JR NZ,WeGot
    DEC H
    JR NZ,WeLoop
    LD B,0
    RET
WeGot:
    LD A,C
    LD (LastEar),A
    LD B,D
    RET

; The CCP passes the command tail: byte length at 0x80, characters from 0x81.
; The first non-space character chooses the command.
Start:
    LD C,32
    LD E,0xFF
    CALL Bdos
    LD (UserOrig),A
    LD A,(0x80)
    OR A
    JR Z,Usg
    LD HL,0x81
    LD B,A
Spc:
    LD A,(HL)
    CP 0x20
    JR NZ,Cmd
    INC HL
    DEC B
    JR NZ,Spc
    JR Usg
Cmd:
    AND 0x5F
    CP 0x43
    JR Z,DoCreate
    CP 0x4C
    JR Z,DoListing
    CP 0x58
    JR Z,DoExtract
    JR Usg
DoCreate:
    CALL Create
    JR RestoreUser
DoListing:
    CALL DoList
    JR RestoreUser
DoExtract:
    CALL Extract
RestoreUser:
    LD A,(UserOrig)
    LD E,A
    LD C,32
    CALL Bdos
    RET
Usg:
    LD DE,Usage
    LD C,9
    CALL Bdos
    RET

; L: one line per file, "U<user> STEM.EXT". A block that is neither a
; header nor a 0xFF data block ends the listing.
ListBuf    EQU 0x4000
MaxBlock   EQU 16386
SearchFull EQU 1000
SearchInitial EQU 3000

DoList:
    CALL ParseName
    LD DE,SearchInitial
    LD (SearchLeft),DE
    JP LSearch
LNext:
    LD DE,SearchFull
    LD (SearchLeft),DE
LSearch:
    LD HL,ListBuf
    LD BC,MaxBlock
    CALL ReadBlock
    OR A
    JP NZ,LEnd
    LD A,(ListBuf)
    OR A
    JP NZ,LEnd
    LD A,(ListBuf+1)
    CP 0x03
    JP NZ,LEnd
    ; Header: keep the 32-bit data length, then read the metadata block.
    LD A,(ListBuf+14)
    LD (LRem),A
    LD A,(ListBuf+15)
    LD (LRem+1),A
    LD A,(ListBuf+16)
    LD (LRem+2),A
    LD A,(ListBuf+17)
    LD (LRem+3),A
    LD DE,SearchFull
    LD (SearchLeft),DE
    LD HL,ListBuf
    LD BC,MaxBlock
    CALL ReadBlock
    OR A
    JP NZ,LEnd
    LD A,(ListBuf)
    CP 0xFF
    JP NZ,LEnd
    ; Filter: an explicit user or a named pattern; off for bare "L".
    LD A,(UserReq)
    CP 0xFF
    JR Z,LNoUsr
    LD C,A
    LD A,(ListBuf+1)
    CP C
    JP NZ,LSkip
LNoUsr:
    LD A,(NameSet)
    OR A
    JR Z,LPrint
    LD HL,Name11
    LD DE,ListBuf+2
    CALL Match
    OR A
    JP NZ,LSkip
LPrint:
    ; Line: "U" + user + " " + trimmed stem + "." + trimmed ext.
    LD DE,LineBuf
    LD A,0x55
    LD (DE),A
    INC DE
    LD A,(ListBuf+1)
    CP 10
    JR C,U1d
    LD A,0x31
    LD (DE),A
    INC DE
    LD A,(ListBuf+1)
    SUB 10
U1d:
    ADD A,0x30
    LD (DE),A
    INC DE
    LD A,0x20
    LD (DE),A
    INC DE
    ; Eight-byte stem: copy until a space.
    LD HL,ListBuf+2
    LD B,8
StemCp:
    LD A,(HL)
    CP 0x20
    JR Z,Ext
    LD (DE),A
    INC DE
StemAdv:
    INC HL
    DJNZ StemCp
    JR Ext
Ext:
    LD HL,ListBuf+10
    LD A,(HL)
    CP 0x20
    JR Z,LineEnd
    LD A,0x2E
    LD (DE),A
    INC DE
    LD B,3
ExtCp:
    LD A,(HL)
    CP 0x20
    JR Z,LineEnd
    LD (DE),A
    INC DE
    INC HL
    DJNZ ExtCp
    JR LineEnd
LineEnd:
    LD A,13
    LD (DE),A
    INC DE
    LD A,10
    LD (DE),A
    INC DE
    LD A,0x24
    LD (DE),A
    LD DE,LineBuf
    LD C,9
    CALL Bdos
    ; Skip the file's data blocks: LRem is the 32-bit byte count.
    LD A,(LRem)
    LD B,A
    LD A,(LRem+1)
    OR B
    LD B,A
    LD A,(LRem+2)
    OR B
    LD B,A
    LD A,(LRem+3)
    OR B
    OR A
    JP Z,LNext
LSkip:
    LD DE,SearchFull
    LD (SearchLeft),DE
    LD HL,ListBuf
    LD BC,MaxBlock
    CALL ReadBlock
    OR A
    JP NZ,LNext
    LD A,(ListBuf)
    CP 0xFF
    JP NZ,LNext
    ; BC is the block length. File bytes are that length minus flag and checksum.
    LD A,B
    OR A
    JR NZ,LenOk
    LD A,C
    CP 2
    JP C,LNext
LenOk:
    LD A,C
    SUB 2
    LD C,A
    LD A,B
    SBC A,0
    LD B,A
    LD A,(LRem)
    SUB C
    LD (LRem),A
    LD A,(LRem+1)
    SBC A,B
    LD (LRem+1),A
    LD A,(LRem+2)
    SBC A,0
    LD (LRem+2),A
    LD A,(LRem+3)
    SBC A,0
    LD (LRem+3),A
    JP C,LEnd
    LD A,(LRem)
    LD B,A
    LD A,(LRem+1)
    OR B
    LD B,A
    LD A,(LRem+2)
    OR B
    LD B,A
    LD A,(LRem+3)
    OR B
    OR A
    JR NZ,LSkip
    JP LNext
LEnd:
    RET

; C and X parse "[D:][U<n>:]name" from the tail. The CCP puts the command letter
; in the first FCB, so 0x5C is not the file. Wildcards are * and ?, per 8.3 part;
; a blank part matches anything.
Fcb     EQU 0x3800
Dma     EQU 0x3900
HdrBuf  EQU 0x3A00
MetaBuf EQU 0x3A20
Name11  EQU 0x3A50
Recs    EQU 0x3A5B
UserNow EQU 0x3A5C
Len32   EQU 0x3A60
Chunk   EQU 0x3A64
Src     EQU 0x3A66
NCopy   EQU 0x3A68
Drive   EQU 0x3A69
Found   EQU 0x3A6A
UserReq EQU 0x3A6B
NameSet EQU 0x3A6C
CrPattern EQU 0x3A70
CrIndex EQU 0x3A7B
CrSkip EQU 0x3A7D
CrEntry EQU 0x3A7F
UserOrig EQU 0x3A80
CrFlags EQU 0x3A81
XFlags EQU 0x3A82
CrRemain EQU 0x3A83
DataBuf EQU 0x4100

Create:
    CALL ParseName
    OR A
    JP NZ,NeedName
    LD HL,Name11
    LD B,8
    CALL CrExpand
    LD B,3
    CALL CrExpand
    LD HL,Name11
    LD DE,CrPattern
    LD BC,11
    LDIR
    LD HL,0
    LD (CrIndex),HL
    LD C,32
    LD E,0xFF
    CALL Bdos
    LD (UserNow),A
    LD A,(UserReq)
    CP 0xFF
    JR Z,CrFind
    LD (UserNow),A
    LD E,A
    LD C,32
    CALL Bdos
CrFind:
    LD HL,CrPattern
    LD DE,Name11
    LD BC,11
    LDIR
    CALL MakeFcb
    LD DE,Dma
    LD C,26
    CALL Bdos
    LD DE,Fcb
    LD C,17
    CALL Bdos
    LD HL,(CrIndex)
    LD (CrSkip),HL
CrWalk:
    CP 0xFF
    JP Z,CrEnd
    LD (CrEntry),A
    LD HL,(CrSkip)
    LD A,H
    OR L
    JR Z,CrFound
    DEC HL
    LD (CrSkip),HL
    LD DE,Fcb
    LD C,18
    CALL Bdos
    JR CrWalk
CrFound:
    LD A,(CrEntry)
    RLCA
    RLCA
    RLCA
    RLCA
    RLCA
    LD E,A
    LD D,0
    LD HL,Dma+1
    ADD HL,DE
    PUSH HL
    LD DE,8
    ADD HL,DE
    LD A,(HL)
    AND 0x80
    RLCA
    LD C,A
    INC HL
    LD A,(HL)
    AND 0x80
    RLCA
    ADD A,A
    OR C
    LD (CrFlags),A
    POP HL
    LD DE,Name11
    LD B,11
CrName:
    LD A,(HL)
    AND 0x7F
    LD (DE),A
    INC HL
    INC DE
    DJNZ CrName
    CALL MakeFcb
    LD DE,Fcb
    LD C,35
    CALL Bdos
    LD HL,(Fcb+33)
    LD (CrRemain),HL
    LD (Len32),HL
    LD A,(Fcb+35)
    LD (CrRemain+2),A
    LD (Len32+2),A
    XOR A
    LD (Len32+3),A
    LD B,7
CrSizeBytes:
    LD HL,Len32
    SLA (HL)
    INC HL
    RL (HL)
    INC HL
    RL (HL)
    INC HL
    RL (HL)
    DJNZ CrSizeBytes
    LD A,(CrRemain+1)
    LD B,A
    LD A,(CrRemain+2)
    OR B
    JR NZ,CrFullMeta
    LD A,(CrRemain)
    CP 128
    JR C,CrStoreMeta
CrFullMeta:
    LD A,128
CrStoreMeta:
    LD (Recs),A
    CALL MakeFcb
    LD DE,Fcb
    LD C,15
    CALL Bdos
    CP 0xFF
    JP Z,Missing
    CALL BuildHdr
    CALL BuildMeta
    LD HL,HdrBuf
    LD BC,19
    CALL WriteBlock
    LD HL,MetaBuf
    LD BC,36
    CALL WriteBlock
    XOR A
    LD (Recs),A
CrLp:
    LD A,(CrRemain)
    LD B,A
    LD A,(CrRemain+1)
    OR B
    LD B,A
    LD A,(CrRemain+2)
    OR B
    JR Z,CrDone
    LD DE,Fcb
    LD C,20
    CALL Bdos
    OR A
    JP NZ,CrReadError
    CALL CopyRec
    LD HL,Recs
    INC (HL)
    LD HL,CrRemain
    LD A,(HL)
    SUB 1
    LD (HL),A
    INC HL
    LD A,(HL)
    SBC A,0
    LD (HL),A
    INC HL
    LD A,(HL)
    SBC A,0
    LD (HL),A
    LD A,(Recs)
    CP 128
    JR NZ,CrLp
    CALL CrWriteData
    XOR A
    LD (Recs),A
    JR CrLp
CrDone:
    LD A,(Recs)
    OR A
    CALL NZ,CrWriteData
    JP CrClose
CrWriteData:
    LD A,(Recs)
    LD L,A
    LD H,0
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    PUSH HL
    LD D,H
    LD E,L
    LD A,0xFF
    LD (DataBuf),A
    LD HL,DataBuf
    INC DE
    CALL XorRange
    POP HL
    INC HL
    INC HL
    LD B,H
    LD C,L
    LD HL,DataBuf
    CALL WriteBlock
    RET
CrClose:
    LD DE,Fcb
    LD C,16
    CALL Bdos
    LD HL,(CrIndex)
    INC HL
    LD (CrIndex),HL
    JP CrFind
CrEnd:
    LD HL,(CrIndex)
    LD A,H
    OR L
    JP Z,Missing
    LD DE,OkMsg
    LD C,9
    CALL Bdos
    RET

CrExpand:
    LD C,0
CrExpandLoop:
    LD A,(HL)
    CP 0x2A
    JR NZ,CrExpandTest
    INC C
CrExpandTest:
    LD A,C
    OR A
    JR Z,CrExpandNext
    LD (HL),0x3F
CrExpandNext:
    INC HL
    DJNZ CrExpandLoop
    RET

CrReadError:
    LD DE,Fcb
    LD C,16
    CALL Bdos
    LD DE,ReadError
    LD C,9
    CALL Bdos
    RET

Missing:
    LD DE,NoFile
    LD C,9
    CALL Bdos
    RET

NeedName:
    LD DE,NeedFile
    LD C,9
    CALL Bdos
    RET

Wild:
    LD DE,WildMsg
    LD C,9
    CALL Bdos
    RET

Extract:
    CALL ParseName
    OR A
    JP NZ,NeedName
    LD DE,SearchInitial
    LD (SearchLeft),DE
    JP XSearch
XScan:
    LD DE,SearchFull
    LD (SearchLeft),DE
XSearch:
    LD HL,ListBuf
    LD BC,MaxBlock
    CALL ReadBlock
    OR A
    JR Z,XHeader
    CP 1
    JP Z,XEnd
    JP XTapeError
XHeader:
    LD A,B
    OR A
    JP NZ,XTapeError
    LD A,C
    CP 19
    JP NZ,XTapeError
    LD A,(ListBuf)
    OR A
    JP NZ,XTapeError
    LD A,(ListBuf+1)
    CP 3
    JP NZ,XTapeError
    LD A,(ListBuf+14)
    LD (LRem),A
    LD A,(ListBuf+15)
    LD (LRem+1),A
    LD A,(ListBuf+16)
    LD (LRem+2),A
    LD A,(ListBuf+17)
    LD (LRem+3),A
    LD DE,SearchFull
    LD (SearchLeft),DE
    LD HL,ListBuf
    LD BC,MaxBlock
    CALL ReadBlock
    OR A
    JP NZ,XTapeError
    LD A,B
    OR A
    JP NZ,XTapeError
    LD A,C
    CP 36
    JP NZ,XTapeError
    LD A,(ListBuf)
    CP 0xFF
    JP NZ,XTapeError
    ; User filter, then the 8.3 pattern (blanks match anything).
    LD A,(UserReq)
    CP 0xFF
    JR Z,XNm
    LD C,A
    LD A,(ListBuf+1)
    CP C
    JP NZ,XSkip
XNm:
    LD HL,Name11
    LD DE,ListBuf+2
    CALL Match
    OR A
    JP NZ,XSkip
    LD A,(ListBuf+20)
    LD (XFlags),A
    CALL MakeFcbTape
    JP XSave
XSkip:
    LD A,(LRem)
    LD B,A
    LD A,(LRem+1)
    OR B
    LD B,A
    LD A,(LRem+2)
    OR B
    LD B,A
    LD A,(LRem+3)
    OR B
    JP Z,XScan
    LD DE,SearchFull
    LD (SearchLeft),DE
    LD HL,ListBuf
    LD BC,MaxBlock
    CALL ReadBlock
    OR A
    JP NZ,XTapeError
    LD A,(ListBuf)
    CP 0xFF
    JP NZ,XTapeError
    LD A,B
    OR A
    JR NZ,XSkOk
    LD A,C
    CP 3
    JP C,XTapeError
XSkOk:
    LD A,C
    SUB 2
    LD C,A
    LD A,B
    SBC A,0
    LD B,A
    LD A,(LRem)
    SUB C
    LD (LRem),A
    LD A,(LRem+1)
    SBC A,B
    LD (LRem+1),A
    LD A,(LRem+2)
    SBC A,0
    LD (LRem+2),A
    LD A,(LRem+3)
    SBC A,0
    LD (LRem+3),A
    JP C,XTapeError
    JP XSkip
XSave:
    LD DE,Dma
    LD C,26
    CALL Bdos
    LD DE,Fcb
    LD C,19
    CALL Bdos
    LD DE,Fcb
    LD C,22
    CALL Bdos
    CP 0xFF
    JP Z,NoRoom
XChunk:
    LD A,(LRem)
    LD B,A
    LD A,(LRem+1)
    OR B
    LD B,A
    LD A,(LRem+2)
    OR B
    LD B,A
    LD A,(LRem+3)
    OR B
    JP Z,XMore
    LD DE,SearchFull
    LD (SearchLeft),DE
    LD HL,ListBuf
    LD BC,MaxBlock
    CALL ReadBlock
    OR A
    JP NZ,XAbortTape
    LD A,(ListBuf)
    CP 0xFF
    JP NZ,XAbortTape
    LD A,B
    OR A
    JR NZ,XLen
    LD A,C
    CP 3
    JP C,XAbortTape
XLen:
    LD A,C
    SUB 2
    LD C,A
    LD A,B
    SBC A,0
    LD B,A
    LD (Chunk),BC
    LD HL,ListBuf+1
    LD (Src),HL
    LD A,(LRem)
    SUB C
    LD (LRem),A
    LD A,(LRem+1)
    SBC A,B
    LD (LRem+1),A
    LD A,(LRem+2)
    SBC A,0
    LD (LRem+2),A
    LD A,(LRem+3)
    SBC A,0
    LD (LRem+3),A
    JP C,XAbortTape
    JP XRec
XMore:
    LD DE,Fcb
    LD C,16
    CALL Bdos
    CP 0xFF
    JP Z,XAbortDisk
    LD A,(XFlags)
    AND 1
    JR Z,XNoReadOnly
    LD HL,Fcb+9
    SET 7,(HL)
XNoReadOnly:
    LD A,(XFlags)
    AND 2
    JR Z,XNoSystem
    LD HL,Fcb+10
    SET 7,(HL)
XNoSystem:
    XOR A
    LD (Fcb+12),A
    LD (Fcb+14),A
    LD DE,Fcb
    LD C,30
    CALL Bdos
    CP 0xFF
    JR Z,XAttrError
    LD A,1
    LD (Found),A
    JP XScan
XAttrError:
    LD DE,AttrError
    LD C,9
    CALL Bdos
    RET
XRec:
    LD HL,(Chunk)
    LD A,H
    OR L
    JP Z,XChunk
    LD HL,Dma
    LD B,128
XPad:
    LD (HL),0
    INC HL
    DJNZ XPad
    LD A,128
    LD (NCopy),A
    LD A,(Chunk+1)
    OR A
    JR NZ,XDo
    LD A,(Chunk)
    CP 128
    JR NC,XDo
    LD (NCopy),A
XDo:
    LD HL,(Src)
    LD DE,Dma
    LD A,(NCopy)
    LD B,A
XCp:
    LD A,(HL)
    LD (DE),A
    INC HL
    INC DE
    DJNZ XCp
    LD (Src),HL
    LD DE,Fcb
    LD C,21
    CALL Bdos
    OR A
    JP NZ,XAbortDisk
    LD A,(Chunk)
    LD C,A
    LD A,(NCopy)
    LD B,A
    LD A,C
    SUB B
    LD (Chunk),A
    LD A,(Chunk+1)
    SBC A,0
    LD (Chunk+1),A
    JP XRec
XAbortTape:
    LD HL,TapeError
    JR XAbort
XAbortDisk:
    LD HL,DiskError
XAbort:
    PUSH HL
    LD DE,Fcb
    LD C,16
    CALL Bdos
    XOR A
    LD (Fcb+12),A
    LD (Fcb+14),A
    LD DE,Fcb
    LD C,19
    CALL Bdos
    POP DE
    LD C,9
    CALL Bdos
    RET
XTapeError:
    LD DE,TapeError
    LD C,9
    CALL Bdos
    RET
XEnd:
    LD A,(Found)
    OR A
    JR Z,NotFound
    LD DE,OkMsg
    LD C,9
    CALL Bdos
    RET

NotFound:
    LD DE,NoFile
    LD C,9
    CALL Bdos
    RET

NoRoom:
    LD DE,NoRoomMsg
    LD C,9
    CALL Bdos
    RET

ParseName:
    LD HL,Name11
    LD B,11
    LD A,0x20
PnClr:
    LD (HL),A
    INC HL
    DJNZ PnClr
    XOR A
    LD (Drive),A
    LD (Found),A
    LD (NameSet),A
    LD A,0xFF
    LD (UserReq),A
    LD A,(0x80)
    OR A
    JP Z,PnNo
    LD C,A
    LD HL,0x81
PnSp:
    LD A,(HL)
    CP 0x20
    JR NZ,PnCmd
    INC HL
    DEC C
    JP Z,PnNo
    JR PnSp
PnCmd:
    INC HL
    DEC C
    JP Z,PnNo
PnSp2:
    LD A,(HL)
    CP 0x20
    JR NZ,PnGo
    INC HL
    DEC C
    JP Z,PnNo
    JR PnSp2
PnGo:
    ; Optional drive: one letter A-P then ':'. U is past P, so U12: stays a user.
    LD A,(HL)
    CALL UpCase
    CP 0x41
    JR C,PnUsr
    CP 0x51
    JR NC,PnUsr
    INC HL
    DEC C
    JP Z,PnNo
    LD A,(HL)
    CP 0x3A
    JR NZ,PnDrvBack
    DEC HL
    LD A,(HL)
    CALL UpCase
    SUB 0x41
    INC A
    LD (Drive),A
    INC HL
    INC HL
    DEC C
    JP Z,PnNo
    JR PnUsr
PnDrvBack:
    DEC HL
    INC C
PnUsr:
    ; Optional user: U, one or two digits, then ':'. Otherwise it is the name.
    ; Found keeps the tail count so a failed prefix can be parsed as the name.
    LD D,H
    LD E,L
    LD A,C
    LD (Found),A
    LD A,(HL)
    CALL UpCase
    CP 0x55
    JR NZ,PnName
    INC HL
    DEC C
    JR Z,PnUsrBack
    LD A,(HL)
    CP 0x30
    JR C,PnUsrBack
    CP 0x3A
    JR NC,PnUsrBack
    SUB 0x30
    LD (UserReq),A
    INC HL
    DEC C
    JR Z,PnUsrBack
    LD A,(HL)
    CP 0x3A
    JR Z,PnColon
    CP 0x30
    JR C,PnUsrBack
    CP 0x3A
    JR NC,PnUsrBack
    ; 10*first+second. C is the tail count, so the multiply uses the stack.
    PUSH BC
    LD B,A
    LD A,(UserReq)
    ADD A,A
    ADD A,A
    ADD A,A
    LD C,A
    LD A,(UserReq)
    ADD A,A
    ADD A,C
    LD C,A
    LD A,B
    SUB 0x30
    ADD A,C
    POP BC
    CP 16
    JR NC,PnUsrBack
    LD (UserReq),A
    INC HL
    DEC C
    JR Z,PnUsrBack
PnColon:
    LD A,(HL)
    CP 0x3A
    JR NZ,PnUsrBack
    INC HL
    DEC C
    JP Z,PnNo
    JR PnName
PnUsrBack:
    LD A,(Found)
    LD C,A
    XOR A
    LD (Found),A
    LD H,D
    LD L,E
    LD A,0xFF
    LD (UserReq),A
PnName:
    LD DE,Name11
    LD B,8
PnSt:
    LD A,(HL)
    CP 0x2E
    JR Z,PnDot
    CP 0x20
    JR Z,PnYes
    CALL UpCase
    LD (DE),A
    LD A,1
    LD (NameSet),A
    INC DE
    INC HL
    DEC C
    JR Z,PnYes
    DJNZ PnSt
    LD A,(HL)
    CP 0x2E
    JR NZ,PnYes
PnDot:
    INC HL
    DEC C
    JR Z,PnYes
    LD DE,Name11+8
    LD B,3
PnEx:
    LD A,(HL)
    CP 0x20
    JR Z,PnYes
    CALL UpCase
    LD (DE),A
    LD A,1
    LD (NameSet),A
    INC DE
    INC HL
    DEC C
    JR Z,PnYes
    DJNZ PnEx
PnYes:
    XOR A
    LD (Found),A
    RET
PnNo:
    XOR A
    LD (Found),A
    LD A,1
    RET

UpCase:
    CP 0x61
    RET C
    CP 0x7B
    RET NC
    AND 0x5F
    RET

MakeFcb:
    LD HL,Fcb
    LD B,36
    XOR A
Mf:
    LD (HL),A
    INC HL
    DJNZ Mf
    LD HL,Name11
    LD DE,Fcb+1
    LD BC,11
    LDIR
    LD A,(Drive)
    LD (Fcb),A
    LD HL,Fcb+15
    LD (HL),0x01
    RET

; X builds the FCB from the tape file, not from the pattern.
MakeFcbTape:
    LD HL,Fcb
    LD B,36
    XOR A
MfT:
    LD (HL),A
    INC HL
    DJNZ MfT
    LD HL,ListBuf+2
    LD DE,Fcb+1
    LD BC,11
    LDIR
    LD A,(Drive)
    LD (Fcb),A
    LD HL,Fcb+15
    LD (HL),0x01
    LD A,(ListBuf+1)
    LD E,A
    LD C,32
    CALL Bdos
    RET

; HL = 11-byte pattern, DE = 11-byte directory name. A=0 on a match.
; A blank stem or extension matches anything. * matches the rest of that part.
Match:
    PUSH DE
    LD B,8
    CALL PartMatch
    OR A
    POP DE
    RET NZ
    ; PartMatch advanced both pointers past the stem it matched, but for a
    ; blank or star stem it returns early and leaves them at the start. The
    ; saved directory pointer always gives the extension at dir+8.
    LD HL,8
    ADD HL,DE
    EX DE,HL
    LD HL,Name11+8
    LD B,3
    CALL PartMatch
    RET

PartMatch:
    PUSH HL
    PUSH BC
PmSp:
    LD A,(HL)
    CP 0x20
    JR NZ,PmGo
    INC HL
    DJNZ PmSp
    POP BC
    POP HL
    XOR A
    RET
PmGo:
    POP BC
    POP HL
PmWild:
    LD A,(HL)
    CP 0x2A
    JR NZ,PmChr
    XOR A
    RET
PmChr:
    LD C,A
    LD A,(DE)
    CP C
    JR Z,PmAdv
    LD A,C
    CP 0x3F
    JR NZ,PmFail
PmAdv:
    INC HL
    INC DE
    DJNZ PmWild
    XOR A
    RET
PmFail:
    LD A,1
    RET

CopyRec:
    LD A,(Recs)
    LD L,A
    LD H,0
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    LD DE,DataBuf+1
    ADD HL,DE
    EX DE,HL
    LD HL,Dma
    LD BC,128
    LDIR
    RET

BuildHdr:
    LD HL,HdrBuf
    LD B,19
    XOR A
Hz:
    LD (HL),A
    INC HL
    DJNZ Hz
    LD HL,HdrBuf+1
    LD (HL),3
    LD HL,Name11
    LD DE,HdrBuf+2
    LD BC,7
    LDIR
    LD HL,Name11+8
    LD BC,3
    LDIR
    LD HL,HdrBuf+12
    LD (HL),34
    LD HL,(Len32)
    LD (HdrBuf+14),HL
    LD HL,(Len32+2)
    LD (HdrBuf+16),HL
    LD HL,HdrBuf
    LD DE,18
    CALL XorRange
    RET

BuildMeta:
    LD HL,MetaBuf
    LD B,36
    XOR A
Mz:
    LD (HL),A
    INC HL
    DJNZ Mz
    LD HL,MetaBuf
    LD (HL),0xFF
    INC HL
    LD A,(UserNow)
    LD (HL),A
    INC HL
    EX DE,HL
    LD HL,Name11
    LD BC,11
    LDIR
    LD A,(Recs)
    LD (MetaBuf+15),A
    LD HL,(Len32)
    LD (MetaBuf+16),HL
    LD HL,(Len32+2)
    LD (MetaBuf+18),HL
    LD A,(CrFlags)
    LD (MetaBuf+20),A
    LD HL,MetaBuf
    LD DE,35
    CALL XorRange
    RET

; HL = first byte, DE = how many bytes to fold. The checksum is stored after them.
; DEC DE does not set flags, so the count is tested on its own.
XorRange:
    XOR A
XrLp:
    LD C,A
    LD A,(HL)
    XOR C
    LD C,A
    INC HL
    DEC DE
    LD A,D
    OR E
    LD A,C
    JR NZ,XrLp
    LD (HL),A
    RET

NeedFile:
    DB "ARCH: NEED FILE",13,10,"$"

NoFile:
    DB "ARCH: NO FILE",13,10,"$"

ReadError:
    DB "ARCH: READ ERROR",13,10,"$"

TapeError:
    DB "ARCH: TAPE ERROR",13,10,"$"

DiskError:
    DB "ARCH: DISK ERROR",13,10,"$"

NoRoomMsg:
    DB "ARCH: NO ROOM",13,10,"$"

AttrError:
    DB "ARCH: ATTR ERROR",13,10,"$"

WildMsg:
    DB "ARCH: WILD",13,10,"$"

OkMsg:
    DB "ARCH: OK",13,10,"$"

Usage:
    DB "ARCH C|L|X$"

FeValue:    DB 0
WordBurn:   DW 0
; ROM export uses 8063 pilots for frames below 0x80 (TapeTransport.Cursor).
PilotCount: DW 8063
; Hold after the terminator: the export decoder requires a D6-high gap of at
; least 2800 reference ticks; the archive contract pauses 1000 ms at the end.
PauseCount: DW 150000
SearchLeft: DW 8
LastEar:    DB 0
Pilots:     DB 0
Acc:        DB 0
BitsLeft:   DB 0
XorSum:     DB 0
WbHl:       DW 0
WbBc:       DW 0
WbData:     DB 0
WbBits:     DB 0
Dest:       DW 0
MaxLen:     DW 0
Got:        DW 0
; Shell data: the list line and the 32-bit bytes-left of the current file.
LineBuf:    DEFS 32,0
LRem:       DEFS 4,0
