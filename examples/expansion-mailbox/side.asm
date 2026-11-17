; ===========================================================================
; side.asm -- EXPMAIL side-Z80 job: answer each mailbox challenge
;
; What it is:  the job that main.asm loads into compute unit 1, one of the
;              side Z80s of the X5 expansion. It takes a frame from its
;              mailbox, checks that it is the next round's challenge, replies
;              with (round, challenge XOR 0xFF) and sleeps with WAIT until the
;              next frame arrives. Anything unexpected stops it with HALT, so
;              the main program sees a failure instead of a wrong answer.
; Builds:      side.bin, by tools/ExpansionMailboxBuilder
;              (ExpansionMailboxAssets.cs); main.asm embeds side.bin with
;              INCBIN and loads it into the unit at address 0.
; Runs on:     a compute unit (side Z80) of the X5 expansion, in its own
;              64 KB address space; loads at ORG 0 and is entered once at 0000
;              by the root START request.
; Interface:   the unit's local mailbox ports: 0x74FD (control / status) and
;              0x75FD (frame data). Stack at 0xBFFF. Owns counter, rx and tx.
; Provenance:  house code (hc2000-emu); a source of the public example in
;              examples/expansion-mailbox/. The installer ships the tape built
;              from it (EXPMAIL.tap), not this file.
; ===========================================================================
;
; The local mailbox, as seen from a side Z80:
;   Frame (73 bytes, the same in both directions):
;     +0       payload length, 0..64
;     +1..+8   request id, 64-bit little-endian, never zero
;     +9..+72  payload; every byte past the payload length must be zero
;   Write 0x74FD (control): 1 = begin an outgoing frame, 2 = commit it to the
;     outbox, 3 = abort it, 4 = rewind the inbox read position to byte 0,
;     5 = release the inbox frame, 6 = WAIT (sleep until a frame arrives),
;     7 = IDLE (service idle).
;   Read 0x74FD (status): bit 0 = a frame is in the inbox, bit 1 = the outbox
;     is free, bit 2 = an outgoing frame is open, bits 3-5 = last local error,
;     bit 6 = waiting, bit 7 = service idle.
;   0x75FD: read = next inbox byte (0xFF past the end), write = next byte of
;     the open outgoing frame.
;
; In-house byte-mailbox job. Entered once at 0000; WAIT resumes after OUT.
ORG 0
; Entered once, by START: own stack, round counter = 0.
    LD SP,0xBFFF
    XOR A
    LD (counter),A
; receive: the main program sends a frame before START (round 1) or wakes
; WAIT with one (later rounds), so the inbox must hold a frame here.
; IN A,(C) with BC = 0x74FD puts the full 16-bit port number on the bus.
receive:
    LD BC,0x74FD
    IN A,(C)
    AND 1
    JP Z,bad
; Rewind the inbox to byte 0 and copy all 73 bytes of the frame into rx.
    LD A,4
    OUT (C),A
    LD HL,rx
    LD DE,73
    LD BC,0x75FD
read_frame:
    IN A,(C)
    LD (HL),A
    INC HL
    DEC DE
    LD A,D
    OR E
    JR NZ,read_frame
; Check the frame: payload length 1, request id = this round (counter + 1)
; with the other 7 id bytes zero.
    LD A,(rx)
    CP 1
    JP NZ,bad
    LD A,(counter)
    INC A
    LD D,A
    LD A,(rx+1)
    CP D
    JP NZ,bad
    LD HL,rx+2
    LD E,7
    CALL zeros
; D = round * 17 (round * 16 + round): the expected challenge, 0x11 in
; round 1 and 0x22 in round 2. It must be the payload byte, and the 63 bytes
; after it must be zero.
    LD A,D
    ADD A,A
    ADD A,A
    ADD A,A
    ADD A,A
    ADD A,D
    LD D,A
    LD A,(rx+9)
    CP D
    JP NZ,bad
    LD HL,rx+10
    LD E,63
    CALL zeros
; The request is valid: release the inbox (command 5).
    LD BC,0x74FD
    LD A,5
    OUT (C),A
; Build the reply in tx: length 2 (preset), request id = round, payload
; byte 0 = round, payload byte 1 = challenge XOR 0xFF.
    LD A,(counter)
    INC A
    LD (counter),A
    LD (tx+1),A
    LD (tx+9),A
    LD A,D
; reply_mask labels the XOR; the example's tests patch its operand to make
; the job send a well-formed but wrong reply.
reply_mask:
    XOR 255
    LD (tx+10),A
; Begin an outgoing frame (command 1), write the 73 bytes of tx to 0x75FD.
    LD A,1
    OUT (C),A
    LD HL,tx
    LD DE,73
    LD BC,0x75FD
write_frame:
    LD A,(HL)
    OUT (C),A
    INC HL
    DEC DE
    LD A,D
    OR E
    JR NZ,write_frame
; Commit the frame to the outbox (command 2), then WAIT (command 6): the
; unit sleeps after this OUT and resumes at the next instruction when the
; main program sends the next frame.
    LD BC,0x74FD
    LD A,2
    OUT (C),A
    LD A,6
    OUT (C),A
    JP receive
; zeros: check that E bytes from HL are zero; on any nonzero byte, stop.
; Clobbers A, E, HL.
zeros:
    LD A,(HL)
    OR A
    JP NZ,bad
    INC HL
    DEC E
    JR NZ,zeros
    RET
; bad: the frame was not what this job expects. Stop for good; the main
; program reports a failure.
bad:
    HALT
    JP bad
; counter: rounds answered so far. rx: the received frame. tx: the reply
; frame, payload length 2, the rest filled in per round.
counter: DB 0
rx: DS 73,0
tx: DB 2,0,0,0,0,0,0,0,0,0,0
    DS 62,0
; The whole job must fit in the 4 KB that main.asm loads.
ASSERT $ <= 4096
