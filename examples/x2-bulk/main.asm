; ===========================================================================
; main.asm -- X2BULK: BASIC demo and callable entries for X2 bulk upload
;
; What it is:  the X2 bulk graphics example program. Discovers bulk upload
;              support on the X2 card, initializes palette swatches, maps and
;              fills arena pages with an indexed image test pattern, uploads
;              the complete 320x240 image (76,800 bytes) in five bulk DMA
;              segments across 16-KiB page boundaries without per-byte port
;              loops, verifies completion via latched query, and presents it.
;              BASIC prints 0 on success. A jump table at 0x8000 provides
;              callable entry points for custom guest code.
; Builds:      X2BULK.tap (CODE block "X2BULK" at 32768) built by
;              tools/ExpansionMailboxBuilder (X2BulkAssets.cs); includes
;              x2.bulk.asm.
; Runs on:     extended HC2000, full X5 with the X2 command VGA card owned by
;              the main Z80 (--memory paged --m-view --side-z80 16
;              --driver-z80 0 --x2-vga), from BASIC after CLEAR 32767;
;              loads at ORG 0x8000.
; Interface:   0x8000: JP x2_bulk_demo (USR entry, returns BC = 0 on success)
;              0x8003: JP x2_bulk_discover
;              0x8006: JP x2_bulk_begin
;              0x8009: JP x2_bulk_segment
;              0x800C: JP x2_bulk_wait_done
;              0x800F: JP x2_bulk_cancel
;              0x8012: JP x2_bulk_ack
;              0x8015: JP x2_bulk_present
;              0x8018: JP x2_bulk_query
; Provenance:  house code (hc2000-emu); public example source under
;              examples/x2-bulk/.
; ===========================================================================

ORG 0x8000

; Fixed jump table
    JP x2_bulk_demo       ; 0x8000
    JP x2_bulk_discover   ; 0x8003
    JP x2_bulk_begin      ; 0x8006
    JP x2_bulk_segment    ; 0x8009
    JP x2_bulk_wait_done  ; 0x800C
    JP x2_bulk_cancel     ; 0x800F
    JP x2_bulk_ack        ; 0x8012
    JP x2_bulk_present    ; 0x8015
    JP x2_bulk_query      ; 0x8018

; ---------------------------------------------------------------------------
; x2_bulk_demo: USR entry point.
; Out: BC = return code for BASIC (0 = success, 4 = unsupported, 1..5 error)
; ---------------------------------------------------------------------------
x2_bulk_demo:
    DI
    PUSH IX
    PUSH IY
    EXX
    PUSH BC
    PUSH DE
    PUSH HL
    EXX
    LD (demo_saved_sp),SP
    LD SP,0xBFF0

    ; 1. Discover X2 bulk support via port 0x779D query
    CALL x2_bulk_discover
    OR A
    JP NZ,demo_unsupported

    ; 2. Initialize palette entries 0..7
    CALL init_palette
    OR A
    JP NZ,demo_refused

    ; 3. If unit 0 (root) is present, map arena pages 10..14 and fill pattern
    CALL fill_arena_pages

    ; 4. Begin bulk upload of full 320x240 plane (76,800 bytes = 0x12C00)
    ;    destOffset = 0, totalBytes = 76,800
    LD DE,0              ; offset = 0
    LD HL,0x2C00         ; total low word (76800 & 0xFFFF)
    LD BC,1              ; total high word (76800 >> 16)
    CALL x2_bulk_begin
    OR A
    JP NZ,demo_refused

    ; 5. Upload 5 segments across arena pages 10..14
    ; Segment 1: Page 10, offset 0, count 16384 (0x4000)
    LD BC,10
    LD DE,0
    LD HL,0x4000
    CALL x2_bulk_segment
    OR A
    JP NZ,demo_refused
    CALL x2_bulk_wait_done
    OR A
    JP NZ,demo_refused

    ; Segment 2: Page 11, offset 0, count 16384 (0x4000)
    LD BC,11
    LD DE,0
    LD HL,0x4000
    CALL x2_bulk_segment
    OR A
    JP NZ,demo_refused
    CALL x2_bulk_wait_done
    OR A
    JP NZ,demo_refused

    ; Segment 3: Page 12, offset 0, count 16384 (0x4000)
    LD BC,12
    LD DE,0
    LD HL,0x4000
    CALL x2_bulk_segment
    OR A
    JP NZ,demo_refused
    CALL x2_bulk_wait_done
    OR A
    JP NZ,demo_refused

    ; Segment 4: Page 13, offset 0, count 16384 (0x4000)
    LD BC,13
    LD DE,0
    LD HL,0x4000
    CALL x2_bulk_segment
    OR A
    JP NZ,demo_refused
    CALL x2_bulk_wait_done
    OR A
    JP NZ,demo_refused

    ; Segment 5: Page 14, offset 0, count 11264 (0x2C00)
    LD BC,14
    LD DE,0
    LD HL,0x2C00
    CALL x2_bulk_segment
    OR A
    JP NZ,demo_refused
    CALL x2_bulk_wait_done
    OR A
    JP NZ,demo_refused

    ; 6. Verify whole-upload completion via query
    CALL x2_bulk_query
    OR A
    JP NZ,demo_refused
    LD A,(x2_query_receipt+2) ; Phase
    CP 2                      ; 2 = Complete
    JP NZ,demo_incomplete

    ; 7. Present completed image at video boundary
    CALL x2_bulk_present
    OR A
    JP NZ,demo_refused

    ; Success: return 0
    XOR A
    JP demo_return

demo_unsupported:
    LD A,4
    JP demo_return

demo_incomplete:
    LD A,2
    JP demo_return

demo_refused:
    ; A already holds refusal code
    JP demo_return

demo_return:
    LD C,A
    LD B,0
    LD SP,(demo_saved_sp)
    EXX
    POP HL
    POP DE
    POP BC
    EXX
    POP IY
    POP IX
    EI
    RET

; ---------------------------------------------------------------------------
; init_palette: set palette entries 0..7
; ---------------------------------------------------------------------------
init_palette:
    LD HL,palette_table
    LD B,8
palette_loop:
    PUSH BC
    PUSH HL
    LD D,12              ; SetPalette frame is 12 bytes
    CALL x2_submit
    POP HL
    POP BC
    OR A
    RET NZ
    LD DE,12
    ADD HL,DE
    DJNZ palette_loop
    XOR A
    RET

palette_table:
    ; [ver=1, op=4, len=4, res=0, tag:u32, index, r, g, b]
    DB 1,4,4,0, 1,0,0,0, 0,   0,   0,   0   ; 0: Black
    DB 1,4,4,0, 1,0,0,0, 1,   0,   0, 255   ; 1: Blue
    DB 1,4,4,0, 1,0,0,0, 2, 255,   0,   0   ; 2: Red
    DB 1,4,4,0, 1,0,0,0, 3, 255,   0, 255   ; 3: Magenta
    DB 1,4,4,0, 1,0,0,0, 4,   0, 255,   0   ; 4: Green
    DB 1,4,4,0, 1,0,0,0, 5,   0, 255, 255   ; 5: Cyan
    DB 1,4,4,0, 1,0,0,0, 6, 255, 255,   0   ; 6: Yellow
    DB 1,4,4,0, 1,0,0,0, 7, 255, 255, 255   ; 7: White

stripe_pattern_64:
    DB 1,2,3,4,5,6,7,1
    DB 1,2,3,4,5,6,7,1
    DB 1,2,3,4,5,6,7,1
    DB 1,2,3,4,5,6,7,1
    DB 1,2,3,4,5,6,7,1
    DB 1,2,3,4,5,6,7,1
    DB 1,2,3,4,5,6,7,1
    DB 1,2,3,4,5,6,7,1

; ---------------------------------------------------------------------------
; fill_arena_pages: if unit 0 is present, map arena pages 10..14 and fill
; with patterned color bytes.
; ---------------------------------------------------------------------------
fill_arena_pages:
    LD BC,0x77FD
    IN A,(C)
    AND 7
    RET NZ               ; unit 0 not present/idle; skip mapping
    LD BC,0x7BFD
    XOR A
    OUT (C),A
    LD BC,0x7AFD
    OUT (C),A

    ; Loop over pages 10..14
    LD A,10
    LD (current_page),A
    LD A,1               ; initial map epoch
    LD (current_epoch),A
fill_page_loop:
    LD A,(current_page)
    LD (map_slot3_page),A
    LD A,(current_epoch)
    LD (map_req_epoch),A
    INC A
    LD (current_epoch),A

    ; Send MAP request
    LD HL,map_request
    CALL root_map_transaction
    OR A
    RET NZ

    ; Page is now mapped at 0xC000: fill 16,384 bytes with 64-byte repeated pattern
    LD HL,stripe_pattern_64
    LD DE,0xC000
    LD BC,64
    LDIR
    LD HL,0xC000
    LD DE,0xC040
    LD BC,0x3FC0
    LDIR

    ; Advance page
    LD A,(current_page)
    INC A
    LD (current_page),A
    CP 15
    JR C,fill_page_loop

    ; Restore legacy memory map
    LD A,(current_epoch)
    LD (restore_req_epoch),A
    LD HL,map_restore
    CALL root_map_transaction
    RET

; root_map_transaction: send 120-byte MAP request at HL to unit 0
root_map_transaction:
    PUSH HL
    LD BC,0x79FD
    LD A,1
    OUT (C),A
    POP HL
    LD DE,120
    LD BC,0x78FD
root_write:
    LD A,(HL)
    OUT (C),A
    INC HL
    DEC DE
    LD A,D
    OR E
    JR NZ,root_write
    LD BC,0x79FD
    LD A,2
    OUT (C),A
    LD HL,root_reply
    LD DE,40
    LD BC,0x78FD
root_read:
    IN A,(C)
    LD (HL),A
    INC HL
    DEC DE
    LD A,D
    OR E
    JR NZ,root_read
    LD BC,0x79FD
    LD A,5
    OUT (C),A
    LD A,(root_reply+1)  ; result byte
    RET

; ---------------------------------------------------------------------------
; Memory data and buffers
; ---------------------------------------------------------------------------
demo_saved_sp:
    DW 0
current_page:
    DB 0
current_epoch:
    DB 0
root_reply:
    DS 40,0

map_request:
    ; 40-byte header: ver=1, op=14 (MAP), len=80, reqId=1, epochs
    DB 1,14,0,0, 80,0,0,0, 1,0,0,0, 0,0,0,0
    DB 1,0,0,0,0,0,0,0, 1,0,0,0,0,0,0,0, 1,0,0,0,0,0,0,0
    ; 80-byte payload: unitEpoch=1 (u64), mapEpoch (u64), 4 slots
    DB 1,0,0,0, 0,0,0,0
map_req_epoch:
    DB 1,0,0,0, 0,0,0,0
    DB 5,7,0,0,0,0       ; slot 0 (0x0000) legacy rwx
    DS 10,0
    DB 5,7,0,0,0,0       ; slot 1 (0x4000) legacy rwx
    DS 10,0
    DB 5,7,0,0,0,0       ; slot 2 (0x8000) legacy rwx
    DS 10,0
    DB 2,7               ; slot 3 (0xC000) arena page rwx
map_slot3_page:
    DB 10,0,0,0
    DS 10,0

map_restore:
    ; 40-byte header
    DB 1,14,0,0, 80,0,0,0, 2,0,0,0, 0,0,0,0
    DB 1,0,0,0,0,0,0,0, 1,0,0,0,0,0,0,0, 1,0,0,0,0,0,0,0
    ; 80-byte payload: unitEpoch=1 (u64), mapEpoch (u64), 4 legacy slots
    DB 1,0,0,0, 0,0,0,0
restore_req_epoch:
    DB 6,0,0,0, 0,0,0,0
    DB 5,7,0,0,0,0
    DS 10,0
    DB 5,7,0,0,0,0
    DS 10,0
    DB 5,7,0,0,0,0
    DS 10,0
    DB 5,7,0,0,0,0
    DS 10,0

; Include bulk library
INCLUDE "x2.bulk.asm"

; Pinned bounds check
ASSERT $ <= 0xB000
