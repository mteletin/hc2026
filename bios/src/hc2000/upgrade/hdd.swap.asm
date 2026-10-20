; Cooperative guest command, never host-injected. Resident owns validation.
ORG 0x0100
HddSwap:
    LD HL,(0x0006)
    LD DE,0x9006
    OR A
    SBC HL,DE
    JR NZ,HddSwapRefused
    CALL 0x9009
    RET
HddSwapRefused:
    LD A,1
    LD HL,0xFFFF
    RET
