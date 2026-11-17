; ===========================================================================
; exercise-owned.asm -- test harness: run x2.owned.asm against a driver Z80
;
; What it is:  a test program for the main-side client in x2.owned.asm. It
;              makes eight calls and stores each result for the test:
;                0x9001  length 0                          -> 0xFF
;                0x9002  length 33                         -> 0xFF
;                0x9006  frame at 0xFFFF, length 2 (wraps)  -> 0xFF
;                0x9000  Clear                              -> 0
;                0x9003  Clear again                        -> 0
;                0x9007  Present (0xAA until it completes)  -> 0
;                0x9004  frame with unknown opcode 0xFF     -> 0xFE (poisons)
;                0x9005  Clear after the failure            -> 0xFD
;              then halts.
; Builds:      assembled by tests/Hc2000.Machine.Tests/ExpansionX5SnapshotTests.cs
;              as the main program; includes x2.owned.asm. Not part of
;              X2VGA.tap.
; Runs on:     the main Z80 of the full-X5 test machine, with x2.driver.asm
;              running in driver unit 18 and owning the X2 device; ORG 0x8000.
; Interface:   entered at 0x8000; stack at 0xBFFF; results at 0x9000-0x9007.
; Provenance:  house code (hc2000-emu); a source of the public example in
;              examples/x2-service/, used by its tests (not itself copied
;              into the installer).
; ===========================================================================
;
; Test-only RAM entry for a separately configured X5 driver and card owner.
ORG 0x8000
    LD SP,0xBFFF
    LD HL,clear_frame
    LD D,0
    CALL x2_owned_submit
    LD (0x9001),A
    LD HL,clear_frame
    LD D,33
    CALL x2_owned_submit
    LD (0x9002),A
    LD HL,0xFFFF
    LD D,2
    CALL x2_owned_submit
    LD (0x9006),A
    LD HL,clear_frame
    LD D,9
    CALL x2_owned_submit
    LD (0x9000),A
    LD HL,clear_frame
    LD D,9
    CALL x2_owned_submit
    LD (0x9003),A
; 0xAA marks "Present not finished yet": the test checks it while the
; Present waits for the video boundary.
    LD A,0xAA
    LD (0x9007),A
    LD HL,present_frame
    LD D,8
    CALL x2_owned_submit
    LD (0x9007),A
    LD HL,bad_frame
    LD D,9
    CALL x2_owned_submit
    LD (0x9004),A
    LD HL,clear_frame
    LD D,9
    CALL x2_owned_submit
    LD (0x9005),A
    HALT
INCLUDE "x2.owned.asm"
; Frames: Clear (tag 3, colour 7), Present (tag 4), and an invalid frame
; (opcode 0xFF, tag 5) that the card refuses.
clear_frame: DB 1,1,1,0,3,0,0,0,7
present_frame: DB 1,5,0,0,4,0,0,0
bad_frame: DB 1,0xFF,1,0,5,0,0,0,7
