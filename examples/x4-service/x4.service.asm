; ===========================================================================
; x4.service.asm -- X4 math command service: one request, one reply
;
; What it is:  x4_submit sends one complete X4 request frame (8..32 bytes)
;              through the card's command and data ports and copies the
;              64-byte reply into the caller's buffer. START only admits an
;              operation: poll QUERY with the same tag until the state is
;              final, read the result, then ACK the tag.
; Builds:      included by main.asm (X4MATH.tap, built by
;              tools/ExpansionMailboxBuilder, X4ServiceAssets.cs) and by
;              exercise.asm (tests).
; Runs on:     the main Z80 of an extended HC2000 with the X4 math
;              accelerator; no ORG of its own, it assembles where it is
;              included.
; Interface:   x4_submit, see below. X4 ports:
;                0x7A9D write: 1 = begin a request (this CPU becomes the
;                  port's user), 2 = submit it, 3 = end the transaction
;                  (drop the reply, free the port), 4 = rewind the reply.
;                0x7A9D read (status): bits 4-7 = 1 when the card answers
;                  this CPU (0xFF when it is absent or in use by another),
;                  bit 0 = an operation is running, bit 1 = a request is
;                  being collected, bit 2 = an error was latched, bit 3 =
;                  reply bytes are waiting to be read.
;                0x7B9D: write = next request byte; read = next reply byte
;                  (0xFF past the end).
;              Request: 01, opcode, payload length, 00, 32-bit tag, payload.
;              Opcodes: 0 INFO, 1 START (24-byte payload), 2 QUERY, 3 CANCEL,
;              4 ACK. Reply: 8-byte header (result at offset 2), then 56
;              payload bytes when accepted (state at payload offset 0, result
;              at 40..55). Admission results: 0 OK, 1 busy, 2 invalid,
;              3 unsupported, 4 no target, 5 too late, 6 time limit,
;              7 resource limit. Details in instructions.html.
; Provenance:  house code (hc2000-emu); a public example source, shipped with
;              the installer under examples/x4-service/.
; ===========================================================================
;
; HL=request, A=length (8..32), DE=64-byte reply buffer. Returns admission
; result in A, FE for a bad frame or incomplete reply, FF for missing card.
; The buffer receives 64 bytes; an 8-byte refusal is followed by FF bytes.
; The caller owns code, stack, request, reply, tag and completion polling.
; This routine does not wait for the scalar engine to finish. AF, BC, DE, HL
; are clobbered; IX and IY are preserved.
x4_submit:
; Length must be 8..32: at least the 8-byte header (INFO, QUERY, CANCEL and
; ACK have no payload), at most the card's frame limit.
    CP 8
    JR C,x4_bad_frame
    CP 33
    JR NC,x4_bad_frame
; Keep the reply pointer on the stack; D counts the request bytes.
    PUSH DE
    LD D,A
; Presence: the upper status nibble reads 1 only when the card is there and
; open to this CPU.
    LD BC,0x7A9D
    IN A,(C)
    AND 0xF0
    CP 0x10
    JR NZ,x4_missing
; Begin a request (command 1) and write the request bytes to the data port.
    LD A,1
    OUT (C),A
    LD BC,0x7B9D
x4_send_byte:
    LD A,(HL)
    OUT (C),A
    INC HL
    DEC D
    JR NZ,x4_send_byte
; Submit (command 2). The reply is produced at once, so status bit 3 must
; be set now; otherwise end the transaction and return 0xFE.
    LD BC,0x7A9D
    LD A,2
    OUT (C),A
    IN A,(C)
    AND 8
    JR Z,x4_incomplete
; Copy 64 reply bytes to the caller's buffer (a refusal is 8 bytes long;
; the rest reads as 0xFF), end the transaction (command 3), and return the
; admission result, offset 2 of the reply.
    POP HL
    PUSH HL
    LD D,64
    LD BC,0x7B9D
x4_receive_byte:
    IN A,(C)
    LD (HL),A
    INC HL
    DEC D
    JR NZ,x4_receive_byte
    LD BC,0x7A9D
    LD A,3
    OUT (C),A
    POP HL
    INC HL
    INC HL
    LD A,(HL)
    RET
; Exits: 0xFE for a bad length or no reply, 0xFF when the card is missing.
x4_incomplete:
    LD A,3
    OUT (C),A
    POP DE
x4_bad_frame:
    LD A,0xFE
    RET
x4_missing:
    POP DE
    LD A,0xFF
    RET
