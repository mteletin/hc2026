; ===========================================================================
; main.asm -- X3SOUND: callable entry for the X3 sound command service
;
; What it is:  the X3 sound example program: a fixed entry at 0x8000 that
;              jumps to x3_submit, which sends one X3 command (START, STOP,
;              SET or QUERY a channel) and copies the card's reply into your
;              buffer. Load the tape, then call 0x8000 from your own code.
; Builds:      X3SOUND.tap (CODE block "X3SOUND" at 32768), by
;              tools/ExpansionMailboxBuilder (X3ServiceAssets.cs); includes
;              x3.service.asm.
; Runs on:     extended HC2000, full X5 with the X3 sound card owned by the
;              caller (--memory paged --m-view --side-z80 16 --driver-z80 0
;              --x3-sound); loads at ORG 0x8000.
; Interface:   0x8000: HL = request, A = request length 12..32, DE = 48-byte
;              reply buffer; returns A (see x3.service.asm). Ports 0x789D and
;              0x799D.
; Provenance:  house code (hc2000-emu); a public example source, shipped with
;              the installer under examples/x3-service/.
; ===========================================================================
;
; A caller-owned resident command entry at 8000. The caller provides frames,
; reply storage, stack and accessible physical sample pages.
ORG 0x8000
; A fixed entry address, whatever the service code behind it looks like.
    JP x3_submit
INCLUDE "x3.service.asm"
