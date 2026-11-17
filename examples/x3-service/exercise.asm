; ===========================================================================
; exercise.asm -- test harness: run x3.service.asm on the main Z80 from ROM
;
; What it is:  a test program the test places as the 16 KB BASIC ROM image,
;              so it runs from reset on a full-X5 machine with the X3 card
;              (the test first fills arena page 10 with sample bytes). It
;              sends QUERY, START, SET and STOP for channel 0, then two bad
;              lengths (11 and 33); results go to 0x9000-0x9005 (expected 0,
;              0, 0, 0, 0xFE, 0xFE), replies to 0x9100, 0x9140, 0x9180 and
;              0x91C0. 0x9006 = 0xFF marks the end; then it halts.
; Builds:      assembled by tests/Hc2000.App.Tests/ExpansionCompositionTests.cs;
;              includes x3.service.asm. Not part of the X3 tapes.
; Runs on:     the main Z80 of an extended HC2000 with the X3 sound card,
;              from reset in place of the BASIC ROM; ORG 0.
; Interface:   no entry but reset; stack at 0xFF00.
; Provenance:  house code (hc2000-emu); a source of the public example in
;              examples/x3-service/, used by its tests (not itself copied
;              into the installer).
; ===========================================================================
;
; Synthetic ROM harness for the real X3 service on the X5 main CPU.
ORG 0
    NOP ; The IF1 page-in address 0008 falls inside the next operand.
    LD SP,0xFF00
; Each request: HL = frame, DE = reply buffer, A = length; result stored.
    LD HL,query_frame
    LD DE,0x9100
    LD A,12
    CALL x3_submit
    LD (0x9000),A
    LD HL,start_frame
    LD DE,0x9140
    LD A,32
    CALL x3_submit
    LD (0x9001),A
    LD HL,set_frame
    LD DE,0x9180
    LD A,16
    CALL x3_submit
    LD (0x9002),A
    LD HL,stop_frame
    LD DE,0x91C0
    LD A,12
    CALL x3_submit
    LD (0x9003),A
; Bad lengths: 11 (too short) and 33 (too long) must return 0xFE without
; touching the card.
    LD HL,query_frame
    LD DE,0x9200
    LD A,11
    CALL x3_submit
    LD (0x9004),A
    LD A,33
    CALL x3_submit
    LD (0x9005),A
    LD A,0xFF
    LD (0x9006),A
    HALT
INCLUDE "x3.service.asm"
; Requests for channel 0 (8-byte header, then the payload):
;   query_frame: QUERY, tag 1.
;   start_frame: START, tag 2: page 10, offset 0, length 32, no loop,
;                period 124, volume 64.
;   set_frame:   SET, tag 3: period 200, volume 32.
;   stop_frame:  STOP, tag 4.
query_frame:
    DB 1,4,4,0,1,0,0,0,0,0,0,0
start_frame:
    DB 1,1,24,0,2,0,0,0,0,0,10,0,0,0,32,0,0,0,0,0,0,0,0,0,0,0,124,0,64,0,0,0
set_frame:
    DB 1,3,8,0,3,0,0,0,0,0,200,0,32,0,0,0
stop_frame:
    DB 1,2,4,0,4,0,0,0,0,0,0,0
