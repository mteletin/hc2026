; ===========================================================================
; main.asm -- EXPMAIL main-Z80 program: one mailbox round per USR call
;
; What it is:  a BASIC USR routine (PRINT USR 32768) that drives compute
;              unit 1 of the X5 expansion through the controller's root
;              request/response protocol. The first call clears the unit's
;              memory, loads side.asm into it, sends the round-1 challenge to
;              its mailbox and starts it; each later call sends the next round
;              to the same waiting job. Every reply is checked byte by byte.
;              Result: 1000 + round on success (1001, 1002), or 9001 (no
;              expansion), 9002 (refused or wrong reply / state), 9003 (both
;              rounds already done), 9004 (polling budget used up).
; Builds:      EXPMAIL.tap (CODE block "EXPMAIL" at 32768), by
;              tools/ExpansionMailboxBuilder (ExpansionMailboxAssets.cs):
;              side.asm is assembled to side.bin first, then this file, which
;              embeds side.bin with INCBIN (so it does not assemble alone).
; Runs on:     extended HC2000 with paged memory and the X5 expansion
;              (memory=paged, m-view=true, side-z80=16), from BASIC after
;              CLEAR 32767; loads at ORG 0x8000.
; Interface:   entry at 0x8000 by USR; returns BC = the result to BASIC.
;              Preserves IX, IY and the alternate BC', DE', HL' (BASIC needs
;              HL' intact). Root ports: 0x7BFD / 0x7AFD (unit select),
;              0x79FD (control), 0x78FD (data), 0x77FD (status), 0x76FD
;              (presence). All its state lives inside the program, so it
;              survives between USR calls.
; Provenance:  house code (hc2000-emu); a source of the public example in
;              examples/expansion-mailbox/. The installer ships the tape built
;              from it (EXPMAIL.tap), not this file.
; ===========================================================================
;
; The root protocol, as used here (main Z80 side):
;   0x7BFD, 0x7AFD  unit to address, low / high byte (0 = the root itself,
;                   1..16 = compute units). Latched when a request begins.
;   0x79FD (write)  control: 1 = begin a request, 2 = submit it, 3 = abort,
;                   4 = rewind the reply, 5 = release the reply.
;   0x78FD          write = next request byte; read = next reply byte.
;   0x77FD (read)   status: bit 0 = request open, bit 1 = reply ready,
;                   bit 2 = control pending, bit 3 = unit faulted,
;                   bits 4-7 = last result code.
;   0x76FD (read)   1 when the expansion controller is present.
;   Request = 40-byte header + payload; reply = 40-byte header + payload:
;     +0 version (1)        +1 operation (request) / result (reply)
;     +2 the unit's lifecycle (reply; zero in a request)   +3 zero
;     +4..+7   payload length, 32-bit little-endian
;     +8..+15  request id, 64-bit, nonzero; the reply echoes it
;     +16, +24, +32  resource, execution and map epochs (64-bit each). A
;              request must carry the unit's current epochs; only a QUERY may
;              send all three as zero.
;   Operations used: 1 QUERY, 3 CLEAR (fill memory), 4 LOAD (copy bytes in),
;     7 START, 11 SEND (mailbox frame in), 12 RECEIVE (mailbox frame out),
;     13 ACK (free the unit's outbox; payload = the reply frame's 8-byte id).
;   Results: 0 = accepted; anything else is a refusal.
;   Lifecycles: 0 = reset/stopped, 1 = running, 2 = paused, 3 = halted,
;     4 = faulted, 5 = service idle, 6 = waiting for mailbox.
;   Mailbox frame (73 bytes): +0 payload length, +1..+8 id, +9.. payload,
;     zero after the payload (see side.asm).
;
; EXPMAIL: ordinary BASIC USR entry, result in BC; no host execution seam.
ORG 0x8000
; entry: save what BASIC needs back (IX, IY, the alternate registers) and
; the caller's SP, so every exit path can restore them at finish.
entry:
    PUSH IX
    PUSH IY
    EXX
    PUSH BC
    PUSH DE
    PUSH HL
    EXX
    LD (caller_sp),SP
; No controller (0x76FD does not read 1): result 9001.
    LD BC,0x76FD
    IN A,(C)
    CP 1
    JP NZ,absent
; stage = rounds completed so far (0, 1 or 2); round = the one to run now.
    LD A,(stage)
    CP 2
    JP NC,complete
    INC A
    LD (round),A
; Address compute unit 1, then QUERY it. query also checks that the unit
; describes itself as expected and, on the very first call, learns the
; three epochs that every later request must carry.
    LD BC,0x7BFD
    LD A,1
    OUT (C),A
    LD BC,0x7AFD
    XOR A
    OUT (C),A
    CALL query
; First call: the unit must still be reset/stopped (lifecycle 0). CLEAR it
; (fill all its memory with 0, payload = the byte 0) and LOAD the side job
; at address 0 (payload = load_payload).
    LD A,(stage)
    OR A
    JP NZ,continued
    LD A,(response+2)
    OR A
    JP NZ,invalid
    LD A,3
    LD HL,zero
    LD DE,1
    CALL rpc
    LD A,4
    LD HL,load_payload
    LD DE,load_end-load_payload
    CALL rpc
    JR send_round
; Later calls: the job must be waiting on its mailbox (lifecycle 6).
continued:
    LD A,(response+2)
    CP 6
    JP NZ,invalid
; send_round: fill in the mailbox frame: id = round, payload = round * 17
; (0x11, 0x22); the expected reply byte is that value XOR 0xFF. SEND puts the
; frame in the unit's inbox; on later rounds this also wakes the WAIT.
send_round:
    LD A,(round)
    LD (mail+1),A
    LD D,A
    ADD A,A
    ADD A,A
    ADD A,A
    ADD A,A
    ADD A,D
    LD (mail+9),A
    XOR 255
    LD (answer),A
    LD A,11
    LD HL,mail
    LD DE,73
    CALL rpc
; First call only: START the unit; it runs side.asm from address 0.
    LD A,(stage)
    OR A
    JR NZ,wait_reply
    LD A,7
    CALL empty_rpc
; Poll with QUERY until the job has replied and gone back to WAIT
; (lifecycle 6). Running (1) means not yet; any other lifecycle fails.
wait_reply:
    LD HL,4095 ; plus the initial QUERY: at most 4096 per USR call
    LD (queries_left),HL
poll_lifecycle:
    CALL query
    LD A,(response+2)
    CP 6
    JR Z,read_reply
    CP 1
    JP NZ,invalid
    LD HL,(queries_left)
    DEC HL
    LD (queries_left),HL
    LD A,H
    OR L
    JP Z,timed_out
    JR poll_lifecycle
; read_reply: RECEIVE the unit's outbox frame (73 bytes at response+40) and
; check it: payload length 2, the same id as our frame, payload byte 0 =
; round, byte 1 = the expected reply, and the remaining 62 bytes zero.
read_reply:
    LD A,12
    CALL empty_rpc
    LD A,(response+2)
    CP 6
    JP NZ,invalid
    LD A,(response+40)
    CP 2
    JP NZ,invalid
    LD HL,response+41
    LD DE,mail+1
    LD B,8
    CALL equal
    LD A,(round)
    LD D,A
    LD A,(response+49)
    CP D
    JP NZ,invalid
    LD A,(answer)
    LD D,A
    LD A,(response+50)
    CP D
    JP NZ,invalid
    LD HL,response+51
    LD B,62
    CALL zeros
; ACK the reply by its id (8-byte payload), which frees the unit's outbox,
; then record the round and return BC = 1000 + round.
    LD A,13
    LD HL,mail+1
    LD DE,8
    CALL rpc
    LD A,(round)
    LD (stage),A
    LD C,A
    LD B,0
    LD HL,1000
    ADD HL,BC
    LD B,H
    LD C,L
    JP finish
; Error exits: BC = 9001 .. 9004, then finish.
absent:
    LD BC,9001
    JP finish
invalid:
    LD BC,9002
    JP finish
complete:
    LD BC,9003
    JP finish
timed_out:
    LD BC,9004
; finish: restore the caller's SP and saved registers; return BC to BASIC.
finish:
    LD SP,(caller_sp)
    EXX
    POP HL
    POP DE
    POP BC
    EXX
    POP IY
    POP IX
    RET
; query: QUERY the selected unit and require its 32-byte description to
; match query_identity exactly. Clobbers all except IX, IY.
query:
    LD A,1
    CALL empty_rpc
    LD HL,response+40
    LD DE,query_identity
    LD B,32
    CALL equal
    RET
; empty_rpc: rpc with no payload (HL = a dummy pointer, DE = 0).
empty_rpc:
    LD HL,zero
    LD DE,0
; A=operation, HL=payload, DE=length. Every reply is released, even refusal.
; rpc: one complete request / reply exchange with the selected unit.
;   In:   A = operation, HL = payload, DE = payload length (under 64 KB).
;   Out:  returns only when the reply is accepted and as expected, with the
;         reply header at response and its payload at response+40.
;         Any problem jumps to invalid (9002) or timed_out (9004).
;   Clobbers A, BC, DE, HL.
; Each request gets a fresh id (the 64-bit id is incremented first).
rpc:
    LD (request+1),A
    LD (payload_pointer),HL
    LD (request+4),DE
    LD HL,request+8
    CALL increment64
; Begin the request, write the 40-byte header and the payload, submit.
    LD BC,0x79FD
    LD A,1
    OUT (C),A
    LD HL,request
    LD DE,40
    CALL write_bytes
    LD HL,(payload_pointer)
    LD DE,(request+4)
    CALL write_bytes
    LD BC,0x79FD
    LD A,2
    OUT (C),A
; Wait for "reply ready" (status bit 1), at most 65535 status reads.
    LD BC,0x77FD
    LD DE,65535
ready:
    IN A,(C)
    AND 2
    JR NZ,read_header
    DEC DE
    LD A,D
    OR E
    JR NZ,ready
    JP timed_out
; Read the 40-byte reply header. Then check the payload length before
; reading it: only lengths below 74 fit in response; a longer reply is
; drained (or, if longer than any reply can be, released at once) and
; counts as invalid.
read_header:
    LD HL,response
    LD DE,40
    CALL read_bytes
    ; A recognized reply never exceeds 69636 payload bytes. Drain even refusals.
    LD A,(response+7)
    OR A
    JP NZ,release_invalid
    LD A,(response+6)
    CP 2
    JP NC,release_invalid
    OR A
    JR Z,short_length
    LD HL,(response+4)
    LD DE,0x1005
    OR A
    SBC HL,DE
    JP NC,release_invalid
    JP drain_invalid
; Short reply: read its payload into response+40, then release the reply
; (control 5) so the controller can take the next request.
short_length:
    LD A,(response+5)
    OR A
    JP NZ,drain_invalid
    LD A,(response+4)
    CP 74
    JP NC,drain_invalid
    LD E,A
    LD D,0
    LD HL,response+40
    CALL read_bytes
    LD BC,0x79FD
    LD A,5
    OUT (C),A
; The reply must be version 1, result 0 (accepted), byte 3 zero, and must
; echo our request id.
    LD A,(response)
    CP 1
    JP NZ,invalid
    LD A,(response+1)
    OR A
    JP NZ,invalid
    LD A,(response+3)
    OR A
    JP NZ,invalid
    LD HL,response+8
    LD DE,request+8
    LD B,8
    CALL equal
; Expected payload size by operation: QUERY 32, RECEIVE 73, all others 0.
    LD A,(request+1)
    LD D,0
    CP 1
    JR NZ,not_query
    LD D,32
not_query:
    CP 12
    JR NZ,expected_size
    LD D,73
expected_size:
    LD A,(response+4)
    CP D
    JP NZ,invalid
; The first reply only (to the first QUERY): take its three epochs as the
; unit's current ones and copy them into the request template.
    LD A,(initialized)
    OR A
    JR NZ,check_epochs
    ; First QUERY alone discovers the three nonzero 64-bit epochs.
    LD HL,response+16
    CALL nonzero64
    LD HL,response+24
    CALL nonzero64
    LD HL,response+32
    CALL nonzero64
    LD HL,response+16
    LD DE,request+16
    LD BC,24
    LDIR
    LD A,1
    LD (initialized),A
    RET
; Later replies: CLEAR and LOAD change the unit's memory image, which bumps
; its execution epoch; follow it in the template before comparing.
check_epochs:
    LD A,(request+1)
    CP 3
    JR Z,mutated
    CP 4
    JR NZ,compare_epochs
mutated:
    LD HL,request+24
    CALL increment64
compare_epochs:
    LD HL,response+16
    LD DE,request+16
    LD B,24
    CALL equal
; The lifecycle the reply must report for each operation:
;   QUERY: any. CLEAR, LOAD: 0 (reset/stopped).
;   SEND: 0 on the first round (not started yet), 1 later (the send woke it).
;   START: 1 (running). RECEIVE, ACK: 6 (waiting for mailbox).
    LD A,(request+1)
    CP 1
    RET Z
    CP 3
    JR Z,expect_reset
    CP 4
    JR Z,expect_reset
    CP 11
    JR NZ,not_send
    LD A,(stage)
    OR A
    JR Z,expect_reset
    JR expect_running
not_send:
    CP 7
    JR Z,expect_running
    LD D,6
    JR expected_lifecycle
expect_reset:
    LD D,0
    JR expected_lifecycle
expect_running:
    LD D,1
expected_lifecycle:
    LD A,(response+2)
    CP D
    JP NZ,invalid
    RET
; drain_invalid: read and discard the whole oversized payload (a 24-bit
; count: H = bits 16-23, DE = bits 0-15), then release it as invalid.
drain_invalid:
    LD A,(response+6)
    LD H,A
    LD DE,(response+4)
    LD BC,0x78FD
drain_loop:
    LD A,H
    OR D
    OR E
    JR Z,release_invalid
    IN A,(C)
    LD A,D
    OR E
    JR NZ,drain_low
    DEC H
drain_low:
    DEC DE
    JR drain_loop
; release_invalid: release the reply (control 5) and fail with 9002.
release_invalid:
    LD BC,0x79FD
    LD A,5
    OUT (C),A
    JP invalid
; write_bytes: send DE bytes from HL to the data port 0x78FD (none if DE = 0).
; Clobbers A, BC, DE, HL.
write_bytes:
    LD A,D
    OR E
    RET Z
    LD BC,0x78FD
write_loop:
    LD A,(HL)
    OUT (C),A
    INC HL
    DEC DE
    LD A,D
    OR E
    JR NZ,write_loop
    RET
; read_bytes: read DE bytes from the data port 0x78FD to HL (none if DE = 0).
; Clobbers A, BC, DE, HL.
read_bytes:
    LD A,D
    OR E
    RET Z
    LD BC,0x78FD
read_loop:
    IN A,(C)
    LD (HL),A
    INC HL
    DEC DE
    LD A,D
    OR E
    JR NZ,read_loop
    RET
; increment64: add 1 to the 64-bit little-endian number at HL. Wrapping past
; the top would reuse ids, so it fails instead. Clobbers B, HL.
increment64:
    LD B,8
increment_byte:
    INC (HL)
    RET NZ
    INC HL
    DJNZ increment_byte
    JP invalid
; nonzero64: fail unless the 64-bit number at HL is nonzero. Clobbers A, B, HL.
nonzero64:
    LD B,8
    XOR A
nonzero_byte:
    OR (HL)
    INC HL
    DJNZ nonzero_byte
    JP Z,invalid
    RET
; equal: fail unless the B bytes at HL and DE match. Clobbers A, B, DE, HL.
equal:
    LD A,(DE)
    CP (HL)
    JP NZ,invalid
    INC HL
    INC DE
    DJNZ equal
    RET
; zeros: fail unless the B bytes at HL are all zero. Clobbers A, B, HL.
zeros:
    LD A,(HL)
    OR A
    JP NZ,invalid
    INC HL
    DJNZ zeros
    RET
; State kept between USR calls (stage, initialized) and per call (round,
; answer, caller_sp, queries_left, payload_pointer). zero is the one-byte
; CLEAR payload (fill with 0) and the dummy pointer for empty payloads.
stage: DB 0
round: DB 0
answer: DB 0
initialized: DB 0
caller_sp: DW 0
queries_left: DW 0
payload_pointer: DW 0
zero: DB 0
; query_identity: the 32-byte QUERY reply this example expects from unit 1:
; unit id 1, role 1 (compute), 16 compute units, 64-byte mailbox payload,
; 0x4000-byte pages, 65544 (0x10008) maximum request payload, then two fixed
; 64-bit fields, 5 and 1.
query_identity:
    DW 1
    DB 1,0
    DW 16,64,0x4000,0,8,1
    DB 5,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0
; request: the 40-byte request header template (version 1; operation,
; length, id and epochs are filled in by rpc). response: the 40-byte reply
; header plus room for a 73-byte payload.
request: DB 1,0,0,0
    DS 36,0
response: DS 113,0
; mail: the outgoing mailbox frame, payload length 1 (id and payload are set
; per round).
mail: DB 1
    DS 72,0
; load_payload: the LOAD payload: target address 0 (16-bit), a zero word,
; the byte count (32-bit), then the side job's bytes.
load_payload: DW 0,0
    DW side_end-side_start,0
side_start: INCBIN "side.bin"
side_end:
load_end:
; The side job must fit in 4 KB, and this program must end below 0xC000.
ASSERT side_end-side_start <= 4096
ASSERT $ <= 0xC000
