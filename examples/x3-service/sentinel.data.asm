; ===========================================================================
; sentinel.data.asm -- the X3 sentinel module: a tiny original M.K. MOD
;
; What it is:  a complete four-channel ProTracker "M.K." module written for
;              this example: one order, one 64-row pattern whose first row
;              starts instruments 1-4 at period 428, and four 64-byte square
;              wave samples. No third-party song or sample is included.
; Builds:      included by player.asm (X3MOD.tap) and by sentinel.asm
;              (X3-sentinel.mod), both by tools/ExpansionMailboxBuilder
;              (X3ServiceAssets.cs).
; Runs on:     nothing; it is data, placed where it is included.
; Interface:   labels mod_file (start), mod_pattern (pattern 0), mod_samples
;              (sample data) and mod_end; 2364 bytes in all.
; Provenance:  house code (hc2000-emu); a public example source, shipped with
;              the installer under examples/x3-service/.
; ===========================================================================
;
; Original synthetic four-channel M.K. fixture authored for this example.
; One order, one 64-row pattern, four 64-byte signed samples; no external input.
mod_file:
; Title: 20 bytes, zero padded.
    DB "X3 sentinel"
    DS 9,0
    ; Name[22], length in big-endian words, finetune, volume,
    ; loop start and loop length in big-endian words.
; Instrument 1: length 32 words (64 bytes), finetune 0, volume 64, loop
; start 0, loop length 32 words (the whole sample loops).
    DS 22,0
    DB 0,32,0,64,0,0,0,32
; Instrument 2: as 1, volume 48.
    DS 22,0
    DB 0,32,0,48,0,0,0,32
; Instrument 3: as 1, volume 0.
    DS 22,0
    DB 0,32,0,0,0,0,0,32
; Instrument 4: volume 32, loop length 1 word: ProTracker's "no loop".
    DS 22,0
    DB 0,32,0,32,0,0,0,1
; Instruments 5-31 unused (27 x 30 bytes). Song length 1, restart byte 127,
; then the 128-entry order table (all pattern 0) and the "M.K." signature.
    DS 810,0
    DB 1,127
    DS 128,0
    DB "M.K."
mod_pattern:
    ; Row 0: instruments 1..4 at period 428, no effects.
;   Each note: 0x01,0xAC = period 0x1AC (428); 0xN0 = instrument N, no effect.
    DB 1,0xAC,0x10,0,1,0xAC,0x20,0,1,0xAC,0x30,0,1,0xAC,0x40,0
; Rows 1-63: empty.
    DS 1008,0
; Four samples of 64 signed bytes: half a period high, half low, at four
; levels (+64/-64, +32/-32, +96/-96, +16/-16).
mod_samples:
    DS 32,64
    DS 32,192
    DS 32,32
    DS 32,224
    DS 32,96
    DS 32,160
    DS 32,16
    DS 32,240
mod_end:
; Fixed layout checks: header 1084 bytes, one 1024-byte pattern, 2364 in all.
    ASSERT mod_pattern-mod_file == 1084
    ASSERT mod_samples-mod_pattern == 1024
    ASSERT mod_end-mod_file == 2364
