; ===========================================================================
; map-exercise.asm -- test harness: map an arena page into the main Z80
;
; What it is:  a test program the test places as the 16 KB BASIC ROM image.
;              It asks the root (unit 0) for a MAP that puts arena page 10 at
;              0xC000-0xFFFF, writes 16 bytes (1..16) there, then MAPs the
;              four normal (legacy) slots back. Results: 0x9000 = first MAP
;              result, 0x9001 = restore result (0 = accepted), 0x9002 = 0xFF
;              when finished. It shows how a sample page for the X3 card can
;              be filled by the main Z80.
; Builds:      assembled by tests/Hc2000.App.Tests/ExpansionCompositionTests.cs.
;              Not part of the X3 tapes.
; Runs on:     the main Z80 of an extended HC2000, full X5 with a fresh main
;              map (unit epoch 1, map epoch 1, four legacy slots), from reset
;              in place of the BASIC ROM; ORG 0.
; Interface:   no entry but reset; stack at 0xBFF0; reply header at 0x9100.
;              Root ports 0x7BFD / 0x7AFD, 0x79FD, 0x78FD (see main.asm of the
;              expansion-mailbox example).
; Provenance:  house code (hc2000-emu); a source of the public example in
;              examples/x3-service/, used by its tests (not itself copied
;              into the installer).
; ===========================================================================
;
; Fresh full-X5 main map only: exercise guest-controlled sample-page mapping.
; The ROM, code, stack and receipt buffer are outside the temporary C000 slot.
ORG 0
    NOP
    LD SP,0xBFF0
    NOP
    NOP ; Put the 0008 IF1 page-in address inside the next operand.
; Address unit 0, the root itself: MAP for the main Z80 goes there.
    LD BC,0x7BFD
    XOR A
    OUT (C),A
    LD BC,0x7AFD
    OUT (C),A
    LD HL,map_sample
    CALL map_transaction
    LD (0x9000),A
    OR A
    JR NZ,finish
; Page 10 is now at 0xC000: write the 16-byte sample into it.
    LD HL,sample
    LD DE,0xC000
    LD BC,16
    LDIR
    LD HL,map_restore
    CALL map_transaction
    LD (0x9001),A
finish:
    LD A,0xFF
    LD (0x9002),A
    HALT
; map_transaction: send the 120-byte MAP request at HL (40-byte header plus
; 80-byte payload) to the root and read the 40-byte reply header into 0x9100.
; MAP answers at once, so there is no wait for "reply ready".
; Out: A = the result (0 = accepted). Clobbers BC, DE, HL.
map_transaction:
    PUSH HL
    LD BC,0x79FD
    LD A,1
    OUT (C),A
    POP HL
    LD DE,120
    LD BC,0x78FD
map_write:
    LD A,(HL)
    OUT (C),A
    INC HL
    DEC DE
    LD A,D
    OR E
    JR NZ,map_write
    LD BC,0x79FD
    LD A,2
    OUT (C),A
    LD HL,0x9100
    LD DE,40
    LD BC,0x78FD
map_read:
    IN A,(C)
    LD (HL),A
    INC HL
    DEC DE
    LD A,D
    OR E
    JR NZ,map_read
    LD BC,0x79FD
    LD A,5
    OUT (C),A
    LD A,(0x9101)
    RET
; MAP requests (operation 14, payload 80 bytes). Header: version 1, id 1
; (map_sample) or 2 (map_restore), and the root's epochs, all 1. Payload:
; expected unit epoch (1), expected map epoch (1 before the first MAP, 2
; after it), then four 16-byte slot records for 0x0000, 0x4000, 0x8000 and
; 0xC000: kind (5 = legacy, the normal memory; 2 = arena page), access (7 =
; read, write, execute), target page (32-bit), 10 zero bytes.
map_sample:
    DB 1,14,0,0,80,0,0,0,1,0,0,0,0,0,0,0
    DB 1,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0
    DB 1,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0
    DB 5,7,0,0,0,0
    DS 10,0
    DB 5,7,0,0,0,0
    DS 10,0
    DB 5,7,0,0,0,0
    DS 10,0
    DB 2,7,10,0,0,0
    DS 10,0
map_restore:
    DB 1,14,0,0,80,0,0,0,2,0,0,0,0,0,0,0
    DB 1,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0
    DB 1,0,0,0,0,0,0,0,2,0,0,0,0,0,0,0
    DB 5,7,0,0,0,0
    DS 10,0
    DB 5,7,0,0,0,0
    DS 10,0
    DB 5,7,0,0,0,0
    DS 10,0
    DB 5,7,0,0,0,0
    DS 10,0
; The 16 bytes written into page 10.
sample:
    DB 1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16
