; ===========================================================================
; x3.service.asm -- X3 sound command service: one request, one reply
;
; What it is:  x3_submit sends one complete X3 request frame (12..32 bytes)
;              through the card's command and data ports and copies the
;              48-byte reply into the caller's buffer. The card answers at
;              once: the result says whether the command was admitted, not
;              that a sound has finished playing.
; Builds:      included by main.asm (X3SOUND.tap) and player.asm (X3MOD.tap),
;              built by tools/ExpansionMailboxBuilder (X3ServiceAssets.cs),
;              and by exercise.asm (tests).
; Runs on:     the main Z80 (or an owning driver Z80) of an extended HC2000
;              with the X3 sound card; no ORG of its own, it assembles where
;              it is included.
; Interface:   x3_submit, see below. X3 ports:
;                0x789D write: 1 = begin a request (this CPU becomes the
;                  port's user), 2 = submit it, 3 = end the transaction
;                  (drop the reply, free the port), 4 = rewind the reply.
;                0x789D read (status): bits 4-7 = 1 when the card answers
;                  this CPU (0xFF when it is absent or in use by another),
;                  bit 0 = a channel is playing, bit 1 = a request is being
;                  collected, bit 2 = the last result was a refusal,
;                  bit 3 = a reply is ready.
;                0x799D: write = next request byte (at most 32);
;                  read = next reply byte (0xFF past the end).
;              Request: 01, opcode, payload length, 00, 32-bit tag, payload.
;              Opcodes: 1 START, 2 STOP, 3 SET, 4 QUERY (payloads in
;              instructions.html). Reply: 01, opcode, result, payload length,
;              tag, then a 40-byte channel snapshot when accepted. Results:
;              0 accepted, 1 busy, 2 invalid, 3 unsupported, 4 source denied,
;              5 resource limit, 6 invalid state, 7 time limit.
; Provenance:  house code (hc2000-emu); a public example source, shipped with
;              the installer under examples/x3-service/.
; ===========================================================================
;
; X3 bounded main/owning-driver service. HL=request, A=request length (12..32),
; DE=48-byte reply buffer. Returns A=the X3 admission result (0..7), 0xFE for
; invalid length or an incomplete reply, or 0xFF when no accessible X3 card is
; present. The reply buffer always receives 48 bytes; a refusal has an 8-byte
; reply followed by unspecified bytes. The caller owns code, stack, request,
; reply and sample placement. This service does not wait for playback to end.
; Clobbers AF, BC, DE and HL; IX and IY are preserved.
x3_submit:
; Length must be 12..32: the 8-byte header plus the smallest payload (4)
; up to the card's frame limit.
    CP 12
    JR C,x3_bad_frame
    CP 33
    JR NC,x3_bad_frame
; Keep the reply pointer on the stack; D counts the request bytes.
    PUSH DE
    LD D,A
; Presence: the upper status nibble reads 1 only when the card is there and
; open to this CPU.
    LD BC,0x789D
    IN A,(C)
    AND 0xF0
    CP 0x10
    JR NZ,x3_missing
; Begin a request (command 1) and write the request bytes to the data port.
    LD A,1
    OUT (C),A
    LD BC,0x799D
x3_send_byte:
    LD A,(HL)
    OUT (C),A
    INC HL
    DEC D
    JR NZ,x3_send_byte
; Submit (command 2). The reply is produced at once, so status bit 3 must
; be set now; otherwise end the transaction and return 0xFE.
    LD BC,0x789D
    LD A,2
    OUT (C),A
    IN A,(C)
    AND 8
    JR Z,x3_incomplete
; Copy 48 reply bytes to the caller's buffer (a refusal is 8 bytes long;
; the rest reads as 0xFF), end the transaction (command 3), and return the
; result byte, offset 2 of the reply.
    POP HL
    PUSH HL
    LD D,48
    LD BC,0x799D
x3_receive_byte:
    IN A,(C)
    LD (HL),A
    INC HL
    DEC D
    JR NZ,x3_receive_byte
    LD BC,0x789D
    LD A,3
    OUT (C),A
    POP HL
    INC HL
    INC HL
    LD A,(HL)
    RET
; Exits: 0xFE for a bad length or no reply, 0xFF when the card is missing.
x3_incomplete:
    LD A,3
    OUT (C),A
    POP DE
x3_bad_frame:
    LD A,0xFE
    RET
x3_missing:
    POP DE
    LD A,0xFF
    RET
