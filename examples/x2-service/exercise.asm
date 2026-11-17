; ===========================================================================
; exercise.asm -- test harness: run x2.service.asm on the main Z80 from ROM
;
; What it is:  a test program the test places as the 16 KB BASIC ROM image,
;              so it runs from reset on a full-X5 machine with the X2 card.
;              It calls x2_submit with a Clear frame, a Present frame and two
;              bad lengths (0 and 33), stores each result at 0x9000-0x9003
;              (expected 0, 0, 0xFF, 0xFF) and halts.
; Builds:      assembled by tests/Hc2000.App.Tests/ExpansionCompositionTests.cs;
;              includes x2.service.asm. Not part of X2VGA.tap.
; Runs on:     the main Z80 of an extended HC2000 with the X2 command VGA
;              card, from reset in place of the BASIC ROM; ORG 0.
; Interface:   no entry but reset; stack at 0xFF00; results at 0x9000-0x9003.
; Provenance:  house code (hc2000-emu); a source of the public example in
;              examples/x2-service/, used by its tests (not itself copied
;              into the installer).
; ===========================================================================
;
; Synthetic ROM harness: executes the real service on a real X5 main CPU.
ORG 0
    NOP ; Keep the 0x0008 IF1 page-in trap inside an operand, not on an M1 fetch.
    LD SP,0xFF00
; Each call's result A is stored for the test to read.
    LD HL,clear_frame
    LD D,9
    CALL x2_submit
    LD (0x9000),A
    LD HL,present_frame
    LD D,8
    CALL x2_submit
    LD (0x9001),A
    LD HL,clear_frame
    LD D,0
    CALL x2_submit
    LD (0x9002),A
    LD HL,clear_frame
    LD D,33
    CALL x2_submit
    LD (0x9003),A
    HALT
INCLUDE "x2.service.asm"
; Clear (opcode 1, tag 1, colour 7) and Present (opcode 5, tag 2) frames.
clear_frame:
    DB 1,1,1,0,1,0,0,0,7
present_frame:
    DB 1,5,0,0,2,0,0,0
