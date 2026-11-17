; ===========================================================================
; x2.owned.asm -- main-side X2 client that goes through an owning driver Z80
;
; What it is:  x2_owned_submit sends one X2 frame when a driver Z80 owns the
;              X2 card's data port (the driver runs x2.driver.asm). The main
;              Z80 begins the frame on the X2 command port, mails the bytes to
;              the driver with a root SEND, polls RECEIVE for the driver's
;              receipt, ACKs it, then submits the frame and waits for the card.
;              On any failure it cancels the X2 frame and marks itself
;              "poisoned": every later call returns 0xFD until fresh client
;              state is built.
; Builds:      included by exercise-owned.asm, which the tests assemble
;              (tests/Hc2000.Machine.Tests/ExpansionX5SnapshotTests.cs). Not
;              part of X2VGA.tap.
; Runs on:     the main Z80 of the full X5, with a driver unit that is mapped,
;              started and owns the X2 device; no ORG of its own, it assembles
;              where it is included.
; Interface:   x2_owned_submit: HL = frame, D = length 1..32. Out: A = 0 done,
;              0xFF bad length or address range, 0xFD poisoned, 0xFE timeout
;              or refusal. Clobbers AF, BC, DE, HL. Before use, set x2_unit
;              and the three epochs in x2_request. Ports: the root protocol
;              (0x7BFD / 0x7AFD unit, 0x79FD control, 0x78FD data, 0x77FD
;              status; see main.asm of the expansion-mailbox example) and the
;              X2 command / status port 0x759D (see x2.service.asm).
; Provenance:  house code (hc2000-emu); a source of the public example in
;              examples/x2-service/, used by its tests (not itself copied
;              into the installer).
; ===========================================================================
;
; Explicit main-side X2 client for an already mapped, started and exclusively
; assigned driver. Set x2_unit and the three epochs in x2_request before use.
; HL = frame, D = length 1..32, not wrapping or overlapping this client's state.
; A=0 after actual completion, A=FF for invalid range, A=FD if already poisoned,
; A=FE on timeout or protocol refusal. Caller serializes access to the card and
; controller, and supplies readable frame bytes and writable client/stack RAM.
; A failed transaction attempts X2 cancellation and poisons this client. A
; Present already awaiting video may still complete; wait for card quiescence,
; reset the driver and controller channel, then construct fresh client state.
; Clobbers AF, BC, DE and HL. Caller owns this code, data and stack; this does
; not reserve BIOS or TPA RAM.
x2_owned_submit:
; A client that failed once stays failed (0xFD).
    LD A,(x2_poisoned)
    OR A
    JP NZ,x2_poison_result
; Length must be 1..32, and HL + length must not wrap past 0xFFFF (ending
; exactly at 0xFFFF is allowed: the sum is then 0 with carry).
    LD A,D
    OR A
    JP Z,x2_length_result
    CP 33
    JP NC,x2_length_result
    PUSH HL
    PUSH DE
    LD E,D
    LD D,0
    ADD HL,DE
    JR NC,x2_range_ready
    LD A,H
    OR L
    JP NZ,x2_range_invalid
x2_range_ready:
    POP DE
    POP HL
; Build the mailbox frame in x2_mail: all 73 bytes zero, payload length =
; frame length, payload = the frame, id = x2_mail_id.
    LD A,D
    LD (x2_frame_ptr),HL
    LD (x2_frame_len),A
    LD HL,x2_mail
    LD DE,x2_mail+1
    LD BC,72
    LD (HL),0
    LDIR
    LD A,(x2_frame_len)
    LD (x2_mail),A
    LD C,A
    LD B,0
    LD HL,(x2_frame_ptr)
    LD DE,x2_mail+9
    LDIR
    LD HL,x2_mail_id
    LD DE,x2_mail+1
    LD BC,8
    LDIR
; Wait until the X2 card is not busy, then begin an X2 frame (command 1 on
; 0x759D). Only the bytes go through the driver; begin and submit stay with
; the main Z80.
    CALL x2_owned_wait
    OR A
    JP NZ,x2_abort
    LD BC,0x759D
    LD A,1
    OUT (C),A
; SEND (root operation 11) the mail to the driver's inbox. It must be
; accepted (A = 0) with an empty reply payload.
    LD A,11
    LD HL,x2_mail
    LD DE,73
    CALL x2_rpc
    OR A
    JP NZ,x2_abort
    LD A,(x2_response+4)
    OR A
    JP NZ,x2_abort
; Poll RECEIVE (operation 12) for the driver's reply, at most 0x4000 times.
; Result 1 (busy) means no reply yet; any other refusal fails.
    LD HL,0x4000
    LD (x2_polls),HL
x2_poll_reply:
    LD A,12
    LD HL,x2_mail
    LD DE,0
    CALL x2_rpc
    CP 1
    JR NZ,x2_reply_ready
    LD HL,(x2_polls)
    DEC HL
    LD (x2_polls),HL
    LD A,H
    OR L
    JP Z,x2_abort
    JR x2_poll_reply
; The reply must be a 73-byte frame with payload length 1, our id, and
; receipt 0 (the driver staged the bytes on the data port).
x2_reply_ready:
    OR A
    JP NZ,x2_abort
    LD A,(x2_response+4)
    CP 73
    JP NZ,x2_abort
    LD A,(x2_response+40)
    CP 1
    JP NZ,x2_abort
    LD HL,x2_mail_id
    LD DE,x2_response+41
    LD B,8
x2_compare_id:
    LD A,(DE)
    CP (HL)
    JP NZ,x2_abort
    INC DE
    INC HL
    DJNZ x2_compare_id
    LD A,(x2_response+49)
    OR A
    JP NZ,x2_abort
; ACK (operation 13) the reply by its id, which frees the driver's outbox.
    LD A,13
    LD HL,x2_mail_id
    LD DE,8
    CALL x2_rpc
    OR A
    JP NZ,x2_abort
    LD A,(x2_response+4)
    OR A
    JP NZ,x2_abort
; Next mail id (64-bit increment; wrapping to zero fails).
    LD HL,x2_mail_id
    LD B,8
x2_increment_id:
    INC (HL)
    JR NZ,x2_id_ready
    INC HL
    DJNZ x2_increment_id
    JP x2_abort
; Submit the X2 frame (command 2), wait until the card is not busy, then
; require status bits 2-3 = "done, no refusal" (bit 3 set, bit 2 clear).
x2_id_ready:
    LD BC,0x759D
    LD A,2
    OUT (C),A
    CALL x2_owned_wait
    OR A
    JP NZ,x2_abort
    LD BC,0x759D
    IN A,(C)
    AND 12
    CP 8
    JP NZ,x2_abort
    XOR A
    RET
; Result exits: 0xFF bad length or range, 0xFD poisoned.
x2_length_result:
    LD A,0xFF
    RET
x2_range_invalid:
    POP DE
    POP HL
    JP x2_length_result
x2_poison_result:
    LD A,0xFD
    RET
; x2_abort: cancel the X2 frame (command 3), poison the client, return 0xFE.
x2_abort:
    LD BC,0x759D
    LD A,3
    OUT (C),A
    LD A,1
    LD (x2_poisoned),A
    LD A,0xFE
    RET
; x2_owned_wait: poll X2 status bit 0 (busy) until clear, at most 65535
; reads. Out: A = 0 when idle, 0xFE on timeout. Clobbers BC, HL, flags.
x2_owned_wait:
    LD BC,0x759D
    LD HL,0xFFFF
x2_owned_wait_loop:
    IN A,(C)
    AND 1
    JR Z,x2_owned_ready
    DEC HL
    LD A,H
    OR L
    JR NZ,x2_owned_wait_loop
    LD A,0xFE
    RET
x2_owned_ready:
    XOR A
    RET

; One root request at a time; never consume a channel already in use.
; x2_rpc: one root request to unit x2_unit.
;   In:   A = operation, HL = payload, DE = payload length.
;   Out:  A = the reply's result code (0 = accepted), with the reply header
;         at x2_response and its payload at x2_response+40; A = 0xFE when the
;         channel is in use, the reply never comes, or the reply is malformed.
;   Clobbers BC, DE, HL. The request id is fixed (one request at a time).
; First, root status bits 0-2 must be clear: no request open, no unread
; reply, no control pending.
x2_rpc:
    LD (x2_request+1),A
    LD (x2_payload_ptr),HL
    LD (x2_request+4),DE
    LD BC,0x77FD
    IN A,(C)
    AND 7
    JR NZ,x2_rpc_failed
; Address the unit, begin, write the 40-byte header and payload, submit.
    LD BC,0x7BFD
    LD A,(x2_unit)
    OUT (C),A
    LD BC,0x7AFD
    LD A,(x2_unit+1)
    OUT (C),A
    LD BC,0x79FD
    LD A,1
    OUT (C),A
    LD HL,x2_request
    LD DE,40
    CALL x2_write_bytes
    LD HL,(x2_payload_ptr)
    LD DE,(x2_request+4)
    CALL x2_write_bytes
    LD BC,0x79FD
    LD A,2
    OUT (C),A
; Wait for "reply ready" (status bit 1), at most 65535 reads.
    LD BC,0x77FD
    LD DE,0xFFFF
x2_rpc_wait:
    IN A,(C)
    AND 2
    JR NZ,x2_rpc_header
    DEC DE
    LD A,D
    OR E
    JR NZ,x2_rpc_wait
x2_rpc_failed:
    LD A,0xFE
    RET
; Read the 40-byte header. Only payloads under 74 bytes are accepted; a
; longer one is released unread and fails.
x2_rpc_header:
    LD HL,x2_response
    LD DE,40
    CALL x2_read_bytes
    LD A,(x2_response+5)
    LD D,A
    LD A,(x2_response+6)
    OR D
    LD D,A
    LD A,(x2_response+7)
    OR D
    JR NZ,x2_rpc_bad_response
    LD A,(x2_response+4)
    CP 74
    JR NC,x2_rpc_bad_response
    LD E,A
    LD D,0
    LD HL,x2_response+40
    CALL x2_read_bytes
; Release the reply (control 5), then check version 1 and byte 3 zero, and
; that the request id and all three epochs are echoed unchanged.
    LD BC,0x79FD
    LD A,5
    OUT (C),A
    LD A,(x2_response)
    CP 1
    JR NZ,x2_rpc_failed
    LD A,(x2_response+3)
    OR A
    JR NZ,x2_rpc_failed
    LD HL,x2_request+8
    LD DE,x2_response+8
    LD B,32
x2_rpc_compare:
    LD A,(DE)
    CP (HL)
    JR NZ,x2_rpc_failed
    INC DE
    INC HL
    DJNZ x2_rpc_compare
    LD A,(x2_response+1)
    RET
x2_rpc_bad_response:
    LD BC,0x79FD
    LD A,5
    OUT (C),A
    JR x2_rpc_failed
; x2_write_bytes / x2_read_bytes: DE bytes from HL to the root data port
; 0x78FD, or from it to HL (none if DE = 0). Clobbers A, BC, DE, HL.
x2_write_bytes:
    LD A,D
    OR E
    RET Z
    LD BC,0x78FD
x2_write_loop:
    LD A,(HL)
    OUT (C),A
    INC HL
    DEC DE
    LD A,D
    OR E
    JR NZ,x2_write_loop
    RET
x2_read_bytes:
    LD A,D
    OR E
    RET Z
    LD BC,0x78FD
x2_read_loop:
    IN A,(C)
    LD (HL),A
    INC HL
    DEC DE
    LD A,D
    OR E
    JR NZ,x2_read_loop
    RET

; Client state. x2_unit: the driver unit (18 in the tests). x2_request: the
; 40-byte request header template (version 1; operation and length set per
; call; fixed id 321; the epochs the unit is expected to have). x2_mail_id:
; the id of the next mailbox frame. x2_poisoned: set after a failure.
; x2_mail: the outgoing frame. x2_response: reply header + 73-byte payload.
x2_unit: DW 18
x2_request:
    DB 1,0,0,0,0,0,0,0
    DB 65,1,0,0,0,0,0,0 ; root correlation 321, no overlapping request
    DB 1,0,0,0,0,0,0,0 ; resource epoch
    DB 2,0,0,0,0,0,0,0 ; execution epoch
    DB 1,0,0,0,0,0,0,0 ; grant map epoch
x2_mail_id: DB 1,0,0,0,0,0,0,0
x2_poisoned: DB 0
x2_frame_len: DB 0
x2_frame_ptr: DW 0
x2_payload_ptr: DW 0
x2_polls: DW 0
x2_mail: DS 73,0
x2_response: DS 113,0
