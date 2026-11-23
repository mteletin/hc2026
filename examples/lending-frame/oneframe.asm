; ===========================================================================
; oneframe.asm -- one-frame lending demo (hc2000-expansion-lending-interrupts)
;
; What it is:  a boot ROM that proves the expansion lending and interrupt
;              contracts on screen. Four compute units each draw one band of
;              a 320x240 frame into their own pages, lend the two pages to
;              the X2 VGA card, the card DMAs the bands up and Presents the
;              frame, and the main Z80 advances only because the row's
;              interrupt source (7F9D) wakes it: four unit-done sources,
;              eight dma-complete sources, one frame-presented source.
;              The expected picture: four horizontal bands, red / green /
;              blue / white, on a black background.
; Builds:      a raw binary with the house assembler (hc asm oneframe.asm -o
;              oneframe.bin), placed at offset 0 of the machine's 16 KiB
;              boot ROM. LendingFrameDemoTests assembles and boots it
;              headless; LendingFrameRunner runs it in the app (Benchmarks
;              menu). It INCLUDEs the X2 command service x2.service.asm.
; Runs on:     extended HC2000, profile hc2000-v2-paged-expansion-x5-v1,
;              with the X2 VGA card (card id 3).
; Handshake:   PHASE 0x9000 (1 units started, 2 pages lent, 3 present sent,
;              0xFF finished, 0xEE failed); the lend comes after the wait for
;              the four unit-done sources, because a lent page denies the
;              unit's writes until the transfer ends. FAIL_OP 0x9001,
;              FAIL_CODE 0x9002,
;              UNIT_DONE 0x9003 (interrupt count, class unit-done),
;              DMA_DONE 0x9004 (class dma-complete), FRAME_PRESENTED 0x9005
;              (class frame-presented, set by the interrupt only). The row
;              register 7F9D is drained only in the interrupt handler; the
;              main body never reads it, so every counted source is proof
;              the interrupt woke the main.
; Provenance:  house code (hc2000-emu); a public example source, shipped
;              with the installer under examples/lending-frame/.
; ===========================================================================
;
; Controller protocol ( ExpansionController, port block 7F.. ): the guest
;   selects a unit (OUT 7FBF low, OUT 7FAF high), starts collecting (OUT
;   79FD,1), streams the frame to 78FD, and submits (OUT 79FD,2). A frame is
;   a 40-byte header -- version 1, opcode, reserved 0, reserved 0, 32-bit
;   little-endian payload length, a nonzero field, then the resource,
;   execution and map epochs as 64-bit little-endian -- plus the payload.
;   Status 77FD: bit 0 collecting, bit 1 a receipt is waiting, bit 2 a
;   pending id, bit 3 a unit fault, bits 4-7 the last result (0 accepted,
;   1 busy, 2 stale epoch, 3 invalid frame, 4 invalid state). A receipt is
;   read from 78FD: version, result, lifecycle, reserved, payload length,
;   the echoed field, then the same three epochs of the unit after the
;   operation, plus the payload. The guest clears it (OUT 79FD,5). Opcodes
;   used here: 1 QUERY (epochs all zero is legal for it; the receipt carries
;   the unit's current epochs), 4 REPLACE IMAGE (u16 entry, u16 reserved,
;   u32 count, image; execution epoch rises by one), 7 START (empty payload),
;   17 LEND (payload: u16 card id, u16 page; the unit keeps the page it
;   lends until the loan settles). Row-interrupt enable is control verb 6 on
;   79FD. Lending a page denies the unit's own writes to it until the loan
;   settles, so the lend happens only once the units have halted.
; ===========================================================================

ORG 0
    JP start
    DB "LEND1F v1",0,0   ; never executed: keeps the 0x0008 IF1 trap off the fetch path

ORG 0x38
    JP int               ; vector: the row source and the frame clock share this line

; --- Interrupt handler: the only reader of 7F9D. ---
; A read pops the next source and acks it; zero when drained. Bits 0-4 the
; id, bits 5-7 the class (1 unit-done, 2 unit-mailbox-wait, 3 dma-complete,
; 4 frame-presented). The frame clock also wakes us
; (it drains to zero and leaves); a mailbox-wait would mean a unit asked for
; mail, which none of the four do.
int:
    PUSH AF
    PUSH BC               ; B and C are live in the main body's port loops.
    LD C,0x9D
int_drain:
    LD B,0x7F
    IN A,(C)
    OR A
    JR Z,int_out
    ; Class = bits 5-7. Mask the id rather than relying on rotate semantics.
    AND 0xE0
    CP 0x20
    JR Z,int_unit_done
    CP 0x60
    JR Z,int_dma
    CP 0x80
    JR NZ,int_drain
    LD A,1
    LD (0x9005),A        ; frame-presented: the main body only ever sees this.
    JR int_out
int_unit_done:
    LD A,(0x9003)
    INC A
    LD (0x9003),A
    JR int_out
int_dma:
    LD A,(0x9004)
    INC A
    LD (0x9004),A
int_out:
    POP BC
    POP AF
    EI                    ; maskable INT acceptance cleared IFF2, so RETI alone leaves IFF1 down.
    RETI                  ; preserve the daisy-chain return and restore the EI state.

; --- Main body. ---
start:
    DI
    LD SP,0xFF00
    XOR A
    LD (0x9000),A
    LD (0x9003),A
    LD (0x9004),A
    LD (0x9005),A
    LD DE,CELL
    LD B,48
cell_zero:
    LD (DE),A
    INC DE
    DJNZ cell_zero
    LD A,0
    LD BC,0x00FE
    OUT (C),A              ; black border
    ; Palette: index 0 black, 1 red, 2 green, 3 blue, 4 white (the bands).
    LD HL,pal_list
    CALL run_list
    ; Units 1..4: learn the epochs, load their band program, start them.
    ; The lend comes after the wait: lending while the units write their
    ; bands would fault them (write denial on a lent page).
    LD C,1
units:
    PUSH BC
    CALL query_unit
    POP BC
    PUSH BC
    CALL reset_unit
    POP BC
    PUSH BC
    CALL replace_image
    POP BC
    PUSH BC
    CALL start_unit
    POP BC
    INC C
    LD A,C
    CP 5
    JR NZ,units
    LD A,1
    LD (0x9000),A         ; PHASE 1: the four units are running.
    LD A,6
    LD BC,0x79FD
    OUT (C),A              ; enable the row's contribution to the INT line.
    EI
wait_units:
    LD A,(0x9003)
    CP 4
    JR Z,units_done
    HALT
    JR wait_units
units_done:
    ; The bands are drawn and the units are halted: lend the two band pages
    ; of each unit to the X2 card (id 3).
    LD C,1
lends:
    PUSH BC
    CALL lend_pages
    POP BC
    INC C
    LD A,C
    CP 5
    JR NZ,lends
    LD A,2
    LD (0x9000),A         ; PHASE 2: the bands are drawn and the pages lent.
    ; Upload: one BeginUpload for the whole frame, then the eight segments
    ; in band order (the upload fills the destination in submission order).
    LD HL,x_upload
    LD D,16
    CALL submit_checked
    ; The template is in ROM, so copy both command frames into writable RAM
    ; before patching the unit's source-page fields.
    LD HL,seg_frame
    LD DE,FRAME
    LD B,48
seg_template_copy:
    LD A,(HL)
    LD (DE),A
    INC HL
    INC DE
    DJNZ seg_template_copy
    LD C,1
segs:
    PUSH BC
    ; Unit N's band pages: 508+4N and the page after it (high byte 2).
    LD A,C
    ADD A,A
    ADD A,A
    LD DE,0
    LD E,A
    LD HL,508
    ADD HL,DE
    LD A,L
    LD (FRAME+12),A
    LD A,H
    LD (FRAME+13),A
    INC HL                 ; carry the second page number into the high byte when needed.
    LD A,L
    LD (FRAME+36),A
    LD A,H
    LD (FRAME+37),A
    LD A,(0x9004)
    INC A
    LD (0x9008),A          ; wait for this segment's DMA-complete interrupt.
    LD HL,FRAME
    LD D,24
    CALL submit_checked
    CALL wait_dma_done     ; waits for DMA completion, then acknowledges the channel.
    LD A,(0x9004)
    INC A
    LD (0x9008),A
    LD HL,FRAME + 24
    LD D,24
    CALL submit_checked
    CALL wait_dma_done
    POP BC
    INC C
    LD A,C
    CP 5
    JR NZ,segs
    LD HL,x_present
    LD D,8
    CALL submit_checked
    LD A,3
    LD (0x9000),A         ; PHASE 3: the present is on its way to the boundary.
wait_frame:
    LD A,(0x9005)
    OR A
    JR NZ,frame_done
    HALT
    JR wait_frame
frame_done:
    LD A,0xFF
    LD (0x9000),A         ; PHASE finished: the expected picture is on screen.
    LD A,4
    LD BC,0x00FE
    OUT (C),A              ; green border
frame_hold:
    HALT                   ; the following JR is the interrupt return target.
    JR frame_hold          ; never let a frame wake execute the table below.

; --- X2 command frames (x2_submit: HL points at the frame, D its length;
; --- the frame is version 1, opcode, payload length, reserved 0, 32-bit
; --- tag, payload). Tag 1 chains the upload. SetPalette payload: index,
; --- red, green, blue. BeginUpload payload: u32 destination offset (0),
; --- u32 total bytes (76800). UploadSegment payload (16): version 1,
; --- direction ram-to-card (1), reserved, reserved, u16 page, u16 offset,
; --- u32 count, u32 tag. Present: no payload.
pal_list:
    DB 12,1,4,4,0, 0,0,0,0, 0,  0,  0,  0
    DB 12,1,4,4,0, 0,0,0,0, 1, 220, 40, 40
    DB 12,1,4,4,0, 0,0,0,0, 2,  40, 180, 60
    DB 12,1,4,4,0, 0,0,0,0, 3,  60, 90, 220
    DB 12,1,4,4,0, 0,0,0,0, 4, 255, 255, 255
    DB 0
x_upload:
    DB 1,8,8,0, 1,0,0,0   ; version 1, op 8, payload 8, tag 1.
    DB 0,0,0,0            ; destination 0.
    DB 0,0x2C,0x01,0      ; total 76800 little-endian (320x240).
x_present:
    DB 1,5,0,0, 0,0,0,0
; AcknowledgeSegment (op 11): no payload, the chain tag.
ack_frame:
    DB 1,11,0,0, 1,0,0,0
; The two upload segments for unit N (pages 508+4N and 509+4N): the band
; starts at the unit address 0x0030 (after its 13-byte program), so the
; first segment takes 16384-0x30 bytes of page 0 and the second the rest of
; the 19200-byte band on page 1. The page words at frame offsets 12/13 and
; 36/37 are patched per unit before submission.
seg_frame:
    DB 1,9,16,0, 1,0,0,0 ; version 1, op 9, payload 16, tag 1.
    DB 1,1,0,0           ; version 1, ram-to-card, reserved, reserved.
    DB 0,0              ; page low/high (patched)
    DB 0x30,0x00        ; offset 0x0030
    DB 0xD0,0x3F,0,0    ; count 16336
    DB 1,0,0,0          ; segment tag 1
    DB 1,9,16,0, 1,0,0,0
    DB 1,1,0,0
    DB 0,0              ; page low/high (patched)
    DB 0,0              ; offset 0
    DB 0x30,0x0B,0,0    ; count 2864
    DB 1,0,0,0          ; segment tag 1

; --- Unit band programs (16 bytes each, entry 0): fill 0x0030..0x4B2F with
; --- the unit's palette index, then HALT. The unit Z80 boots at 0.
; --- Keep the colour in D: A is reused for the BC loop test.
prog1: DB 0x21,0x30,0x00, 0x16,1, 0x01,0x00,0x4B, 0x72,0x23,0x0B,0x78,0xB1,0x20,0xF9,0x76
prog2: DB 0x21,0x30,0x00, 0x16,2, 0x01,0x00,0x4B, 0x72,0x23,0x0B,0x78,0xB1,0x20,0xF9,0x76
prog3: DB 0x21,0x30,0x00, 0x16,3, 0x01,0x00,0x4B, 0x72,0x23,0x0B,0x78,0xB1,0x20,0xF9,0x76
prog4: DB 0x21,0x30,0x00, 0x16,4, 0x01,0x00,0x4B, 0x72,0x23,0x0B,0x78,0xB1,0x20,0xF9,0x76
progo: DW prog1,prog2,prog3,prog4

; --- Controller frame staging and receipts (main RAM). ---
FRAME = 0x9010            ; 40-byte header + up to 24 payload bytes.
RCV   = 0x9060            ; 40-byte receipt header + up to 32 payload bytes.
CELL  = 0x9200            ; per unit: resource, execution, map epochs (3 x u32).
FIELD = 0x9008            ; controller header field: required nonzero value.
uoff:  DW 0, 12, 24, 36   ; the per-unit cell offset.

; cell_base: HL <- CELL + (C-1) x 12. Clobbers A, B, D, E, H, L.
cell_base:                 ; HL <- CELL + (C-1) * 12 (res, exec, map cells).
    LD A,C
    DEC A
    LD B,A
    ADD A,A
    ADD A,A                 ; A 4(C-1).
    LD D,A
    ADD A,A
    ADD A,A                 ; A 16(C-1).
    SUB D                   ; A 12(C-1).
    LD D,A
    LD E,0
    LD HL,0
    ADD HL,DE
    LD DE,CELL
    ADD HL,DE
    RET

; epoch_write: (DE) <- the 64-bit epoch (zero-extended u32) at (HL); HL += 4.
epoch_write:
    LD A,(HL)
    LD (DE),A
    INC DE
    INC HL
    LD A,(HL)
    LD (DE),A
    INC DE
    INC HL
    LD A,(HL)
    LD (DE),A
    INC DE
    INC HL
    LD A,(HL)
    LD (DE),A
    INC DE
    INC HL
    LD B,4
epoch_zero:
    XOR A
    LD (DE),A
    INC DE
    DJNZ epoch_zero
    RET

; send_frame: DE the frame start, B its byte length (40..61), C the unit.
; Selects the unit, collects, streams, submits, then drains any receipt.
; Returns A = result; preserves BC so the unit id survives.
send_frame:
    LD H,B
    PUSH BC
    LD A,C
    LD BC,0x7BFD
    OUT (C),A
    LD A,0
    LD BC,0x7AFD
    OUT (C),A
    LD A,1
    LD BC,0x79FD
    OUT (C),A
    LD BC,0x78FD
sf_byte:
    LD A,(DE)
    OUT (C),A
    INC DE
    DEC H
    JR NZ,sf_byte
    LD A,2
    LD BC,0x79FD
    OUT (C),A
    LD BC,0x77FD
    IN A,(C)
    AND 0x08
    JR Z,sf_result
    LD A,0xFF             ; a unit faulted.
    POP BC
    RET
sf_result:
    LD BC,0x77FD
    IN A,(C)
    AND 0x02
    JR Z,sf_no_receipt
    LD DE,RCV
    LD H,40
rcv_byte:
    LD BC,0x78FD
    IN A,(C)
    LD (DE),A
    INC DE
    DEC H
    JR NZ,rcv_byte
    LD A,(RCV+4)          ; payload length (the upper bytes are zero).
    LD H,A
    LD DE,RCV+40
    OR A
    JR Z,sf_clear
rcv_payload:
    LD BC,0x78FD
    IN A,(C)
    LD (DE),A
    INC DE
    DEC H
    JR NZ,rcv_payload
sf_clear:
    LD A,5
    LD BC,0x79FD
    OUT (C),A
    LD A,(RCV+1)
    POP BC
    RET
sf_no_receipt:
    LD BC,0x77FD
    IN A,(C)
    RRCA
    RRCA
    RRCA
    RRCA
    AND 0x0F
    POP BC
    RET

; header40: (DE) <- version, opcode (A), reserved 0, reserved 0, payload
; length (B), the required nonzero field (FIELD; always 1), then the unit's
; three epochs. DE ends at the first payload byte. Clobbers A, B, D, E, H, L.
header40:
    PUSH AF
    LD A,1
    LD (DE),A
    INC DE
    POP AF
    LD (DE),A
    INC DE
    XOR A
    LD (DE),A
    INC DE
    LD (DE),A
    INC DE
    LD A,B
    LD (DE),A
    INC DE
    XOR A
    LD (DE),A
    INC DE
    LD (DE),A
    INC DE
    LD (DE),A
    INC DE
    LD A,(FIELD)
    LD (DE),A
    INC DE
    XOR A
    LD B,7
header_field:
    LD (DE),A
    INC DE
    DJNZ header_field
    PUSH DE
    CALL cell_base
    POP DE
    CALL epoch_write
    CALL epoch_write
    CALL epoch_write
    RET

fail_ee:                 ; A the result code.
    LD (0x9002),A
    LD A,0xEE
    LD (0x9000),A
    HALT

; query_unit: C the unit. Op 1 QUERY with all-zero epochs (legal for 1);
; the receipt carries the unit's current epochs into the cell.
query_unit:
    LD A,1
    LD (FIELD),A
    LD DE,FRAME
    LD B,0
    CALL header40
    LD DE,FRAME
    LD B,40
    CALL send_frame
    OR A
    JR NZ,query_failed
    ; Epochs in the receipt are u64; the cells hold the low u32 and the
    ; header builder zero-extends them back to 64 bits.
    CALL cell_base
    PUSH BC
    PUSH HL
    POP DE
    LD HL,RCV+16
    LD BC,4
    LDIR
    LD HL,RCV+24
    LD BC,4
    LDIR
    LD HL,RCV+32
    LD BC,4
    LDIR
    POP BC
    RET
query_failed:
    PUSH AF
    LD A,1
    LD (0x9001),A
    POP AF
    JP fail_ee

; reset_unit: C the unit. Make its instruction frontier explicitly retired
; before replacing its image, then advance the local execution epoch.
reset_unit:
    LD A,1
    LD (FIELD),A
    LD A,2
    LD DE,FRAME
    LD B,0
    CALL header40
    LD DE,FRAME
    LD B,40
    CALL send_frame
    OR A
    JR NZ,reset_failed
    CALL cell_base
    LD DE,4
    ADD HL,DE
    INC (HL)
    RET
reset_failed:
    PUSH AF
    LD A,2
    LD (0x9001),A
    POP AF
    JP fail_ee

; replace_image: C the unit. Op 4, payload = u16 entry 0, u16 zero, u32
; count 16, then the unit's 16-byte program. On success execution epoch rises.
replace_image:
    LD A,C
    DEC A
    ADD A,A
    ADD A,A
    ADD A,A
    ADD A,A             ; each unit's program is 16 bytes.
    LD E,A
    LD D,0
    LD HL,prog1
    ADD HL,DE             ; HL points at progN (the programs are contiguous).
    PUSH HL               ; header40 uses HL; preserve the ROM source address.
    LD A,1
    LD (FIELD),A
    LD A,4
    LD DE,FRAME
    LD B,24
    CALL header40
    XOR A
    LD (DE),A
    INC DE
    LD (DE),A             ; entry offset 0.
    INC DE
    LD (DE),A
    INC DE
    LD (DE),A             ; reserved zero.
    INC DE
    LD A,16
    LD (DE),A
    INC DE
    XOR A
    LD (DE),A
    INC DE
    LD (DE),A
    INC DE
    LD (DE),A             ; count 16.
    INC DE
    POP HL                ; source; DE points to image bytes in the frame.
    LD B,16
prog_copy:
    LD A,(HL)
    LD (DE),A
    INC HL
    INC DE
    DJNZ prog_copy
    LD DE,FRAME
    LD B,64
    CALL send_frame
    OR A
    JR NZ,replace_failed
    CALL cell_base
    LD DE,4
    ADD HL,DE
    INC (HL)              ; execution epoch += 1.
    RET
replace_failed:
    PUSH AF
    LD A,4
    LD (0x9001),A
    POP AF
    JP fail_ee

; start_unit: C the unit. Op 7, no payload, the current epochs.
start_unit:
    LD A,1
    LD (FIELD),A
    LD A,7
    LD DE,FRAME
    LD B,0
    CALL header40
    LD DE,FRAME
    LD B,40
    CALL send_frame
    CP 0
    RET Z
    PUSH AF
    LD A,7
    LD (0x9001),A
    POP AF
    JP fail_ee

; wait_dma_done: wait for the DMA-complete source to reach the expected count,
; then issue op 11 (AcknowledgeSegment) to return the DMA channel to Idle.
; The shared line wakes the HALTed main; clobbers AF, BC, DE.
wait_dma_done:
wait_dma_loop:
    LD A,(0x9004)
    LD B,A
    LD A,(0x9008)
    CP B
    JR Z,wait_dma_ack
    HALT
    JR wait_dma_loop
wait_dma_ack:
    LD HL,ack_frame
    LD D,8
    CALL submit_checked
    RET

; lend_pages: C the unit. Op 17 LEND, payload = u16 card id (3), u16 page:
; page 508+4C and the page after it. The first 40-byte header is reused for
; the second frame (same unit, same epochs, same op); only the payload
; bytes differ.
lend_pages:
    LD HL,508
    LD A,C
    ADD A,A
    ADD A,A
    LD DE,0
    LD E,A
    ADD HL,DE
    LD A,L
    LD (0x9006),A          ; preserve page low across header40.
    LD A,H
    LD (0x9007),A          ; page high (2 for the compute roster).
    LD A,1
    LD (FIELD),A
    LD A,17
    LD DE,FRAME
    LD B,4
    CALL header40          ; DE at the first frame's payload byte.
    LD A,3
    LD (DE),A
    INC DE
    XOR A
    LD (DE),A
    INC DE
    LD A,(0x9006)
    LD (DE),A
    INC DE
    LD A,(0x9007)
    LD (DE),A              ; payload 1: card 3, page 508+4C.
    LD DE,FRAME
    LD B,44
    CALL send_frame
    OR A
    JR NZ,lend_failed

    LD A,1
    LD (FIELD),A
    LD A,17
    LD DE,FRAME
    LD B,4
    CALL header40          ; build second frame with the same unit epochs.
    LD A,3
    LD (DE),A
    INC DE
    XOR A
    LD (DE),A
    INC DE
    LD A,(0x9006)
    INC A
    LD (DE),A
    INC DE
    LD A,(0x9007)
    LD (DE),A              ; payload 2: card 3, page 509+4C.
    LD DE,FRAME
    LD B,44
    CALL send_frame
    OR A
    RET Z
lend_failed:
    PUSH AF
    LD A,17
    LD (0x9001),A
    POP AF
    JP fail_ee

; --- X2 submission (from x2.service.asm). ---
INCLUDE "../x2-service/x2.service.asm"

; run_list: HL a list of frames (length byte, then the frame); submit each;
; stop at 0. submit_checked records a refusal and halts.
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
submit_checked:
    PUSH HL
    CALL x2_submit
    POP HL
    OR A
    RET Z
    INC HL
    LD A,(HL)
    LD (0x9001),A
    DEC HL
    LD A,(HL)
    LD (0x9002),A
    LD A,0xEE
    LD (0x9000),A
    HALT
