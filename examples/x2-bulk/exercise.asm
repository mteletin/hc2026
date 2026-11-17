; ===========================================================================
; exercise.asm -- guest harness: test all X2 bulk criteria journeys on CPU
;
; What it is:  a test program the test places as the 16 KB BASIC ROM image,
;              so it runs from reset on an extended HC2000 with the X2 card
;              and shared DMA service (profile hc2000-v2-paged-expansion-x5-v1).
;              Exercises:
;                1. Discovery & query receipt signature/version check
;                2. Presentation fence refusal while upload is Open
;                3. Multi-page >16 KiB upload across page boundaries (aligned)
;                4. Unaligned source range upload (page 11, offset 100)
;                5. Whole upload completion check
;                6. Presentation acceptance once complete
;                7. Cancellation quarantine and presentation fence while Cancelled
;                8. Partial restart refusal while Cancelled
;                9. Fence recovery via completed full-plane Clear
;               10. Presentation acceptance after Clear recovery
;               11. Invalid request refusal (tag mismatch)
;              Stores results at 0x9000-0x900D, writes 0xFF to 0x9010, and halts.
; Builds:      assembled by Hc2000.App.Tests / Hc2000.Machine.Tests;
;              includes x2.bulk.asm. Not part of X2BULK.tap.
; Runs on:     main Z80 from reset in place of the BASIC ROM; ORG 0.
; ===========================================================================

ORG 0
    NOP ; Keep 0x0008 IF1 trap inside operand
    LD SP,0xFF00

    ; -----------------------------------------------------------------------
    ; 1. Discovery test
    ; -----------------------------------------------------------------------
    CALL x2_bulk_discover
    LD (0x9000),A

    ; -----------------------------------------------------------------------
    ; 2. Signature and version verification
    ; -----------------------------------------------------------------------
    LD A,(x2_query_receipt)
    CP 0xB1
    JR NZ,bad_sig
    LD A,(x2_query_receipt+1)
    CP 1
    JR NZ,bad_sig
    XOR A
    JR sig_ok
bad_sig:
    LD A,0xFF
sig_ok:
    LD (0x9001),A

    ; -----------------------------------------------------------------------
    ; 3. Presentation fence while Open:
    ;    Begin upload of 20,000 bytes with tag 10
    ; -----------------------------------------------------------------------
    LD A,10
    LD (x2_active_tag),A
    XOR A
    LD (x2_active_tag+1),A
    LD (x2_active_tag+2),A
    LD (x2_active_tag+3),A

    LD DE,0              ; offset 0
    LD HL,20000          ; total 20,000 bytes (0x4E20)
    LD BC,0
    CALL x2_bulk_begin
    ; While Open, Present MUST be refused
    CALL x2_bulk_present
    LD (0x9002),A        ; expected != 0 (refused)

    ; -----------------------------------------------------------------------
    ; 4. Upload segment 1: aligned (16,384 bytes from page 10 offset 0)
    ; -----------------------------------------------------------------------
    LD BC,10             ; page 10
    LD DE,0              ; offset 0
    LD HL,16384          ; count 16384 (0x4000)
    CALL x2_bulk_segment
    LD (0x9003),A
    CALL x2_bulk_wait_done

    ; -----------------------------------------------------------------------
    ; 5. Presentation fence while still Open (16384 of 20000 bytes)
    ; -----------------------------------------------------------------------
    CALL x2_bulk_present
    LD (0x9004),A        ; expected != 0 (refused)

    ; -----------------------------------------------------------------------
    ; 6. Upload segment 2: unaligned source range (page 11, offset 100, count 3616)
    ; -----------------------------------------------------------------------
    LD BC,11             ; page 11
    LD DE,100            ; unaligned offset 100
    LD HL,3616           ; count 3616 (16384 + 3616 = 20000)
    CALL x2_bulk_segment
    LD (0x9005),A
    CALL x2_bulk_wait_done

    ; -----------------------------------------------------------------------
    ; 7. Whole upload completion check
    ; -----------------------------------------------------------------------
    CALL x2_bulk_query
    LD A,(x2_query_receipt+2) ; Phase
    CP 2                      ; 2 = Complete
    JR NZ,not_complete
    XOR A
    JR complete_ok
not_complete:
    LD A,0xFF
complete_ok:
    LD (0x9006),A

    ; -----------------------------------------------------------------------
    ; 8. Present after completion: must succeed
    ; -----------------------------------------------------------------------
    CALL x2_bulk_present
    LD (0x9007),A        ; expected 0 (success)

    ; -----------------------------------------------------------------------
    ; 9. Cancellation quarantine:
    ;    Begin upload of 10,000 bytes with tag 20
    ; -----------------------------------------------------------------------
    LD A,20
    LD (x2_active_tag),A
    LD DE,0
    LD HL,10000
    LD BC,0
    CALL x2_bulk_begin
    LD BC,10
    LD DE,0
    LD HL,5000
    CALL x2_bulk_segment
    CALL x2_bulk_wait_done
    ; Now cancel the active chain
    CALL x2_bulk_cancel
    LD (0x9008),A        ; expected 0 (accepted)

    ; -----------------------------------------------------------------------
    ; 10. Presentation fence while Cancelled
    ; -----------------------------------------------------------------------
    CALL x2_bulk_present
    LD (0x9009),A        ; expected != 0 (refused)

    ; -----------------------------------------------------------------------
    ; 11. Partial restart while Cancelled: must be refused
    ; -----------------------------------------------------------------------
    LD DE,0
    LD HL,5000           ; partial length < 76800
    LD BC,0
    CALL x2_bulk_begin
    LD (0x900A),A        ; expected 2 (Invalid)

    ; -----------------------------------------------------------------------
    ; 12. Fence recovery via completed full-plane Clear command
    ; -----------------------------------------------------------------------
    LD HL,clear_cmd_frame
    LD D,9               ; Clear frame is 9 bytes
    CALL x2_submit
    CALL x2_wait_idle
    LD (0x900B),A        ; expected 0

    ; -----------------------------------------------------------------------
    ; 13. Present accepted after Clear recovery
    ; -----------------------------------------------------------------------
    CALL x2_bulk_present
    LD (0x900C),A        ; expected 0

    ; -----------------------------------------------------------------------
    ; 14. Invalid request refusal: mismatched descriptor tag
    ; -----------------------------------------------------------------------
    ; Begin chain 40
    LD A,40
    LD (x2_active_tag),A
    LD DE,0
    LD HL,1000
    LD BC,0
    CALL x2_bulk_begin
    ; Segment with mismatched descriptor tag (99)
    LD HL,bad_tag_segment_frame
    LD D,24
    CALL x2_submit
    LD (0x900D),A        ; expected 2 (Invalid)
    ; Clean up: cancel chain 40
    CALL x2_bulk_cancel

    ; -----------------------------------------------------------------------
    ; Finished marker
    ; -----------------------------------------------------------------------
    LD A,0xFF
    LD (0x9010),A
    HALT

clear_cmd_frame:
    ; Opcode 1 Clear, colour 0, tag 30
    DB 1, 1, 1, 0, 30, 0, 0, 0, 0

bad_tag_segment_frame:
    ; Frame tag 40, descriptor tag 99 (mismatched)
    DB 1, 9, 16, 0, 40, 0, 0, 0
    DB 1, 1, 0, 0
    DW 10, 0
    DW 500, 0
    DB 99, 0, 0, 0

INCLUDE "x2.bulk.asm"
