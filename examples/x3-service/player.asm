; ===========================================================================
; player.asm -- X3MOD: a small MOD player for the X3 sentinel module
;
; What it is:  a BASIC USR routine (PRINT USR 32768) that plays the embedded
;              sentinel module (sentinel.data.asm) on the X3 sound card. It
;              checks that the module is the narrow kind it supports, copies
;              the four samples into arena page 10 (mapping the page in at
;              0xC000 for the copy, then restoring the normal map), starts the
;              notes of each row on channels 0-3 with X3 START, paces the 64
;              rows by the card's own clock, and stops all four channels at
;              the end. It is an example, not a general MOD player.
;              Result: 0 = played, 1 = unsupported module, 2 = map refused,
;              3 = X3 refused.
; Builds:      X3MOD.tap (CODE block "X3MOD" at 32768), by
;              tools/ExpansionMailboxBuilder (X3ServiceAssets.cs); includes
;              x3.service.asm and sentinel.data.asm.
; Runs on:     extended HC2000, full X5 with the X3 sound card owned by the
;              main Z80, a fresh main map, an idle expansion controller and
;              arena page 10 free for this program; from BASIC after
;              CLEAR 32767; loads at ORG 0x8000.
; Interface:   entry at 0x8000 by USR; returns BC = result. Disables
;              interrupts while it runs and enables them on return; preserves
;              IX, IY and BC', DE', HL'. Uses 0x8000-0xBFFF (code, data, stack
;              at 0xBFF0) and the first 256 bytes of arena page 10. Ports:
;              root 0x7BFD / 0x7AFD, 0x79FD, 0x78FD, 0x77FD; X3 0x789D,
;              0x799D.
; Provenance:  house code (hc2000-emu); a public example source, shipped with
;              the installer under examples/x3-service/.
; ===========================================================================
;
; MOD terms used below (ProTracker "M.K." layout): a 20-byte title, 31
; instrument headers of 30 bytes, the song length at offset 950, the order
; table at 952, "M.K." at 1080, then 1024-byte patterns of 64 rows x 4
; channels x 4 bytes, then the sample data. A note is 4 bytes: instrument
; high nibble + period high nibble, period low byte, instrument low nibble +
; effect, effect parameter. The period sets the pitch; X3 takes the same
; period value.
;
; Finite four-channel M.K. subset player for the included original sentinel.
; Fresh X5 main map, main-owned X3 and exclusive use of arena page 10 required.
; The caller reserves 8000..BFFF. Interrupts are enabled on return.
ORG 0x8000
    JP player_entry
; player_entry: save what BASIC needs back, then run on a private stack.
player_entry:
    DI
    PUSH IX
    PUSH IY
    EXX
    PUSH BC
    PUSH DE
    PUSH HL
    EXX
    LD (caller_sp),SP
    LD SP,0xBFF0
    XOR A
    LD (result_code),A
    LD (rows_done),A
; Refuse anything but the supported module shape (result 1).
    CALL validate_mod
    OR A
    JP NZ,invalid_mod
; The root channel must be idle (status bits 0-2 clear), then address unit 0
; (the root) for the two MAP requests.
    LD BC,0x77FD
    IN A,(C)
    AND 7
    JP NZ,map_failed
    LD BC,0x7BFD
    XOR A
    OUT (C),A
    LD BC,0x7AFD
    OUT (C),A
; Map arena page 10 at 0xC000, copy the four 64-byte samples into it, and
; map the normal memory back. If the restore fails, stop here (below).
    LD HL,map_sample
    CALL map_transaction
    OR A
    JP NZ,map_failed
    LD HL,mod_samples
    LD DE,0xC000
    LD BC,256
    LDIR
    LD HL,map_restore
    CALL map_transaction
    OR A
    JP NZ,restore_failed
; Start the row clock: deadline = the card's master tick, taken from a QUERY
; of channel 0 (reply+32: the low 32 bits of the observed master tick).
    CALL query_channel_zero
    OR A
    JP NZ,sound_failed
    LD HL,(reply+32)
    LD (deadline),HL
    LD HL,(reply+34)
    LD (deadline+2),HL
; row_loop: for each of the 64 rows of the single pattern, for channels 0-3.
    LD IX,mod_pattern
row_loop:
    XOR A
    LD (channel),A
channel_loop:
; Period = low nibble of note byte 0 (high bits) and note byte 1. A note
; with period 0 is "no new note": leave that channel alone.
    LD A,(IX+0)
    AND 15
    LD B,A
    LD A,(IX+1)
    OR B
    JR Z,next_channel
    LD A,(IX+1)
    LD (start_period),A
    LD A,B
    LD (start_period+1),A
; Instrument = the high nibble of note byte 2 (1..4, checked earlier); its
; sample sits at (instrument - 1) * 64 in page 10.
    LD A,(IX+2)
    RRCA
    RRCA
    RRCA
    RRCA
    DEC A
    LD E,A
    ADD A,A
    ADD A,A
    ADD A,A
    ADD A,A
    ADD A,A
    ADD A,A
    LD (sample_offset),A
    LD A,(channel)
    LD (start_channel),A
; From the instrument's header: the volume (+25), and the loop length's low
; byte (+29): 1 = one-shot (X3 loop length 0), otherwise a full 64-byte loop.
    LD A,E
    ADD A,A
    LD E,A
    LD D,0
    LD HL,instrument_headers
    ADD HL,DE
    LD E,(HL)
    INC HL
    LD D,(HL)
    EX DE,HL
    LD DE,25
    ADD HL,DE
    LD A,(HL)
    LD (start_volume),A
    INC HL
    INC HL
    INC HL
    INC HL
    LD A,(HL)
    CP 1
    LD A,0
    JR Z,one_shot
    LD A,64
one_shot:
    LD (loop_length),A
; X3 START on this channel with the frame filled in above.
    LD HL,start_frame
    LD DE,reply
    LD A,32
    CALL x3_submit
    OR A
    JP NZ,sound_failed
next_channel:
    INC IX
    INC IX
    INC IX
    INC IX
    LD A,(channel)
    INC A
    LD (channel),A
    CP 4
    JR NZ,channel_loop
; Next row deadline, a 32-bit add of 2,128,137 (0x00207909) master ticks.
    ; 17,734,475 Hz * 6 ticks * 5/(2*125) = 2,128,137 ticks per row.
    LD HL,(deadline)
    LD DE,0x7909
    ADD HL,DE
    LD (deadline),HL
    LD HL,(deadline+2)
    LD DE,0x20
    ADC HL,DE
    LD (deadline+2),HL
; wait_row: QUERY channel 0 until its observed master tick has reached the
; deadline (signed 32-bit difference, so a wrap of the counter is harmless).
wait_row:
    CALL query_channel_zero
    OR A
    JP NZ,sound_failed
    LD HL,(reply+32)
    LD DE,(deadline)
    OR A
    SBC HL,DE
    LD HL,(reply+34)
    LD DE,(deadline+2)
    SBC HL,DE
    BIT 7,H
    JR NZ,wait_row
; 64 rows done: stop all channels and return 0.
    LD A,(rows_done)
    INC A
    LD (rows_done),A
    CP 64
    JP NZ,row_loop
    CALL stop_channels
    JP finish
; Failure exits set the result code. A failed restore of the map is the one
; case that must not return (see the note below).
invalid_mod:
    LD A,1
    LD (result_code),A
    JP finish
map_failed:
    LD A,2
    LD (result_code),A
    JP finish
restore_failed:
    ; The caller's stack could be behind the still-mapped page: do not return to it.
    HALT
    JR restore_failed
sound_failed:
    LD A,3
    LD (result_code),A
    CALL stop_channels
; finish: restore SP and the saved registers, BC = result, interrupts on.
finish:
    LD SP,(caller_sp)
    EXX
    POP HL
    POP DE
    POP BC
    EXX
    POP IY
    POP IX
    LD A,(result_code)
    LD C,A
    LD B,0
    EI
    RET

; validate_mod: Out: A = 0 when the module is supported, 1 otherwise.
; Clobbers BC, DE, HL, IX. Checks, in order:
;   "M.K." at 1080; song length (950) = 1; first order (952) = pattern 0;
;   instruments 1-4: length 32 words (64 bytes), finetune 0, volume <= 64,
;   loop start 0, loop length 1 word (no loop) or 32 words (full loop);
;   instruments 5-31: length 0;
;   all 256 notes of pattern 0: no effect, no instrument above 15 (byte 0
;   high nibble 0), an empty note has no instrument, and a real note has
;   period >= 124 and instrument 1..4.
; Accept only one M.K. order/pattern, four 64-byte samples with zero
; finetune and a zero-start full loop or the ProTracker one-word no-loop.
; Every note must explicitly name instrument 1..4 and use effect 000.
validate_mod:
    LD HL,mod_file+1080
    LD DE,signature
    LD B,4
validate_signature:
    LD A,(DE)
    CP (HL)
    JP NZ,invalid_data
    INC HL
    INC DE
    DJNZ validate_signature
    LD A,(mod_file+950)
    CP 1
    JP NZ,invalid_data
    LD A,(mod_file+952)
    OR A
    JP NZ,invalid_data
    LD IX,mod_file+20
    LD B,4
validate_instrument:
    LD A,(IX+22)
    OR (IX+24)
    OR (IX+26)
    OR (IX+27)
    OR (IX+28)
    JP NZ,invalid_data
    LD A,(IX+23)
    CP 32
    JP NZ,invalid_data
    LD A,(IX+25)
    CP 65
    JP NC,invalid_data
    LD A,(IX+29)
    CP 1
    JR Z,valid_loop
    CP 32
    JR NZ,invalid_data
valid_loop:
    LD DE,30
    ADD IX,DE
    DJNZ validate_instrument
    LD B,27
validate_unused:
    LD A,(IX+22)
    OR (IX+23)
    JR NZ,invalid_data
    LD DE,30
    ADD IX,DE
    DJNZ validate_unused
    LD IX,mod_pattern
    LD HL,256
validate_note:
    LD A,(IX+0)
    AND 0xF0
    JR NZ,invalid_data
    LD A,(IX+2)
    AND 15
    OR (IX+3)
    JR NZ,invalid_data
    LD A,(IX+0)
    OR (IX+1)
    JR NZ,has_period
    LD A,(IX+2)
    OR A
    JR NZ,invalid_data
    JR valid_note
has_period:
    LD A,(IX+0)
    OR A
    JR NZ,period_valid
    LD A,(IX+1)
    CP 124
    JR C,invalid_data
period_valid:
    LD A,(IX+2)
    RRCA
    RRCA
    RRCA
    RRCA
    CP 1
    JR C,invalid_data
    CP 5
    JR NC,invalid_data
valid_note:
    INC IX
    INC IX
    INC IX
    INC IX
    DEC HL
    LD A,H
    OR L
    JR NZ,validate_note
    XOR A
    RET
invalid_data:
    LD A,1
    RET

; query_channel_zero: X3 QUERY of channel 0 into reply. Out: A = result.
query_channel_zero:
    LD HL,query_frame
    LD DE,reply
    LD A,12
    JP x3_submit
; stop_channels: X3 STOP on channels 0-3. Any refusal sets result 3, but all
; four are still sent.
stop_channels:
    XOR A
    LD (stop_channel),A
stop_loop:
    LD HL,stop_frame
    LD DE,reply
    LD A,12
    CALL x3_submit
    OR A
    JR Z,stop_next
    LD A,3
    LD (result_code),A
stop_next:
    LD A,(stop_channel)
    INC A
    LD (stop_channel),A
    CP 4
    JR NZ,stop_loop
    RET

; A fresh main map has unit epoch 1, map epoch 1 and four Legacy slots.
; map_transaction: send the 120-byte MAP request at HL to the root and check
; the reply: version 1, no payload, and the request id echoed (low byte in
; map_tag, the rest zero). MAP answers at once, so "reply ready" (status
; bit 1) must already be set. Out: A = the result (0 = accepted), 0xFF when
; the channel was busy or the reply is wrong. Clobbers BC, DE, HL.
map_transaction:
    PUSH HL
    LD BC,0x77FD
    IN A,(C)
    AND 7
    JR NZ,map_busy
    POP HL
    PUSH HL
    LD DE,8
    ADD HL,DE
    LD A,(HL)
    LD (map_tag),A
    POP HL
    PUSH HL
    LD BC,0x79FD
    LD A,1
    OUT (C),A
    POP HL
    LD DE,120
    LD BC,0x78FD
map_write:
    LD A,(HL)
    OUT (C),A
    INC HL
    DEC DE
    LD A,D
    OR E
    JR NZ,map_write
    LD BC,0x79FD
    LD A,2
    OUT (C),A
    LD BC,0x77FD
    IN A,(C)
    AND 2
    JR Z,map_no_reply
    LD HL,map_reply
    LD DE,40
    LD BC,0x78FD
map_read:
    IN A,(C)
    LD (HL),A
    INC HL
    DEC DE
    LD A,D
    OR E
    JR NZ,map_read
    LD BC,0x79FD
    LD A,5
    OUT (C),A
    LD A,(map_reply)
    CP 1
    JR NZ,map_no_reply
    LD A,(map_reply+4)
    LD HL,map_reply+5
    OR (HL)
    INC HL
    OR (HL)
    INC HL
    OR (HL)
    JR NZ,map_no_reply
    LD A,(map_reply+8)
    LD HL,map_tag
    CP (HL)
    JR NZ,map_no_reply
    LD HL,map_reply+9
    LD B,7
map_tag_check:
    LD A,(HL)
    OR A
    JR NZ,map_no_reply
    INC HL
    DJNZ map_tag_check
    LD A,(map_reply+1)
    RET
map_busy:
    POP HL
map_no_reply:
    LD A,0xFF
    RET
; The X3 command service: x3_submit.
INCLUDE "x3.service.asm"
; Constant and working data.
;   instrument_headers: addresses of the four instrument headers in mod_file.
;   deadline: 32-bit master tick of the next row.
signature: DB "M.K."
instrument_headers:
    DW mod_file+20,mod_file+50,mod_file+80,mod_file+110
caller_sp: DW 0
deadline: DS 4,0
result_code: DB 0
rows_done: DB 0
channel: DB 0
; X3 requests, built in place (8-byte header, then the payload):
;   query_frame: QUERY channel 0.  stop_frame: STOP, channel in stop_channel.
;   start_frame: START: channel, 0, page 10, sample_offset, length 64, loop
;   start 0, loop_length, start_period, start_volume and three zero bytes.
query_frame: DB 1,4,4,0,0,0,0,0,0,0,0,0
stop_frame: DB 1,2,4,0,0,0,0,0
stop_channel: DB 0,0,0,0
start_frame: DB 1,1,24,0,0,0,0,0
start_channel: DB 0,0
    DW 10
sample_offset: DW 0
    DB 64,0,0,0
    DS 4,0
loop_length: DB 0,0,0,0
start_period: DW 428
start_volume: DB 0,0,0,0
; reply: the last X3 reply. map_reply / map_tag: the last MAP reply header
; and the id it must echo.
reply: DS 48,0
map_reply: DS 40,0
map_tag: DB 0
; MAP requests (operation 14, 80-byte payload), as in map-exercise.asm:
; map_sample puts arena page 10 (kind 2) at 0xC000; map_restore puts all
; four slots back to legacy memory (kind 5). Both carry the fresh main
; map's epochs (1), and the expected map epoch 1, then 2.
map_sample:
    DB 1,14,0,0,80,0,0,0,1,0,0,0,0,0,0,0
    DB 1,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0
    DB 1,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0
    DB 5,7,0,0,0,0
    DS 10,0
    DB 5,7,0,0,0,0
    DS 10,0
    DB 5,7,0,0,0,0
    DS 10,0
    DB 2,7,10,0,0,0
    DS 10,0
map_restore:
    DB 1,14,0,0,80,0,0,0,2,0,0,0,0,0,0,0
    DB 1,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0
    DB 1,0,0,0,0,0,0,0,2,0,0,0,0,0,0,0
    DB 5,7,0,0,0,0
    DS 10,0
    DB 5,7,0,0,0,0
    DS 10,0
    DB 5,7,0,0,0,0
    DS 10,0
    DB 5,7,0,0,0,0
    DS 10,0
; The embedded module (mod_file, mod_pattern, mod_samples).
INCLUDE "sentinel.data.asm"
; Keep code and data below 0xB000, clear of the stack under 0xBFF0.
ASSERT $ <= 0xB000
