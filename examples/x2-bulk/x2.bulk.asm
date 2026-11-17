; ===========================================================================
; x2.bulk.asm -- X2 VGA bulk graphics service: discover, upload, wait, present
;
; What it is:  the main-owned X2 bulk graphics upload library. Exposes discovery
;              via the latched status query on port 0x779D, extent initiation
;              (BeginUpload), segmented physical DMA submission (UploadSegment),
;              completion polling with automatic segment acknowledgement,
;              cancellation, and presentation fences.
; Builds:      included by main.asm (X2BULK.tap) and exercise.asm (tests);
;              can be included in any Z80 guest program.
; Runs on:     the main Z80 of an extended HC2000 with the X2 command VGA card
;              and shared DMA service (profile hc2000-v2-paged-expansion-x5-v1).
; Interface:   x2_bulk_discover
;              x2_bulk_begin
;              x2_bulk_segment
;              x2_bulk_wait_done
;              x2_bulk_cancel
;              x2_bulk_ack
;              x2_bulk_present
;              x2_bulk_query
;              x2_submit
;              x2_wait_idle
; Ports:       0x759D write: 1 = begin frame, 2 = submit frame, 3 = cancel legacy.
;              0x759D read: bit 0 = busy, bit 1 = collecting, bit 2 = refusal,
;                           bit 3 = done, bit 4 = 1.
;              0x769D write: next frame byte (up to 32 bytes).
;              0x769D read: admission result: 0 Accepted, 1 Busy, 2 Invalid,
;                           3 TimeLimit, 4 Unsupported, 5 TooLate.
;              0x779D write: 1 = latch 20-byte immutable query receipt.
;              0x779D read: returns receipt bytes 0..19, then 0xFF.
; Receipt:     [0] 0xB1, [1] 1 (ABI version), [2] Phase (0 Idle, 1 Open,
;              2 Complete, 3 Cancelled), [3] BulkResult, [4..7] Progress u32,
;              [8..11] Total u32, [12..15] Tag u32, [16] DMA State (0 Idle,
;              1 Prog, 2 Queued, 3 Done, 4 Error, 5 Cancelled, 6 TooLate),
;              [17] DMA Refusal, [18..19] 0.
; Provenance:  house code (hc2000-emu); public example source under
;              examples/x2-bulk/.
; ===========================================================================

; Working RAM cells (allocated in scratch RAM 0x9200..0x9260, outside stack/code)
x2_active_tag     EQU 0x9200   ; 4 bytes (tag: u32le)
x2_query_receipt  EQU 0x9204   ; 20 bytes (latched query receipt)
begin_frame       EQU 0x9218   ; 16 bytes
segment_frame     EQU 0x9228   ; 24 bytes
cancel_frame      EQU 0x9240   ; 8 bytes
ack_frame         EQU 0x9248   ; 8 bytes
present_frame     EQU 0x9250   ; 8 bytes

; ---------------------------------------------------------------------------
; x2_bulk_discover: test for X2 bulk upload support via port 0x779D query.
; Out: A = 0 (Z flag set) if supported; A = 4 (NZ flag set) if unsupported.
; Populates x2_query_receipt (20 bytes). Clobbers AF, BC, DE, HL.
; ---------------------------------------------------------------------------
x2_bulk_discover:
    ; Initialize default tag 1 if tag is currently 0
    LD HL,x2_active_tag
    LD A,(HL)
    INC HL
    OR (HL)
    INC HL
    OR (HL)
    INC HL
    OR (HL)
    JR NZ,x2_disc_query
    LD A,1
    LD (x2_active_tag),A
    XOR A
    LD (x2_active_tag+1),A
    LD (x2_active_tag+2),A
    LD (x2_active_tag+3),A
x2_disc_query:
    CALL x2_bulk_query
    RET

; ---------------------------------------------------------------------------
; x2_bulk_query: latches and reads the 20-byte query receipt from port 0x779D.
; Out: A = 0 on valid receipt (signature 0xB1, ABI 1), A = 4 if unsupported.
; Populates x2_query_receipt. Clobbers AF, BC, DE, HL.
; ---------------------------------------------------------------------------
x2_bulk_query:
    LD BC,0x779D
    LD A,1
    OUT (C),A
    LD HL,x2_query_receipt
    LD D,20
x2_query_read_loop:
    IN A,(C)
    LD (HL),A
    INC HL
    DEC D
    JR NZ,x2_query_read_loop
    ; Verify signature 0xB1 and version 1
    LD A,(x2_query_receipt)
    CP 0xB1
    JR NZ,x2_query_unsupported
    LD A,(x2_query_receipt+1)
    CP 1
    JR NZ,x2_query_unsupported
    XOR A
    RET
x2_query_unsupported:
    LD A,4
    OR A
    RET

; ---------------------------------------------------------------------------
; x2_bulk_begin: initiate an upload extent (opcode 8 BeginUpload).
; In:  DE = destination offset (0..65535, high word assumed 0)
;      HL = total bytes low word (e.g. 0x2C00 for 76,800)
;      BC = total bytes high word (e.g. 0x0001 for 76,800)
; Out: A = admission result (0 Accepted, 1 Busy, 2 Invalid, etc.)
; Clobbers AF, BC, DE, HL.
; ---------------------------------------------------------------------------
x2_bulk_begin:
    PUSH BC              ; total high
    PUSH HL              ; total low
    PUSH DE              ; offset low
    ; Frame header: [1, 8, 8, 0]
    LD HL,begin_frame
    LD (HL),1
    INC HL
    LD (HL),8
    INC HL
    LD (HL),8
    INC HL
    LD (HL),0
    INC HL
    ; Tag: copy from x2_active_tag (4 bytes)
    EX DE,HL
    LD HL,x2_active_tag
    LD BC,4
    LDIR                 ; DE is now begin_frame+8
    ; Offset (32-bit): low word from stack, high word = 0
    POP HL
    LD A,L
    LD (DE),A
    INC DE
    LD A,H
    LD (DE),A
    INC DE
    XOR A
    LD (DE),A
    INC DE
    LD (DE),A
    INC DE
    ; Total (32-bit): low word from stack, high word from stack
    POP HL               ; total low
    LD A,L
    LD (DE),A
    INC DE
    LD A,H
    LD (DE),A
    INC DE
    POP HL               ; total high
    LD A,L
    LD (DE),A
    INC DE
    LD A,H
    LD (DE),A
    INC DE
    ; Submit frame (16 bytes)
    LD HL,begin_frame
    LD D,16
    CALL x2_submit
    RET

; ---------------------------------------------------------------------------
; x2_bulk_segment: submit one physical DMA segment (opcode 9 UploadSegment).
; In:  BC = arena physical page (e.g. 10)
;      DE = page offset (0..16383)
;      HL = byte count (1..16384)
; Out: A = admission result (0 Accepted, 1 Busy, 2 Invalid, etc.)
; Clobbers AF, BC, DE, HL.
; ---------------------------------------------------------------------------
x2_bulk_segment:
    PUSH HL              ; count
    PUSH DE              ; offset
    PUSH BC              ; page
    ; Frame header: [1, 9, 16, 0]
    LD HL,segment_frame
    LD (HL),1
    INC HL
    LD (HL),9
    INC HL
    LD (HL),16
    INC HL
    LD (HL),0
    INC HL
    ; Frame tag: copy from x2_active_tag (4 bytes)
    EX DE,HL
    LD HL,x2_active_tag
    LD BC,4
    LDIR                 ; DE is now segment_frame+8
    ; Descriptor header: 1 (version), 1 (RamToCard), 0 (flags), 0 (reserved)
    LD A,1
    LD (DE),A
    INC DE
    LD (DE),A
    INC DE
    XOR A
    LD (DE),A
    INC DE
    LD (DE),A
    INC DE
    ; Page (16-bit) at DE = segment_frame+12
    POP HL               ; page
    LD A,L
    LD (DE),A
    INC DE
    LD A,H
    LD (DE),A
    INC DE
    ; Offset (16-bit) at DE = segment_frame+14
    POP HL               ; offset
    LD A,L
    LD (DE),A
    INC DE
    LD A,H
    LD (DE),A
    INC DE
    ; Count (32-bit) at DE = segment_frame+16
    POP HL               ; count
    LD A,L
    LD (DE),A
    INC DE
    LD A,H
    LD (DE),A
    INC DE
    XOR A
    LD (DE),A
    INC DE
    LD (DE),A
    INC DE
    ; Descriptor tag: copy from x2_active_tag at DE = segment_frame+20
    LD HL,x2_active_tag
    LD BC,4
    LDIR
    ; Submit frame (24 bytes)
    LD HL,segment_frame
    LD D,24
    CALL x2_submit
    RET

; ---------------------------------------------------------------------------
; x2_bulk_wait_done: polls 0x779D query until DMA state != Queued (2).
; If Done (3), automatically acknowledges segment to return DMA channel to Idle.
; Out: A = 0 on Done, A = terminal state (Error/Cancel/TooLate) or 0xFE (timeout).
; Clobbers AF, BC, DE, HL.
; ---------------------------------------------------------------------------
x2_bulk_wait_done:
    LD HL,0xFFFF
x2_wait_done_loop:
    PUSH HL
    CALL x2_bulk_query
    POP HL
    OR A
    JR NZ,x2_wait_done_err
    ; Check DMA state in x2_query_receipt + 16
    LD A,(x2_query_receipt+16)
    CP 2             ; 2 = Queued
    JR NZ,x2_wait_done_terminal
    DEC HL
    LD A,H
    OR L
    JR NZ,x2_wait_done_loop
    LD A,0xFE        ; Bounded wait timeout
    RET
x2_wait_done_terminal:
    CP 3             ; 3 = Done
    JR Z,x2_wait_done_ok
    ; Terminal error or cancel: acknowledge and return state
    PUSH AF
    CALL x2_bulk_ack
    POP AF
    RET
x2_wait_done_ok:
    CALL x2_bulk_ack ; return channel to Idle
    XOR A
    RET
x2_wait_done_err:
    RET

; ---------------------------------------------------------------------------
; x2_bulk_cancel: abort active upload chain (opcode 10 CancelUpload).
; Out: A = admission result (0 Accepted, 5 TooLate, etc.)
; Clobbers AF, BC, DE, HL.
; ---------------------------------------------------------------------------
x2_bulk_cancel:
    LD HL,cancel_frame
    LD (HL),1
    INC HL
    LD (HL),10
    INC HL
    XOR A
    LD (HL),A
    INC HL
    LD (HL),A
    INC HL
    EX DE,HL
    LD HL,x2_active_tag
    LD BC,4
    LDIR
    LD HL,cancel_frame
    LD D,8
    CALL x2_submit
    RET

; ---------------------------------------------------------------------------
; x2_bulk_ack: acknowledge DMA channel terminal state (opcode 11 AcknowledgeSegment).
; Out: A = admission result (0 Accepted, etc.)
; Clobbers AF, BC, DE, HL.
; ---------------------------------------------------------------------------
x2_bulk_ack:
    LD HL,ack_frame
    LD (HL),1
    INC HL
    LD (HL),11
    INC HL
    XOR A
    LD (HL),A
    INC HL
    LD (HL),A
    INC HL
    EX DE,HL
    LD HL,x2_active_tag
    LD BC,4
    LDIR
    LD HL,ack_frame
    LD D,8
    CALL x2_submit
    RET

; ---------------------------------------------------------------------------
; x2_bulk_present: present the back plane at the next video boundary (opcode 5).
; Waits until presentation has committed.
; Out: A = 0 on success, admission error or 0xFE on wait timeout.
; Clobbers AF, BC, DE, HL.
; ---------------------------------------------------------------------------
x2_bulk_present:
    LD HL,present_frame
    LD (HL),1
    INC HL
    LD (HL),5
    INC HL
    XOR A
    LD (HL),A
    INC HL
    LD (HL),A
    INC HL
    EX DE,HL
    LD HL,x2_active_tag
    LD BC,4
    LDIR
    LD HL,present_frame
    LD D,8
    CALL x2_submit
    OR A
    RET NZ
    CALL x2_wait_idle
    OR A
    RET NZ
    LD BC,0x759D
    IN A,(C)
    AND 8            ; bit 3: Done
    JR Z,x2_present_timeout
    XOR A
    RET
x2_present_timeout:
    LD A,0xFE
    RET

; ---------------------------------------------------------------------------
; x2_submit: submits a 1..32 byte X2 frame at HL with length D.
; Out: A = admission result from 0x769D (0 Accepted, 1..5 refused).
; Clobbers AF, BC, DE, HL.
; ---------------------------------------------------------------------------
x2_submit:
    LD A,D
    OR A
    JR Z,x2_bad_length
    CP 33
    JR NC,x2_bad_length
    PUSH HL
    PUSH DE
    CALL x2_wait_idle
    POP DE
    POP HL
    OR A
    RET NZ
    LD BC,0x759D
    LD A,1
    OUT (C),A
    LD BC,0x769D
x2_send_byte:
    LD A,(HL)
    OUT (C),A
    INC HL
    DEC D
    JR NZ,x2_send_byte
    LD BC,0x759D
    LD A,2
    OUT (C),A
    LD BC,0x769D
    IN A,(C)
    RET
x2_bad_length:
    LD A,0xFF
    RET

; ---------------------------------------------------------------------------
; x2_wait_idle: poll status bit 0 until card is not busy (max 65535 reads).
; Out: A = 0 when idle, 0xFE on timeout. Clobbers AF, BC, HL.
; ---------------------------------------------------------------------------
x2_wait_idle:
    LD BC,0x759D
    LD HL,0xFFFF
x2_wait_loop:
    IN A,(C)
    AND 1
    JR Z,x2_ready
    DEC HL
    LD A,H
    OR L
    JR NZ,x2_wait_loop
    LD A,0xFE
    RET
x2_ready:
    XOR A
    RET
