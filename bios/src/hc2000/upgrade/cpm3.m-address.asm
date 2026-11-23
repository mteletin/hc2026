; Standalone, unlinked CP/M 3 M: address calculation (lead ruling 653640,
; doc note-20261117-144142 on hc2000-cpm3-bios). Not a disk driver yet; the
; boot path must not load it until the 7800-7FFF resident block is placed.
; M: is drive 12, backed by paged-store pages 0..511 (16 KiB each, 8 MiB):
; SPT 128 / BSH 6 / BLM 63 / DSM 1023. One 16 KiB track is one page, and
; within it the 128-byte sectors fill the 8000-BFFF window in order.
; Input HL = track (0..511), BC = 128-byte sector (0..127).
; Success: carry clear, DE = physical page (0..511), HL = window address.
; Failure: carry set; output registers unspecified. Clobbers A, BC, DE, HL.
ORG 0x7A00
MAddress:
    LD A,H
    CP 2
    JR NC,MAddressError
    LD A,B
    OR A
    JR NZ,MAddressError
    BIT 7,C
    JR NZ,MAddressError
    EX DE,HL
    ; 8000h + sector*128, sector is limited to 0..127.
    LD A,C
    RRCA
    AND 0x80
    LD L,A
    LD A,C
    SRL A
    OR 0x80
    LD H,A
    OR A
    RET
MAddressError:
    SCF
    RET
