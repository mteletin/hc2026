; ===========================================================================
; sentinel.asm -- X3-sentinel.mod: assemble the sentinel module as a file
;
; What it is:  assembling this file on its own produces the module file
;              X3-sentinel.mod, byte for byte the module that player.asm
;              embeds. It contains no code.
; Builds:      X3-sentinel.mod (2364 bytes), by tools/ExpansionMailboxBuilder
;              (X3ServiceAssets.cs); includes sentinel.data.asm.
; Runs on:     nothing; it is data. ORG 0, so offsets equal file offsets.
; Interface:   none.
; Provenance:  house code (hc2000-emu); a public example source, shipped with
;              the installer under examples/x3-service/.
; ===========================================================================
;
; Standalone assembly produces the distributable .mod fixture byte-for-byte.
ORG 0
INCLUDE "sentinel.data.asm"
