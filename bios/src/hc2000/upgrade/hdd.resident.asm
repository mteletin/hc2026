; Logical PHASE9000 payload, hc2000-hdd-guest-ack-v1 ADR2 / ABI3.
; Not yet qualified with the genuine native successor receiver.
HddResidentStart:
    JP HddWarmResume
    JP HddFinalize
    JP HddBdos
    JP HddSwitch
    JP HddWarm
    DEFS 0x9040-$,0
HddCodeStart:
HddFinalize:
    ; Require the new opt-in profile before exposing any vectors. Old HDD
    ; profiles return unknown command01; IDENTIFY alone is not this guard.
    CALL HddClearDescriptor
    LD A,4
    LD (HddDescriptor+1),A
    LD A,2
    LD (HddDescriptor+2),A
    LD HL,HddQueryBlock
    LD (HddDescriptor+8),HL
    CALL HddDoorbell
    OR A
    RET NZ
    LD A,(HddQueryBlock)
    CP 1
    JP NZ,HddIoRefused
    LD HL,HddQueryBlock+1
    LD BC,511
HddRequireIdle:
    LD A,(HL)
    OR A
    JP NZ,HddIoRefused
    INC HL
    DEC BC
    LD A,B
    OR C
    JR NZ,HddRequireIdle
    LD A,2
    LD (HddSelected),A
HddIdentifyAll:
    CALL HddIdentifySelected
    OR A
    RET NZ
    LD A,(HddSelected)
    INC A
    LD (HddSelected),A
    CP 6
    JR NZ,HddIdentifyAll
    XOR A
    LD (HddSelected),A
    LD (HddCacheValid),A
    LD A,1
    LD (HddReady),A
    XOR A
    RET
HddIdentifySelected:
    CALL HddFlush
    OR A
    RET NZ
    XOR A
    LD (HddCacheValid),A
    CALL HddClearDescriptor
    LD A,3
    LD (HddDescriptor+1),A
    LD A,(HddSelected)
    LD (HddDescriptor+2),A
    CALL HddDoorbell
    OR A
    RET NZ
    CALL HddCurrentGeneration
    EX DE,HL
    LD HL,HddCache+4
    LD BC,4
    LDIR
    LD A,(HddSelected)
    SUB 2
    ADD A,A
    ADD A,A
    LD E,A
    LD D,0
    LD HL,HddDriveState
    ADD HL,DE
    LD A,(HddCache+8)
    LD (HL),A
    XOR A
    RET
HddBdos:
    ; No recursion/IRQ-driven BDOS work. Preserve original caller registers
    ; on forwarding, then preserve original return registers during accounting.
    LD A,(HddDepth)
    OR A
    JP NZ,HddGuestFailure
    LD A,C
    CP 15
    JR Z,HddTrackedCall
    CP 16
    JR Z,HddTrackedCall
    CP 20
    JR Z,HddTrackedCall
    CP 21
    JR Z,HddTrackedCall
    CP 22
    JR Z,HddTrackedCall
    CP 33
    JR Z,HddTrackedCall
    CP 34
    JR Z,HddTrackedCall
    CP 40
    JR Z,HddTrackedCall
    JP HddOriginalBdos
HddTrackedCall:
    PUSH AF
    PUSH BC
    PUSH DE
    PUSH HL
    PUSH IX
    PUSH IY
    LD A,1
    LD (HddDepth),A
    LD A,C
    LD (HddFunction),A
    LD (HddFcb),DE
    ; The supplied CCP load FCB is the one explicit high-memory exception.
    LD HL,0xC4CD
    OR A
    SBC HL,DE
    JR Z,HddFcbValid
    ; Otherwise require application/default-FCB RAM, not resident aliases.
    LD HL,0x005C
    OR A
    SBC HL,DE
    JP NC,HddCheckDefaultFcb
    LD HL,0x8FDC
    OR A
    SBC HL,DE
    JP C,HddGateRefused
    JR HddFcbValid
HddCheckDefaultFcb:
    LD HL,0x005C
    OR A
    SBC HL,DE
    JP NZ,HddGateRefused
HddFcbValid:
    LD A,(DE)
    OR A
    JR NZ,HddExplicitDrive
    LD C,25
    CALL HddOriginalBdos
    INC A
HddExplicitDrive:
    DEC A
    LD (HddCandidate+1),A
    CP 2
    JP C,HddGateUntracked
    CP 6
    JP NC,HddGateUntracked
    LD C,32
    LD E,0xFF
    CALL HddOriginalBdos
    LD (HddCandidate+2),A
    LD A,1
    LD (HddCandidate),A
    XOR A
    LD (HddCandidate+3),A
    LD (HddCandidate+17),A
    LD (HddCandidate+18),A
    LD (HddCandidate+19),A
    LD HL,(HddFcb)
    LD (HddCandidate+4),HL
    INC HL
    LD DE,HddCandidate+6
    LD B,11
HddCopyIdentity:
    LD A,(HL)
    AND 0x7F
    LD (DE),A
    INC HL
    INC DE
    DJNZ HddCopyIdentity
    ; Exactly sixteen slots for the resolved affected drive.
    LD A,(HddCandidate+1)
    SUB 2
    LD L,0
    LD H,A
    LD E,A
    LD D,0
    SLA E
    RL D
    SLA E
    RL D
    SLA E
    RL D
    SLA E
    RL D
    SLA E
    RL D
    SLA E
    RL D
    ADD HL,DE
    LD DE,HddCensus
    ADD HL,DE
    PUSH HL
    POP IX
    LD HL,0
    LD (HddFreeRecord),HL
    LD (HddMatchRecord),HL
    LD B,16
HddFindRecord:
    LD A,(IX+0)
    OR A
    JR NZ,HddOccupiedRecord
    LD HL,(HddFreeRecord)
    LD A,H
    OR L
    JR NZ,HddNextRecord
    LD (HddFreeRecord),IX
    JR HddNextRecord
HddOccupiedRecord:
    ; Matching pointer with changed user/identity is never clearance.
    LD HL,(HddFcb)
    LD A,(IX+4)
    CP L
    JR NZ,HddOtherPointer
    LD A,(IX+5)
    CP H
    JR NZ,HddOtherPointer
    PUSH BC
    PUSH IX
    POP HL
    LD DE,HddCandidate
    LD B,20
HddCompareRecord:
    LD A,(DE)
    CP (HL)
    JP NZ,HddCompareRefused
    INC HL
    INC DE
    DJNZ HddCompareRecord
    POP BC
    LD (HddMatchRecord),IX
    JR HddNextRecord
HddCompareRefused:
    POP BC
    JP HddGateRefused
HddOtherPointer:
    ; Conservative alias rule: same drive/user/name at another address
    ; cannot create another handle whose later close could be ambiguous.
    LD A,(HddCandidate+2)
    CP (IX+2)
    JR NZ,HddNextRecord
    PUSH BC
    PUSH IX
    POP HL
    LD DE,6
    ADD HL,DE
    LD DE,HddCandidate+6
    LD B,11
HddCompareName:
    LD A,(DE)
    CP (HL)
    JR NZ,HddNameDifferent
    INC HL
    INC DE
    DJNZ HddCompareName
    POP BC
    JP HddGateRefused
HddNameDifferent:
    POP BC
HddNextRecord:
    LD DE,20
    ADD IX,DE
    DJNZ HddFindRecord
    LD A,(HddFunction)
    CP 15
    JR Z,HddGateOpen
    CP 22
    JR Z,HddGateOpen
    LD HL,(HddMatchRecord)
    LD A,H
    OR L
    JP Z,HddGateRefused
    JR HddGateForward
HddGateOpen:
    LD HL,(HddMatchRecord)
    LD A,H
    OR L
    JP NZ,HddGateRefused
    LD HL,(HddFreeRecord)
    LD A,H
    OR L
    JP Z,HddGateRefused
HddGateForward:
    POP IY
    POP IX
    POP HL
    POP DE
    POP BC
    POP AF
    CALL HddOriginalBdos
    ; BDOS success for OPEN/MAKE/CLOSE is 00..03, failure FF.
    PUSH AF
    PUSH BC
    PUSH DE
    PUSH HL
    PUSH IX
    PUSH IY
    CP 4
    JR NC,HddGateFinish
    LD A,(HddFunction)
    CP 16
    JR Z,HddAccountClose
    CP 15
    JR Z,HddAccountOpen
    CP 22
    JR NZ,HddGateFinish
HddAccountOpen:
    LD HL,HddCandidate
    LD DE,(HddFreeRecord)
    LD BC,20
    LDIR
    JR HddGateFinish
HddAccountClose:
    LD HL,(HddMatchRecord)
    LD (HL),0
HddGateFinish:
    XOR A
    LD (HddDepth),A
    POP IY
    POP IX
    POP HL
    POP DE
    POP BC
    POP AF
    RET
HddGateUntracked:
    XOR A
    LD (HddDepth),A
    POP IY
    POP IX
    POP HL
    POP DE
    POP BC
    POP AF
    JP HddOriginalBdos
HddGateRefused:
    XOR A
    LD (HddDepth),A
    POP IY
    POP IX
    POP HL
    POP DE
    POP BC
    POP AF
HddGuestFailure:
    LD A,0xFF
    LD HL,0xFFFF
    RET
HddEof:
    ; CCP C406 successful EOF, BEFORE C166 rewrites the load FCB.
    PUSH AF
    PUSH BC
    PUSH DE
    PUSH HL
    PUSH IX
    PUSH IY
    LD A,(0xC4CD)
    OR A
    JR NZ,HddEofExplicit
    LD C,25
    CALL HddOriginalBdos
    INC A
HddEofExplicit:
    DEC A
    CP 2
    JR C,HddEofChain
    CP 6
    JR NC,HddEofChain
    LD HL,0xC4CD
    LD DE,HddControl+128
    LD BC,36
    LDIR
    LD C,16
    LD DE,0xC4CD
    CALL HddBdos
    PUSH AF
    LD HL,HddControl+128
    LD DE,0xC4CD
    LD BC,36
    LDIR
    POP AF
    CP 4
    JR NC,HddEofRefused
HddEofChain:
    POP IY
    POP IX
    POP HL
    POP DE
    POP BC
    POP AF
    JP 0xC166
HddEofRefused:
    POP IY
    POP IX
    POP HL
    POP DE
    POP BC
    POP AF
    INC SP
    INC SP
    JP 0xC471
HddHome:
    LD A,(HddSelected)
    CP 2
    JP C,0xD52D
    CALL HddFlush
    OR A
    RET NZ
    LD HL,0
    LD (HddTrackValue),HL
    RET
HddTrack:
    LD (HddTrackValue),BC
    LD A,(HddSelected)
    CP 2
    RET NC
    JP 0xD52F
HddSector:
    LD (HddSectorValue),BC
    LD A,(HddSelected)
    CP 2
    RET NC
    JP 0xD534
HddSelect:
    LD A,C
    CP 2
    JR NC,HddSelectAffected
    LD (HddSelected),A
    JP 0xD492
HddSelectAffected:
    CP 6
    JR NC,HddSelectRefused
    LD A,(HddReady)
    OR A
    JR Z,HddSelectRefused
    ; Ordinary SELECT never IDENTIFYs/adopts an external replacement.
    LD A,C
    LD (HddSelected),A
    SUB 2
    LD L,A
    LD H,0
    ADD HL,HL
    ADD HL,HL
    LD DE,HddDriveState
    ADD HL,DE
    BIT 0,(HL)
    JR Z,HddSelectRefused
    LD A,C
    SUB 2
    LD L,A
    LD H,0
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    LD DE,HddDph
    ADD HL,DE
    RET
HddSelectRefused:
    LD HL,0
    RET
HddRead:
    LD A,(HddSelected)
    CP 2
    JP C,0xD542
    LD A,1
    JR HddIo
HddWrite:
    LD A,(HddSelected)
    CP 2
    JP C,0xD53E
    LD A,2
HddIo:
    LD (HddIoOp),A
    PUSH BC
    PUSH DE
    PUSH HL
    PUSH IX
    PUSH IY
    LD A,I
    PUSH AF
    DI
    CALL HddLogicalTransfer
    LD (HddControl+69),A
    POP AF
    JP PO,HddIoNoEi
    EI
HddIoNoEi:
    POP IY
    POP IX
    POP HL
    POP DE
    POP BC
    LD A,(HddControl+69)
    RET
HddLogicalTransfer:
    ; Validate before touching cache, including no OS/resident DMA alias.
    LD A,(HddReady)
    OR A
    JP Z,HddIoRefused
    LD HL,(0xD875)
    LD (HddLogicalDma),HL
    LD DE,HddDirectory
    OR A
    SBC HL,DE
    JR Z,HddDmaValid
    LD HL,(HddLogicalDma)
    LD DE,0x0080
    OR A
    SBC HL,DE
    JP C,HddIoRefused
    LD HL,0x8F80
    LD DE,(HddLogicalDma)
    OR A
    SBC HL,DE
    JP C,HddIoRefused
HddDmaValid:
    LD HL,(HddTrackValue)
    LD A,H
    CP 4
    JP NC,HddIoRefused
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    LD DE,(HddSectorValue)
    LD A,D
    OR A
    JP NZ,HddIoRefused
    LD A,E
    CP 64
    JP NC,HddIoRefused
    SRL E
    SRL E
    ADD HL,DE
    LD (HddIoLba),HL
    LD A,(HddSectorValue)
    AND 3
    RRCA
    LD L,A
    AND 1
    ADD A,0xA9
    LD H,A
    LD A,L
    AND 0x80
    LD L,A
    LD (HddSlice),HL
    ; The cached identity is drive/LBA/old generation. No refresh on 0D.
    LD A,(HddCacheValid)
    OR A
    JR Z,HddCacheMiss
    LD A,(HddSelected)
    LD HL,HddCacheDrive
    CP (HL)
    JR NZ,HddCacheMiss
    LD HL,(HddIoLba)
    LD DE,(HddCacheLba)
    OR A
    SBC HL,DE
    JR NZ,HddCacheMiss
    CALL HddCurrentGeneration
    LD DE,HddCacheGeneration
    LD B,4
HddCompareCacheGeneration:
    LD A,(DE)
    CP (HL)
    JR NZ,HddCacheMiss
    INC HL
    INC DE
    DJNZ HddCompareCacheGeneration
    JR HddCacheAvailable
HddCacheMiss:
    CALL HddFlush
    OR A
    RET NZ
    XOR A
    LD (HddCacheValid),A
    CALL HddCurrentDescriptor
    LD A,1
    LD (HddDescriptor+1),A
    CALL HddDoorbell
    OR A
    RET NZ
    LD A,(HddSelected)
    LD (HddCacheDrive),A
    LD HL,(HddIoLba)
    LD (HddCacheLba),HL
    XOR A
    LD (HddCacheLba+2),A
    LD (HddCacheLba+3),A
    CALL HddCurrentGeneration
    LD DE,HddCacheGeneration
    LD BC,4
    LDIR
    LD A,1
    LD (HddCacheValid),A
HddCacheAvailable:
    LD A,(HddIoOp)
    CP 2
    JR Z,HddWriteThrough
    LD HL,(HddSlice)
    LD DE,(HddLogicalDma)
    LD BC,128
    LDIR
    XOR A
    RET
HddWriteThrough:
    LD HL,(HddLogicalDma)
    LD DE,(HddSlice)
    LD BC,128
    LDIR
    LD A,1
    LD (HddCacheDirty),A
    JP HddFlush
HddCurrentGeneration:
    LD A,(HddSelected)
    SUB 2
    ADD A,A
    ADD A,A
    LD L,A
    LD H,0
    LD DE,HddGenerations
    ADD HL,DE
    RET
HddCurrentDescriptor:
    CALL HddClearDescriptor
    LD A,(HddSelected)
    LD (HddDescriptor+2),A
    LD HL,(HddIoLba)
    LD (HddDescriptor+4),HL
    CALL HddCurrentGeneration
    LD DE,HddDescriptor+12
    LD BC,4
    LDIR
    RET
HddClearDescriptor:
    XOR A
    LD HL,HddDescriptor
    LD DE,HddDescriptor+1
    LD BC,15
    LD (HL),A
    LDIR
    LD A,2
    LD (HddDescriptor),A
    LD HL,HddCache
    LD (HddDescriptor+8),HL
    RET
HddFlush:
    LD A,(HddCacheDirty)
    OR A
    RET Z
    ; Never retarget a failed write onto the current/new drive generation.
    CALL HddClearDescriptor
    LD A,2
    LD (HddDescriptor+1),A
    LD A,(HddCacheDrive)
    LD (HddDescriptor+2),A
    LD HL,HddCacheLba
    LD DE,HddDescriptor+4
    LD BC,4
    LDIR
    LD HL,HddCacheGeneration
    LD DE,HddDescriptor+12
    LD BC,4
    LDIR
    CALL HddDoorbell
    OR A
    RET NZ
    XOR A
    LD (HddCacheDirty),A
    RET
HddDoorbell:
    LD BC,0x0081
    LD A,HddDescriptor & 0xFF
    OUT (C),A
    LD BC,0x0083
    LD A,HddDescriptor >> 8
    OUT (C),A
    LD BC,0x0089
    LD A,1
    OUT (C),A
    IN A,(C)
    RET
HddIoRefused:
    LD A,1
    RET
HddWarm:
    CALL HddFlush
    OR A
    JR Z,HddWarmFlushed
    ; Genuine console retry, not an auto-discard or nested BDOS call.
    CALL 0xD309
    JR HddWarm
HddWarmFlushed:
    ; Retiring TPA with outstanding handles cannot clear the census.
    LD HL,HddCensus
    LD DE,20
    LD B,64
HddWarmScan:
    LD A,(HL)
    OR A
    JR NZ,HddWarmUnsafe
    ADD HL,DE
    DJNZ HddWarmScan
    JP 0xD3C1
HddWarmUnsafe:
    LD A,1
    LD (HddUnsafe),A
    JP 0xD3C1
HddWarmResume:
    ; WBOOT reloads CCP/BDOS; restore only verified operands/opcodes.
    LD A,(0xC3F4)
    CP 0x11
    JR NZ,HddWarmRefused
    LD A,(0xC406)
    CP 0xCD
    JR NZ,HddWarmRefused
    LD HL,(0xC3F5)
    LD DE,0xBD00
    OR A
    SBC HL,DE
    JR NZ,HddWarmRefused
    LD HL,(0xC407)
    LD DE,0xC166
    OR A
    SBC HL,DE
    JR NZ,HddWarmRefused
    LD HL,0x9000
    LD (0xC3F5),HL
    LD HL,HddEof
    LD (0xC407),HL
    LD HL,0x9006
    LD (0x0006),HL
    JP 0xBD00
HddWarmRefused:
    ; Do not enter an unguarded/reloaded CCP or drop retained state.
    DI
    HALT
    JR HddWarmRefused
; Validate only a genuinely Requested wire block, including every reserved
; byte. The first48 bytes are later compared exactly; reserved464 must be0.
HddValidateRequested:
    LD A,(HddQueryBlock)
    CP 1
    JP NZ,HddIoRefused
    LD A,(HddQueryBlock+1)
    CP 1
    JP NZ,HddIoRefused
    LD A,(HddSwitchPrimary)
    LD HL,HddQueryBlock+2
    CP (HL)
    JP NZ,HddIoRefused
    LD A,(HddQueryBlock+3)
    OR A
    JP NZ,HddIoRefused
    LD HL,(HddQueryBlock+6)
    LD A,H
    OR L
    JP NZ,HddIoRefused
    LD HL,(HddQueryBlock+4)
    LD A,H
    OR A
    JP NZ,HddIoRefused
    LD A,L
    CP 0x3C
    JR Z,HddMaskValid
    LD A,(HddSwitchPrimary)
    LD B,A
    LD A,1
HddPrimaryBit:
    ADD A,A
    DJNZ HddPrimaryBit
    CP L
    JP NZ,HddIoRefused
HddMaskValid:
    LD (HddResetMask),HL
    LD HL,HddQueryBlock+8
    LD B,8
    CALL HddAnyNonzero
    OR A
    JP Z,HddIoRefused
    LD HL,HddQueryBlock+16
    LD B,16
    CALL HddAnyNonzero
    OR A
    JP Z,HddIoRefused
    ; Native correlation validates generation truth; reject any nonmask
    ; member even before considering a guest ACK.
    LD HL,HddQueryBlock+32
    LD A,(HddResetMask)
    RRCA
    RRCA
    LD C,A
    LD B,4
HddValidateVector:
    BIT 0,C
    JR NZ,HddVectorMember
    PUSH BC
    LD B,4
    CALL HddAnyNonzero
    POP BC
    OR A
    JP NZ,HddIoRefused
    JR HddVectorNext
HddVectorMember:
    LD DE,4
    ADD HL,DE
HddVectorNext:
    SRL C
    DJNZ HddValidateVector
HddValidateReserved:
    LD HL,HddQueryBlock+48
    LD BC,464
HddReservedLoop:
    LD A,(HL)
    OR A
    JP NZ,HddIoRefused
    INC HL
    DEC BC
    LD A,B
    OR C
    JR NZ,HddReservedLoop
    XOR A
    RET
HddAnyNonzero:
    XOR A
HddAnyNonzeroLoop:
    OR (HL)
    INC HL
    DJNZ HddAnyNonzeroLoop
    RET
HddRequireEmptyCensus:
    LD IX,HddCensus
    LD C,64
HddEmptyLoop:
    LD A,(IX+0)
    OR A
    JR Z,HddEmptyNext
    LD A,(IX+1)
    CP 2
    JP C,HddIoRefused
    CP 6
    JP NC,HddIoRefused
    LD B,A
    LD A,1
HddCensusBit:
    ADD A,A
    DJNZ HddCensusBit
    LD HL,HddResetMask
    AND (HL)
    JP NZ,HddIoRefused
HddEmptyNext:
    LD DE,20
    ADD IX,DE
    DEC C
    JR NZ,HddEmptyLoop
    XOR A
    RET
HddQueryPrimary:
    CALL HddClearDescriptor
    LD A,4
    LD (HddDescriptor+1),A
    LD A,(HddSwitchPrimary)
    LD (HddDescriptor+2),A
    LD HL,HddQueryBlock
    LD (HddDescriptor+8),HL
    JP HddDoorbell
HddCompareChallenge:
    LD HL,HddQueryBlock
    LD DE,HddChallenge
    LD B,48
HddChallengeLoop:
    LD A,(DE)
    CP (HL)
    JP NZ,HddIoRefused
    INC HL
    INC DE
    DJNZ HddChallengeLoop
    JP HddValidateReserved
HddCheckStack:
    LD A,(HddStack)
    CP 0xA5
    JP NZ,HddIoRefused
    XOR A
    RET
HddCodeEnd:
ASSERT HddCodeEnd <= HddCodeLimit
    DEFS 0xB500-$,0
HddSwitch:
    CALL HddCheckStack
    OR A
    JP NZ,HddGuestFailure
    LD A,(HddDepth)
    OR A
    JP NZ,HddGuestFailure
    LD A,(HddUnsafe)
    OR A
    JP NZ,HddGuestFailure
    LD A,(HddReady)
    OR A
    JP Z,HddGuestFailure
    ; Private stack is separate from DMA; save caller SP/IFF exactly.
    PUSH BC
    PUSH DE
    PUSH IX
    PUSH IY
    LD A,I
    PUSH AF
    DI
    LD (HddSavedSP),SP
    LD SP,HddStackTop
    LD A,(HddSelected)
    LD (HddPriorSelected),A
    LD A,1
    LD (HddDepth),A
    LD A,2
    LD (HddSwitchPrimary),A
HddFindRequest:
    CALL HddQueryPrimary
    OR A
    JR Z,HddFoundRequest
    CP 0x0E
    JP NZ,HddSwitchFailed
    LD A,(HddSwitchPrimary)
    INC A
    LD (HddSwitchPrimary),A
    CP 6
    JR C,HddFindRequest
    JP HddSwitchFailed
HddFoundRequest:
    CALL HddValidateRequested
    OR A
    JP NZ,HddSwitchFailed
    CALL HddRequireEmptyCensus
    OR A
    JP NZ,HddSwitchFailed
    LD HL,HddQueryBlock
    LD DE,HddChallenge
    LD BC,48
    LDIR
    CALL HddFlush
    OR A
    JP NZ,HddSwitchFailed
    ; Real, normal guest BDOS37, not injected and not a files-closed bit.
    LD DE,(HddResetMask)
    LD C,37
    CALL HddOriginalBdos
    OR A
    JP NZ,HddSwitchFailed
    DI
    CALL HddCheckStack
    OR A
    JP NZ,HddSwitchUnknown
    CALL HddQueryPrimary
    OR A
    JP NZ,HddSwitchFailed
    CALL HddCompareChallenge
    OR A
    JP NZ,HddSwitchFailed
    ; No BDOS/file work between this final comparison and actual ACK OUT.
    LD A,(HddSwitchPrimary)
    SUB 2
    ADD A,A
    ADD A,A
    LD L,A
    LD H,0
    LD DE,HddChallenge+32
    ADD HL,DE
    LD DE,HddDescriptor+12
    LD BC,4
    LDIR
    LD A,5
    LD (HddDescriptor+1),A
    CALL HddDoorbell
    ; Native holds BEFORE the next IN in HddDoorbell, through Finish.
    OR A
    JR Z,HddReadTerminal
    CP 0x0E
    JP NZ,HddSwitchUnknown
HddReadTerminal:
    CALL HddQueryPrimary
    OR A
    JP NZ,HddSwitchUnknown
    LD A,(HddQueryBlock+1)
    CP 3
    JR Z,HddTerminalPhase
    CP 4
    JP NZ,HddSwitchUnknown
HddTerminalPhase:
    LD (HddSwitchResult),A
    LD A,1
    LD (HddQueryBlock+1),A
    CALL HddCompareChallenge
    OR A
    JP NZ,HddSwitchUnknown
    LD A,(HddSwitchResult)
    CP 4
    JR Z,HddSwitchFailed
    ; Commit alone permits cache invalidation and primary IDENTIFY.
    XOR A
    LD (HddCacheValid),A
    LD A,(HddSwitchPrimary)
    LD (HddSelected),A
    CALL HddIdentifySelected
    OR A
    JR NZ,HddSwitchUnknown
    XOR A
    JR HddSwitchReturn
HddSwitchUnknown:
    XOR A
    LD (HddReady),A
    LD A,1
    LD (HddUnsafe),A
HddSwitchFailed:
    LD A,1
HddSwitchReturn:
    LD (HddSwitchResult),A
    XOR A
    LD (HddDepth),A
    LD A,(HddPriorSelected)
    LD (HddSelected),A
    LD SP,(HddSavedSP)
    POP AF
    JP PO,HddSwitchNoEi
    EI
HddSwitchNoEi:
    POP IY
    POP IX
    POP DE
    POP BC
    LD A,(HddSwitchResult)
    LD HL,0
    OR A
    RET Z
    DEC HL
    RET
HddSwitchEnd:
ASSERT HddSwitchEnd <= HddSwitchLimit
HddResidentEnd:
