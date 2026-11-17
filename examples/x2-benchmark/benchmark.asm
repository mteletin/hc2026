; ===========================================================================
; benchmark.asm -- X2 VGA benchmark ROM: every X2 command in eight named phases
;
; What it is:  a boot ROM that drives the X2 VGA card with no input: it sends
;              every X2 command (Clear, FillRectangle, CopyRectangle,
;              SetPalette, Present, DrawGlyph, ScrollUp) in eight phases and
;              stops after each one, so the host can capture the VGA and ULA
;              screens at a known point and time each command. The original
;              description follows this header.
; Builds:      a raw binary with the house assembler (hc asm benchmark.asm -o
;              benchmark.bin), placed at offset 0 of the machine's 16 KiB boot
;              ROM; X2BenchmarkTests assembles and boots it. It INCLUDEs the X2
;              command service x2.service.asm for x2_submit.
; Runs on:     extended HC2000 with the full X5 expansion and the X2 VGA card
;              (profile hc2000-v2-paged-expansion-x5-v1); no BASIC, no CP/M: it
;              is the ROM the machine boots. ORG 0x0000; must end below 0x1708.
; Interface:   no callers. Host handshake in RAM: PHASE 0x9000, ACK 0x9001,
;              FAIL_OP 0x9002, FAIL_CODE 0x9003 (see below). Also uses 0x9010
;              (text x), 0x9100..0x9111 (glyph frame), the stack below 0xFF00,
;              the ULA screen 0x4000..0x5AFF, border port 0xFE, and through
;              x2_submit the X2 ports 0x759D (command/status) and 0x769D
;              (data/result). Interrupts are never enabled.
; Provenance:  house code (hc2000-emu); a public example source under
;              examples/x2-benchmark/.
; ===========================================================================

; X2 VGA benchmark ROM (hc2000-x2-visual-benchmark). It boots at address 0 on a full-X5 machine
; with the X2 card, with no input, and drives every X2 command through the shipped command
; service (examples/x2-service/x2.service.asm) in named phases:
;   1 palette  SetPalette x24, Clear, FillRectangle swatches
;   2 clear    Clear x4
;   3 fill     FillRectangle at 320x240 down to 8x8
;   4 copy     CopyRectangle 128x96 and 32x32 tiles
;   5 glyph    DrawGlyph text in a 4x8 font
;   6 scroll   ScrollUp of the whole surface and of a band
;   7 cycle    palette cycling: 12 steps of SetPalette x8 + Present
;   8 done     DrawGlyph "FINISHED"
; Each phase ends with Present. The ROM then paints one ULA attribute bar for the phase,
; writes the phase number to PHASE and waits until the host writes it to ACK, so the host can
; capture both screens at a known point. PHASE=0xFF (and a green border) is the finished marker.
; A refused command stores its opcode in FAIL_OP and the service's code in FAIL_CODE, writes
; PHASE=0xEE and halts. Interrupts stay disabled throughout.

; Handshake cells (the host reads and writes them between frames). PHASE: 1..8
; after each phase, 0xFF finished, 0xEE failed. ACK: the phase the host has
; captured. FAIL_OP: the refused frame's opcode. FAIL_CODE: x2_submit's code
; (1 Busy, 2 Invalid, 3 TimeLimit, 4 Unsupported, 0xFE no completion in time,
; 0xFF bad length). TEXT_X: x of the next glyph.
PHASE      EQU 0x9000
ACK        EQU 0x9001
FAIL_OP    EQU 0x9002
FAIL_CODE  EQU 0x9003
TEXT_X     EQU 0x9010
FRAME      EQU 0x9100      ; the DrawGlyph frame the text routine fills in

; One length-prefixed X2 version-1 frame per macro: length, then version, opcode, payload length,
; reserved, a zero tag, and the little-endian payload.
; Frame header: [0] version 1, [1] opcode (1 Clear, 2 FillRectangle,
; 3 CopyRectangle, 4 SetPalette, 5 Present, 6 DrawGlyph, 7 ScrollUp), [2]
; payload length, [3] reserved 0, [4..7] tag u32 (0 here). The first DB of each
; macro is the length byte run_list reads; it is not sent.
; X2_CLEAR colour: the whole 320x240 surface in one palette colour.
X2_CLEAR MACRO col
    DB 9, 1,1,1,0, 0,0,0,0, col
ENDM
; X2_FILL x,y,w,h,colour: a filled rectangle.
X2_FILL MACRO xx,yy,ww,hh,col
    DB 17, 1,2,9,0, 0,0,0,0
    DW xx,yy,ww,hh
    DB col
ENDM
; X2_COPY x,y,w,h,sx,sy: copies the w x h block at (sx,sy) to (x,y).
X2_COPY MACRO xx,yy,ww,hh,sx,sy
    DB 20, 1,3,12,0, 0,0,0,0
    DW xx,yy,ww,hh,sx,sy
ENDM
; X2_PAL index,r,g,b: sets one palette entry (8 bits per channel).
X2_PAL MACRO idx,rr,gg,bb
    DB 12, 1,4,4,0, 0,0,0,0, idx,rr,gg,bb
ENDM
; X2_SCROLL x,y,w,h,rows,colour: moves the rectangle's contents up by rows and
; fills the rows freed at its bottom with colour.
X2_SCROLL MACRO xx,yy,ww,hh,dist,col
    DB 19, 1,7,11,0, 0,0,0,0
    DW xx,yy,ww,hh,dist
    DB col
ENDM

; Reset vector: jump over the ID string to start.
ORG 0
    JP start
    DB "X2 BENCH v1",0     ; never executed: keeps the 0x0008 IF1 trap off the fetch path
; Cold start: interrupts off for good, stack at 0xFF00, handshake cleared,
; black border, ULA bitmap cleared and attributes set to white paper, black ink
; (0x38), and the DrawGlyph header copied to FRAME. Then the phases run.
start:
    DI
    LD SP,0xFF00
    XOR A
    LD (PHASE),A
    LD (FAIL_OP),A
    LD (FAIL_CODE),A
    OUT (0xFE),A           ; black border
    LD HL,0x4000           ; main display: blank bitmap, white paper, black ink
    LD DE,0x4001
    LD BC,0x17FF
    LD (HL),0
    LDIR
    LD HL,0x5800
    LD DE,0x5801
    LD BC,0x02FF
    LD (HL),0x38
    LDIR
    LD HL,glyph_header
    LD DE,FRAME
    LD BC,8
    LDIR
    LD IX,phases
; phases has 3 bytes per entry (DW routine, DB phase number) and ends with DW 0.
; Each routine sends its commands; one Present then shows the result and
; phase_done hands over to the host.
next_phase:
    LD L,(IX+0)
    LD H,(IX+1)
    LD A,H
    OR L
    JR Z,finished
    CALL call_hl
    LD HL,present_frame
    LD D,8
    CALL submit_checked
    LD A,(IX+2)
    CALL phase_done
    INC IX
    INC IX
    INC IX
    JR next_phase
; All phases done: green border, PHASE = 0xFF, HALT with interrupts off (the
; CPU stays here).
finished:
    LD A,4                 ; green border on the main display
    OUT (0xFE),A
    LD A,0xFF
    LD (PHASE),A
    HALT

; CALL call_hl acts as CALL (HL).
call_hl:
    JP (HL)

; HL -> length-prefixed frames, ended by a zero length.
; Each entry is a length byte and then the frame. Returns at the 0 length; any
; refusal halts in submit_checked. Clobbers AF, BC, DE, HL.
run_list:
    LD A,(HL)
    OR A
    RET Z
    LD D,A
    INC HL
    PUSH HL
    PUSH DE
    CALL submit_checked
    POP DE
    POP HL
    LD E,D
    LD D,0
    ADD HL,DE
    JR run_list

; HL -> frame, D = length. Returns only when the card completed the command.
; On a refusal: FAIL_CODE = x2_submit's code, FAIL_OP = the frame's opcode
; (frame+1), PHASE = 0xEE, then HALT for good (interrupts are off).
; Clobbers AF, BC, DE, HL.
submit_checked:
    PUSH HL
    CALL x2_submit
    POP HL
    OR A
    RET Z
    LD (FAIL_CODE),A
    INC HL
    LD A,(HL)
    LD (FAIL_OP),A
    LD A,0xEE
    LD (PHASE),A
    HALT

; HL -> DW x, DW y, DB foreground, DB background, then upper-case text and a zero.
; One DrawGlyph per character, 4 pixels apart.
; FRAME holds the 18-byte DrawGlyph frame: [0..7] header (version 1, opcode 6,
; payload 10), [8..9] x, [10..11] y, [12] foreground, [13] background,
; [14..17] the glyph's 32 bits. Per character only x and the glyph change. A
; character selects font entry (c - 0x20) & 0x3F. Clobbers AF, BC, DE, HL.
draw_text:
    LD E,(HL)
    INC HL
    LD D,(HL)
    INC HL
    LD (TEXT_X),DE
    LD E,(HL)
    INC HL
    LD D,(HL)
    INC HL
    LD (FRAME+10),DE
    LD A,(HL)
    INC HL
    LD (FRAME+12),A
    LD A,(HL)
    INC HL
    LD (FRAME+13),A
text_char:
    LD A,(HL)
    OR A
    RET Z
    INC HL
    PUSH HL
    SUB 0x20
    AND 0x3F
    LD L,A
    LD H,0
    ADD HL,HL
    ADD HL,HL
    LD DE,font
    ADD HL,DE
    LD DE,FRAME+14
    LD BC,4
    LDIR
    LD HL,(TEXT_X)
    LD (FRAME+8),HL
    LD DE,4
    ADD HL,DE
    LD (TEXT_X),HL
    LD HL,FRAME
    LD D,18
    CALL submit_checked
    POP HL
    JR text_char

; A = phase number (1..8): one bright attribute bar on ULA row 2n+2, then PHASE = n and a wait
; for the host's ACK = n.
; Attribute (n & 7)*8 + 0x40: paper colour n & 7 (phase 8 gives black), black
; ink, BRIGHT. Clobbers AF, BC, DE, HL.
phase_done:
    LD B,A
    LD L,A
    LD H,0
    ADD HL,HL
    INC HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ADD HL,HL
    ; HL = (2n+1)*32: attribute row 2n+1 counted from 0 (32 cells per row).
    LD DE,0x5800
    ADD HL,DE
    LD A,B
    AND 7
    ADD A,A
    ADD A,A
    ADD A,A
    OR 0x40
    LD C,32
bar_cell:
    LD (HL),A
    INC HL
    DEC C
    JR NZ,bar_cell
    LD A,B
    LD (PHASE),A
wait_ack:
    LD A,(ACK)
    CP B
    JR NZ,wait_ack
    RET

; x2_submit (from the X2 command service): HL -> frame, D = length 1..32;
; A = 0 only after the card completed the command, else an error code.
INCLUDE "../x2-service/x2.service.asm"

; glyph_header: copied to FRAME at start (version 1, opcode 6 DrawGlyph,
; payload 10 bytes, tag 0). present_frame: a Present (opcode 5, no payload),
; sent after every phase.
glyph_header:
    DB 1,6,10,0, 0,0,0,0
present_frame:
    DB 1,5,0,0, 0,0,0,0

; The phase table: routine, phase number; DW 0 ends it.
phases:
    DW ph_palette
    DB 1
    DW ph_clear
    DB 2
    DW ph_fill
    DB 3
    DW ph_copy
    DB 4
    DW ph_glyph
    DB 5
    DW ph_scroll
    DB 6
    DW ph_cycle
    DB 7
    DW ph_done
    DB 8
    DW 0

; Phase routines: each points HL at its command list and jumps to run_list;
; glyph and done also draw text.
ph_palette:
    LD HL,list_palette
    JP run_list
ph_clear:
    LD HL,list_clear
    JP run_list
ph_fill:
    LD HL,list_fill
    JP run_list
ph_copy:
    LD HL,list_copy
    JP run_list
ph_glyph:
    LD HL,list_glyph_band
    CALL run_list
    LD HL,text_title
    CALL draw_text
    LD HL,text_upper
    CALL draw_text
    LD HL,text_digits
    JP draw_text
ph_scroll:
    LD HL,list_scroll
    JP run_list
ph_cycle:
    LD HL,list_cycle
    JP run_list
ph_done:
    LD HL,list_done_band
    CALL run_list
    LD HL,text_done
    JP draw_text

; 1 palette: 16 named colours, an 8-step blue ramp (16..23) for the cycle phase and a background
; (24) that is none of them, then one swatch per colour.
; Swatches: three rows of eight 32x32 squares, 39 pixels apart, for colours
; 0..23 on background colour 24.
list_palette:
    X2_PAL 0,0,0,0
    X2_PAL 1,16,24,64
    X2_PAL 2,40,80,200
    X2_PAL 3,60,200,220
    X2_PAL 4,60,180,90
    X2_PAL 5,170,230,80
    X2_PAL 6,250,220,60
    X2_PAL 7,255,255,255
    X2_PAL 8,250,140,40
    X2_PAL 9,220,50,50
    X2_PAL 10,200,60,180
    X2_PAL 11,110,60,180
    X2_PAL 12,128,128,128
    X2_PAL 13,200,200,200
    X2_PAL 14,60,60,70
    X2_PAL 15,250,170,190
    X2_PAL 16,20,30,110
    X2_PAL 17,30,60,160
    X2_PAL 18,40,100,210
    X2_PAL 19,50,140,230
    X2_PAL 20,70,180,240
    X2_PAL 21,110,210,250
    X2_PAL 22,170,235,255
    X2_PAL 23,230,250,255
    X2_PAL 24,36,36,44
    X2_CLEAR 24
    X2_FILL 8,56,32,32,0
    X2_FILL 47,56,32,32,1
    X2_FILL 86,56,32,32,2
    X2_FILL 125,56,32,32,3
    X2_FILL 164,56,32,32,4
    X2_FILL 203,56,32,32,5
    X2_FILL 242,56,32,32,6
    X2_FILL 281,56,32,32,7
    X2_FILL 8,100,32,32,8
    X2_FILL 47,100,32,32,9
    X2_FILL 86,100,32,32,10
    X2_FILL 125,100,32,32,11
    X2_FILL 164,100,32,32,12
    X2_FILL 203,100,32,32,13
    X2_FILL 242,100,32,32,14
    X2_FILL 281,100,32,32,15
    X2_FILL 8,144,32,32,16
    X2_FILL 47,144,32,32,17
    X2_FILL 86,144,32,32,18
    X2_FILL 125,144,32,32,19
    X2_FILL 164,144,32,32,20
    X2_FILL 203,144,32,32,21
    X2_FILL 242,144,32,32,22
    X2_FILL 281,144,32,32,23
    DB 0
; 2 clear: the whole surface four times; the last colour stays.
list_clear:
    X2_CLEAR 9
    X2_CLEAR 4
    X2_CLEAR 2
    X2_CLEAR 1
    DB 0
; 3 fill: nested rectangles from the whole surface down to 8x8, and a row of 8x8 cells.
list_fill:
    X2_FILL 0,0,320,240,1
    X2_FILL 32,24,256,192,2
    X2_FILL 96,72,128,96,3
    X2_FILL 128,96,64,48,4
    X2_FILL 144,104,32,32,6
    X2_FILL 152,112,16,16,9
    X2_FILL 156,116,8,8,7
    X2_FILL 32,4,8,8,0
    X2_FILL 48,4,8,8,1
    X2_FILL 64,4,8,8,2
    X2_FILL 80,4,8,8,3
    X2_FILL 96,4,8,8,4
    X2_FILL 112,4,8,8,5
    X2_FILL 128,4,8,8,6
    X2_FILL 144,4,8,8,7
    X2_FILL 160,4,8,8,8
    X2_FILL 176,4,8,8,9
    X2_FILL 192,4,8,8,10
    X2_FILL 208,4,8,8,11
    X2_FILL 224,4,8,8,12
    X2_FILL 240,4,8,8,13
    X2_FILL 256,4,8,8,14
    X2_FILL 272,4,8,8,15
    DB 0
; 4 copy: the centre 128x96 to the four corners, then 32x32 tiles along the bottom.
list_copy:
    X2_COPY 0,0,128,96,96,72
    X2_COPY 192,0,128,96,96,72
    X2_COPY 0,144,128,96,96,72
    X2_COPY 192,144,128,96,96,72
    X2_COPY 96,204,32,32,144,104
    X2_COPY 128,204,32,32,144,104
    X2_COPY 160,204,32,32,144,104
    X2_COPY 192,204,32,32,144,104
    DB 0
; 5 glyph: a dark band for the text lines.
list_glyph_band:
    X2_FILL 0,96,320,48,0
    DB 0
; Text records for draw_text: DW x, DW y, DB foreground, DB background, then
; upper-case text ended by 0.
text_title:
    DW 114,100
    DB 7,0
    DB "HC2000 X2 VGA BENCHMARK",0
text_upper:
    DW 108,116
    DB 6,0
    DB "ABCDEFGHIJKLMNOPQRSTUVWXYZ",0
text_digits:
    DW 114,132
    DB 3,0
    DB "0123456789 +-=:./%()<>!?#",0
; 6 scroll: the whole surface up 8 rows four times, then a 64-row band up 16.
list_scroll:
    X2_SCROLL 0,0,320,240,8,0
    X2_SCROLL 0,0,320,240,8,0
    X2_SCROLL 0,0,320,240,8,0
    X2_SCROLL 0,0,320,240,8,0
    X2_SCROLL 0,160,320,64,16,14
    DB 0
; 7 cycle: eight ramp bars in colours 16..23, then twelve steps that rotate the ramp by one,
; each presented on its own frame; it ends half a turn from where it began.
list_cycle:
    X2_FILL 0,0,40,240,16
    X2_FILL 40,0,40,240,17
    X2_FILL 80,0,40,240,18
    X2_FILL 120,0,40,240,19
    X2_FILL 160,0,40,240,20
    X2_FILL 200,0,40,240,21
    X2_FILL 240,0,40,240,22
    X2_FILL 280,0,40,240,23
    ; A Present frame written out (length 8, version 1, opcode 5, no payload).
    DB 8, 1,5,0,0, 0,0,0,0
    X2_PAL 16,30,60,160
    X2_PAL 17,40,100,210
    X2_PAL 18,50,140,230
    X2_PAL 19,70,180,240
    X2_PAL 20,110,210,250
    X2_PAL 21,170,235,255
    X2_PAL 22,230,250,255
    X2_PAL 23,20,30,110
    DB 8, 1,5,0,0, 0,0,0,0
    X2_PAL 16,40,100,210
    X2_PAL 17,50,140,230
    X2_PAL 18,70,180,240
    X2_PAL 19,110,210,250
    X2_PAL 20,170,235,255
    X2_PAL 21,230,250,255
    X2_PAL 22,20,30,110
    X2_PAL 23,30,60,160
    DB 8, 1,5,0,0, 0,0,0,0
    X2_PAL 16,50,140,230
    X2_PAL 17,70,180,240
    X2_PAL 18,110,210,250
    X2_PAL 19,170,235,255
    X2_PAL 20,230,250,255
    X2_PAL 21,20,30,110
    X2_PAL 22,30,60,160
    X2_PAL 23,40,100,210
    DB 8, 1,5,0,0, 0,0,0,0
    X2_PAL 16,70,180,240
    X2_PAL 17,110,210,250
    X2_PAL 18,170,235,255
    X2_PAL 19,230,250,255
    X2_PAL 20,20,30,110
    X2_PAL 21,30,60,160
    X2_PAL 22,40,100,210
    X2_PAL 23,50,140,230
    DB 8, 1,5,0,0, 0,0,0,0
    X2_PAL 16,110,210,250
    X2_PAL 17,170,235,255
    X2_PAL 18,230,250,255
    X2_PAL 19,20,30,110
    X2_PAL 20,30,60,160
    X2_PAL 21,40,100,210
    X2_PAL 22,50,140,230
    X2_PAL 23,70,180,240
    DB 8, 1,5,0,0, 0,0,0,0
    X2_PAL 16,170,235,255
    X2_PAL 17,230,250,255
    X2_PAL 18,20,30,110
    X2_PAL 19,30,60,160
    X2_PAL 20,40,100,210
    X2_PAL 21,50,140,230
    X2_PAL 22,70,180,240
    X2_PAL 23,110,210,250
    DB 8, 1,5,0,0, 0,0,0,0
    X2_PAL 16,230,250,255
    X2_PAL 17,20,30,110
    X2_PAL 18,30,60,160
    X2_PAL 19,40,100,210
    X2_PAL 20,50,140,230
    X2_PAL 21,70,180,240
    X2_PAL 22,110,210,250
    X2_PAL 23,170,235,255
    DB 8, 1,5,0,0, 0,0,0,0
    X2_PAL 16,20,30,110
    X2_PAL 17,30,60,160
    X2_PAL 18,40,100,210
    X2_PAL 19,50,140,230
    X2_PAL 20,70,180,240
    X2_PAL 21,110,210,250
    X2_PAL 22,170,235,255
    X2_PAL 23,230,250,255
    DB 8, 1,5,0,0, 0,0,0,0
    X2_PAL 16,30,60,160
    X2_PAL 17,40,100,210
    X2_PAL 18,50,140,230
    X2_PAL 19,70,180,240
    X2_PAL 20,110,210,250
    X2_PAL 21,170,235,255
    X2_PAL 22,230,250,255
    X2_PAL 23,20,30,110
    DB 8, 1,5,0,0, 0,0,0,0
    X2_PAL 16,40,100,210
    X2_PAL 17,50,140,230
    X2_PAL 18,70,180,240
    X2_PAL 19,110,210,250
    X2_PAL 20,170,235,255
    X2_PAL 21,230,250,255
    X2_PAL 22,20,30,110
    X2_PAL 23,30,60,160
    DB 8, 1,5,0,0, 0,0,0,0
    X2_PAL 16,50,140,230
    X2_PAL 17,70,180,240
    X2_PAL 18,110,210,250
    X2_PAL 19,170,235,255
    X2_PAL 20,230,250,255
    X2_PAL 21,20,30,110
    X2_PAL 22,30,60,160
    X2_PAL 23,40,100,210
    DB 8, 1,5,0,0, 0,0,0,0
    X2_PAL 16,70,180,240
    X2_PAL 17,110,210,250
    X2_PAL 18,170,235,255
    X2_PAL 19,230,250,255
    X2_PAL 20,20,30,110
    X2_PAL 21,30,60,160
    X2_PAL 22,40,100,210
    X2_PAL 23,50,140,230
    DB 8, 1,5,0,0, 0,0,0,0
    DB 0
; 8 done: a band and the finished line.
list_done_band:
    X2_FILL 80,108,160,24,0
    DB 0
text_done:
    DW 106,116
    DB 5,0
    DB "FINISHED: ALL 7 X2 COMMANDS",0

; 3x5 glyphs in the X2 4x8 cell: row r is nibble r of the little-endian row word,
; bit 3 the left column. Rows 0, 6 and 7 and the right column are spacing.
; 64 entries, 0x20..0x5F, 4 bytes each. The characters no text uses
; (" $ & ; @ [ \ ] ^) repeat the '0' box pattern.
font:
    DB 0x00,0x00,0x00,0x00 ; ' ' 0x20
    DB 0x40,0x44,0x40,0x00 ; '!' 0x21
    DB 0xe0,0xaa,0xea,0x00 ; '"' 0x22
    DB 0xa0,0xae,0xae,0x00 ; '#' 0x23
    DB 0xe0,0xaa,0xea,0x00 ; '$' 0x24
    DB 0xa0,0x42,0xa8,0x00 ; '%' 0x25
    DB 0xe0,0xaa,0xea,0x00 ; '&' 0x26
    DB 0x40,0x04,0x00,0x00 ; "'" 0x27
    DB 0x40,0x88,0x48,0x00 ; '(' 0x28
    DB 0x40,0x22,0x42,0x00 ; ')' 0x29
    DB 0x00,0x4a,0x0a,0x00 ; '*' 0x2a
    DB 0x00,0xe4,0x04,0x00 ; '+' 0x2b
    DB 0x00,0x00,0x84,0x00 ; ',' 0x2c
    DB 0x00,0xe0,0x00,0x00 ; '-' 0x2d
    DB 0x00,0x00,0x40,0x00 ; '.' 0x2e
    DB 0x20,0x42,0x88,0x00 ; '/' 0x2f
    DB 0xe0,0xaa,0xea,0x00 ; '0' 0x30
    DB 0x40,0x4c,0xe4,0x00 ; '1' 0x31
    DB 0xe0,0xe2,0xe8,0x00 ; '2' 0x32
    DB 0xe0,0x62,0xe2,0x00 ; '3' 0x33
    DB 0xa0,0xea,0x22,0x00 ; '4' 0x34
    DB 0xe0,0xe8,0xe2,0x00 ; '5' 0x35
    DB 0xe0,0xe8,0xea,0x00 ; '6' 0x36
    DB 0xe0,0x42,0x44,0x00 ; '7' 0x37
    DB 0xe0,0xea,0xea,0x00 ; '8' 0x38
    DB 0xe0,0xea,0xe2,0x00 ; '9' 0x39
    DB 0x00,0x04,0x04,0x00 ; ':' 0x3a
    DB 0xe0,0xaa,0xea,0x00 ; ';' 0x3b
    DB 0x20,0x84,0x24,0x00 ; '<' 0x3c
    DB 0x00,0x0e,0x0e,0x00 ; '=' 0x3d
    DB 0x80,0x24,0x84,0x00 ; '>' 0x3e
    DB 0xc0,0x42,0x40,0x00 ; '?' 0x3f
    DB 0xe0,0xaa,0xea,0x00 ; '@' 0x40
    DB 0x40,0xea,0xaa,0x00 ; 'A' 0x41
    DB 0xc0,0xca,0xca,0x00 ; 'B' 0x42
    DB 0x60,0x88,0x68,0x00 ; 'C' 0x43
    DB 0xc0,0xaa,0xca,0x00 ; 'D' 0x44
    DB 0xe0,0xc8,0xe8,0x00 ; 'E' 0x45
    DB 0xe0,0xc8,0x88,0x00 ; 'F' 0x46
    DB 0x60,0xa8,0x6a,0x00 ; 'G' 0x47
    DB 0xa0,0xea,0xaa,0x00 ; 'H' 0x48
    DB 0xe0,0x44,0xe4,0x00 ; 'I' 0x49
    DB 0x20,0x22,0x4a,0x00 ; 'J' 0x4a
    DB 0xa0,0xca,0xaa,0x00 ; 'K' 0x4b
    DB 0x80,0x88,0xe8,0x00 ; 'L' 0x4c
    DB 0xa0,0xee,0xaa,0x00 ; 'M' 0x4d
    DB 0xc0,0xaa,0xaa,0x00 ; 'N' 0x4e
    DB 0x40,0xaa,0x4a,0x00 ; 'O' 0x4f
    DB 0xc0,0xca,0x88,0x00 ; 'P' 0x50
    DB 0x40,0xaa,0x6c,0x00 ; 'Q' 0x51
    DB 0xc0,0xca,0xaa,0x00 ; 'R' 0x52
    DB 0x60,0x48,0xc2,0x00 ; 'S' 0x53
    DB 0xe0,0x44,0x44,0x00 ; 'T' 0x54
    DB 0xa0,0xaa,0xea,0x00 ; 'U' 0x55
    DB 0xa0,0xaa,0x4a,0x00 ; 'V' 0x56
    DB 0xa0,0xea,0xae,0x00 ; 'W' 0x57
    DB 0xa0,0x4a,0xaa,0x00 ; 'X' 0x58
    DB 0xa0,0x4a,0x44,0x00 ; 'Y' 0x59
    DB 0xe0,0x42,0xe8,0x00 ; 'Z' 0x5a
    DB 0xe0,0xaa,0xea,0x00 ; '[' 0x5b
    DB 0xe0,0xaa,0xea,0x00 ; '\\' 0x5c
    DB 0xe0,0xaa,0xea,0x00 ; ']' 0x5d
    DB 0xe0,0xaa,0xea,0x00 ; '^' 0x5e
    DB 0x00,0x00,0xe0,0x00 ; '_' 0x5f

    ASSERT $ < 0x1708      ; the IF1 page-in trap at 0x1708 stays outside the ROM image
