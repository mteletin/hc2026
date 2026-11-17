; ===========================================================================
; main.asm -- X4MATH: callable entry for the X4 math command service
;
; What it is:  the X4 math example program: a fixed entry at 0x8000 that
;              jumps to x4_submit, which sends one X4 command (INFO, START an
;              operation, QUERY, CANCEL or ACK) and copies the card's reply
;              into your buffer. Load the tape, then call 0x8000 from your own
;              code.
; Builds:      X4MATH.tap (CODE block "X4MATH" at 32768), by
;              tools/ExpansionMailboxBuilder (X4ServiceAssets.cs); includes
;              x4.service.asm.
; Runs on:     extended HC2000, full X5 with the X4 math accelerator enabled
;              and open to the caller; loads at ORG 0x8000.
; Interface:   0x8000: HL = request, A = request length 8..32, DE = 64-byte
;              reply buffer; returns A (see x4.service.asm). Ports 0x7A9D and
;              0x7B9D.
; Provenance:  house code (hc2000-emu); a public example source, shipped with
;              the installer under examples/x4-service/.
; ===========================================================================
;
; Caller-owned resident X4 command entry at 8000.
ORG 0x8000
; A fixed entry address, whatever the service code behind it looks like.
    JP x4_submit
INCLUDE "x4.service.asm"
