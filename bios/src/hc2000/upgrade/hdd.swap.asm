; Standalone 0x9D00 resident companion. Never call a legacy 0x9000 resident.
ORG 0x0100
HddSwap:
    LD HL,(0x0006)
    LD DE,0x9D06
    OR A
    SBC HL,DE
    JR NZ,HddSwapRefused
    CALL 0x9D09
    RET
HddSwapRefused:
    LD A,1
    LD HL,0xFFFF
    RET
