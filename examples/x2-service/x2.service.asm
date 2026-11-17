; ===========================================================================
; x2.service.asm -- X2 VGA command service: submit one frame, wait for it
;
; What it is:  the main-owned X2 command service. x2_submit sends one complete
;              X2 frame (an 8-byte header plus payload, 1..32 bytes in all) to
;              the X2 command VGA card through its command and data ports,
;              then waits, with bounded polling, until the card has finished
;              the command. For use when the main Z80 owns the card (no
;              driver Z80 holds its data port).
; Builds:      included by main.asm (X2VGA.tap, built by
;              tools/ExpansionMailboxBuilder, X2ServiceAssets.cs) and by
;              exercise.asm (tests); you can include it in your own program.
; Runs on:     the main Z80 of an extended HC2000 with the X2 command VGA
;              card; no ORG of its own, it assembles where it is included.
; Interface:   x2_submit, see below. X2 ports:
;                0x759D write: 1 = begin a frame, 2 = submit it, 3 = cancel.
;                0x759D read (status): bit 0 = busy (a command, or a Present
;                  waiting for the next video boundary), bit 1 = a frame is
;                  being collected, bit 2 = the last result was a refusal,
;                  bit 3 = the last command is done, bit 4 = always 1.
;                0x769D write: next frame byte (at most 32).
;                0x769D read: last result: 0 accepted, 1 busy, 2 invalid,
;                  3 time limit, 4 unsupported.
;              Frame: version 1, opcode, payload length, reserved 0, 32-bit
;              little-endian tag, payload. Opcodes: 1 Clear, 2 FillRectangle,
;              3 CopyRectangle, 4 SetPalette, 5 Present, 6 DrawGlyph,
;              7 ScrollUp (payloads in instructions.html).
; Provenance:  house code (hc2000-emu); a public example source, shipped with
;              the installer under examples/x2-service/.
; ===========================================================================
;
; Main-origin X2 frame submission. HL points to 1..32 bytes; D is the length.
; Returns A=0 only after device completion, A=1..4 for admission refusal,
; A=0xFE on a bounded wait timeout or non-Done terminal, A=0xFF for bad length.
; Clobbers AF, BC, DE and HL. The caller owns the stack, frame and code placement.
x2_submit:
; Length must be 1..32 (the card's frame limit).
    LD A,D
    OR A
    JR Z,x2_bad_length
    CP 33
    JR NC,x2_bad_length
; Do not begin while the card is still busy with an earlier command.
    PUSH HL
    PUSH DE
    CALL x2_wait_idle
    POP DE
    POP HL
    OR A
    RET NZ
; Begin a frame (command 1) and write its D bytes to the data port.
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
; Submit (command 2). The data port then reads the admission result:
; nonzero (1..4) means the card refused the frame; return that code.
    LD BC,0x759D
    LD A,2
    OUT (C),A
    LD BC,0x769D
    IN A,(C)
    OR A
    RET NZ
; Admitted: wait until the command has run (a Present completes only at the
; next video boundary), then require status bit 3 (done). A command that
; ended any other way (for example cancelled) returns 0xFE.
    CALL x2_wait_idle
    OR A
    RET NZ
    LD BC,0x759D
    IN A,(C)
    AND 8
    JR Z,x2_timeout
    XOR A
    RET
; x2_bad_length: D was 0 or more than 32.
x2_bad_length:
    LD A,0xFF
    RET
; x2_wait_idle: poll status bit 0 until the card is not busy, at most 65535
; reads. Out: A = 0 when idle, 0xFE on timeout. Clobbers BC, HL, flags.
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
x2_timeout:
    LD A,0xFE
    RET
x2_ready:
    XOR A
    RET
