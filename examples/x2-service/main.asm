; ===========================================================================
; main.asm -- X2VGA: BASIC demo and callable entry for the X2 VGA service
;
; What it is:  the X2 VGA example program. PRINT USR 32768 sends three
;              commands to the X2 command VGA card: set palette entry 7 to
;              white, clear the 320x240 surface to colour 7, and present it.
;              BASIC prints 0 when all three completed, otherwise the first
;              nonzero result. A second entry at 0x8003 lets your own code
;              send any X2 frame through the same service.
; Builds:      X2VGA.tap (CODE block "X2VGA" at 32768), by
;              tools/ExpansionMailboxBuilder (X2ServiceAssets.cs); includes
;              x2.service.asm.
; Runs on:     extended HC2000, full X5 with the X2 command VGA card owned by
;              the main Z80 (--memory paged --m-view --side-z80 16
;              --driver-z80 0 --x2-vga), from BASIC after CLEAR 32767; loads
;              at ORG 0x8000.
; Interface:   0x8000: JP x2_demo, the USR entry; returns BC = result (0 =
;              success). 0x8003: JP x2_submit, HL = frame, D = length 1..32;
;              returns A (see x2.service.asm). Ports 0x759D and 0x769D.
; Provenance:  house code (hc2000-emu); a public example source, shipped with
;              the installer under examples/x2-service/.
; ===========================================================================
;
; BASIC entry at 8000; the callable main-owned command service is at 8003.
ORG 0x8000
; A two-entry jump table, so both addresses stay fixed however the code
; behind them changes.
    JP x2_demo
    JP x2_submit
; x2_demo: send the three frames below in order; stop at the first nonzero
; result. Out: BC = that result (0 when all completed), for USR.
x2_demo:
    LD HL,palette_frame
    LD D,12
    CALL x2_submit
    OR A
    JR NZ,x2_demo_return
    LD HL,clear_frame
    LD D,9
    CALL x2_submit
    OR A
    JR NZ,x2_demo_return
    LD HL,present_frame
    LD D,8
    CALL x2_submit
x2_demo_return:
    LD C,A
    LD B,0
    RET
; The service itself: x2_submit and its helpers.
INCLUDE "x2.service.asm"
; Version 1 X2 frames: version 1, opcode, payload length, reserved 0, a
; 4-byte little-endian tag, then the payload.
;   palette_frame: opcode 4 SetPalette, tag 3: index 7, red 255, green 255,
;                  blue 255.
;   clear_frame:   opcode 1 Clear, tag 1: colour 7.
;   present_frame: opcode 5 Present, tag 2: no payload.
palette_frame:
    DB 1,4,4,0,3,0,0,0,7,255,255,255
clear_frame:
    DB 1,1,1,0,1,0,0,0,7
present_frame:
    DB 1,5,0,0,2,0,0,0
