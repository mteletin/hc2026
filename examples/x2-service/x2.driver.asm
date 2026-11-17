; ===========================================================================
; x2.driver.asm -- driver-Z80 job: write main's X2 frame to the owned port
;
; What it is:  the job for a driver Z80 of the full X5 that has been given
;              the X2 card's data port, which then only it may write. The main
;              Z80 begins an X2 frame on the command port, mails the frame
;              bytes to this job, and waits for its one-byte receipt; the job
;              writes the bytes to the X2 data port and replies with the port's
;              result code. The main Z80 then submits the frame
;              (x2.owned.asm is the main-side half).
; Builds:      assembled by tests/Hc2000.Machine.Tests/ExpansionX5SnapshotTests.cs
;              and loaded into driver unit 18; not part of X2VGA.tap.
; Runs on:     a driver Z80 of the full X5 (units 17..24), after the root has
;              mapped its memory and handed it the X2 device; loads at ORG 0
;              and is entered by START.
; Interface:   the unit's local mailbox ports 0x74FD (control / status) and
;              0x75FD (frame data), as described in side.asm of the
;              expansion-mailbox example; the X2 data port 0x769D.
;              Request frame: payload = the X2 frame bytes (1..32).
;              Reply frame: payload length 1, the request's id, payload byte
;              = receipt: the X2 data port's result (0 = bytes staged), or 2
;              when the request length is not 1..32.
; Provenance:  house code (hc2000-emu); a source of the public example in
;              examples/x2-service/, used by its tests (not itself copied
;              into the installer).
; ===========================================================================
;
; X5 driver job. Main begins the X2 frame, sends 1..32 bytes in a mailbox
; payload, waits for this job's one-byte receipt, then submits the frame.
; The owning driver alone writes the exclusive X2 data port. This image needs
; private executable RAM and stack; it does not reserve a CP/M console hook.
ORG 0
; Entered once, by START: own stack.
    LD SP,0xBFFF
; receive: no frame in the inbox (status bit 0 clear) means nothing to do:
; WAIT (command 6) until one arrives, then look again.
receive:
    LD BC,0x74FD
    IN A,(C)
    AND 1
    JR NZ,read_request
    LD A,6
    OUT (C),A
    JR receive
; Rewind the inbox (command 4) and copy the 73-byte frame into rx.
read_request:
    LD A,4
    OUT (C),A
    LD HL,rx
    LD D,73
    LD BC,0x75FD
read_byte:
    IN A,(C)
    LD (HL),A
    INC HL
    DEC D
    JR NZ,read_byte
; The payload length is the X2 frame length; it must be 1..32.
    LD A,(rx)
    OR A
    JR Z,invalid
    CP 33
    JR NC,invalid
; Write the frame bytes to the X2 data port, then read the port back: its
; result code (0 = accepted) becomes the receipt.
    LD D,A
    LD HL,rx+9
    LD BC,0x769D
write_byte:
    LD A,(HL)
    OUT (C),A
    INC HL
    DEC D
    JR NZ,write_byte
    IN A,(C)
    JR reply
; Bad length: receipt 2 (the X2 "invalid" code); nothing is written.
invalid:
    LD A,2
; reply: receipt into the payload, the request's 8-byte id into the reply,
; then release the inbox (command 5).
reply:
    LD (tx+9),A
    LD HL,rx+1
    LD DE,tx+1
    LD BC,8
    LDIR
    LD BC,0x74FD
    LD A,5
    OUT (C),A
; Begin an outgoing frame (command 1) and write the 73 bytes of tx.
    LD A,1
    OUT (C),A
    LD HL,tx
    LD D,73
    LD BC,0x75FD
write_reply:
    LD A,(HL)
    OUT (C),A
    INC HL
    DEC D
    JR NZ,write_reply
; Commit it to the outbox (command 2), WAIT (command 6), and go back to
; receive when the next frame wakes the unit.
    LD BC,0x74FD
    LD A,2
    OUT (C),A
    LD A,6
    OUT (C),A
    JP receive
; rx: the received frame. tx: the reply frame, payload length 1.
rx: DS 73,0
tx: DB 1
    DS 72,0
