; ===========================================================================
; exercise.asm -- test harness: run x4.service.asm on the main Z80 from ROM
;
; What it is:  a test program the test places as the 16 KB BASIC ROM image,
;              so it runs from reset on a full-X5 machine with the X4 card.
;              It STARTs the I32 addition 7 + 3 with tag 42, QUERYs it (done,
;              result 10), ACKs it, QUERYs again (refused: no target), then
;              tries two bad lengths (7 and 33). Results go to 0x9000-0x9005
;              (expected 0, 0, 0, 4, 0xFE, 0xFE), replies to 0x9100, 0x9140,
;              0x9180 and 0x91C0. 0x9006 = 0xFF marks the end; then it halts.
; Builds:      assembled by tests/Hc2000.App.Tests/ExpansionCompositionTests.cs;
;              includes x4.service.asm. Not part of X4MATH.tap.
; Runs on:     the main Z80 of an extended HC2000 with the X4 math
;              accelerator, from reset in place of the BASIC ROM; ORG 0.
; Interface:   no entry but reset; stack at 0xFF00.
; Provenance:  house code (hc2000-emu); a source of the public example in
;              examples/x4-service/, used by its tests (not itself copied
;              into the installer).
; ===========================================================================
;
; Synthetic ROM harness for the real X4 service on the X5 main CPU.
ORG 0
; The leading NOP, as in the other ROM harnesses, keeps address 0x0008 (where
; the IF1 ROM pages in on an opcode fetch) inside an operand.
    NOP
    LD SP,0xFF00
; Each request: HL = frame, DE = reply buffer, A = length; result stored.
    LD HL,start_frame
    LD DE,0x9100
    LD A,32
    CALL x4_submit
    LD (0x9000),A
    LD HL,query_frame
    LD DE,0x9140
    LD A,8
    CALL x4_submit
    LD (0x9001),A
    LD HL,ack_frame
    LD DE,0x9180
    LD A,8
    CALL x4_submit
    LD (0x9002),A
    LD HL,query_frame
    LD DE,0x91C0
    LD A,8
    CALL x4_submit
    LD (0x9003),A
; Bad lengths: 7 (too short) and 33 (too long) must return 0xFE without
; touching the card.
    LD A,7
    CALL x4_submit
    LD (0x9004),A
    LD A,33
    CALL x4_submit
    LD (0x9005),A
    LD A,0xFF
    LD (0x9006),A
    HALT
INCLUDE "x4.service.asm"
; Requests, all with tag 42 (8-byte header, then the payload):
;   start_frame: START, payload: operation 1 (add), source and destination
;                format 1 (I32), options 0, operand A = 7, operand B = 3
;                (8 bytes each), 4 zero bytes.
;   query_frame: QUERY.   ack_frame: ACK.
start_frame:
    DB 1,1,24,0,42,0,0,0,1,1,1,0,7,0,0,0,0,0,0,0,3,0,0,0,0,0,0,0,0,0,0,0
query_frame:
    DB 1,2,0,0,42,0,0,0
ack_frame:
    DB 1,4,0,0,42,0,0,0
