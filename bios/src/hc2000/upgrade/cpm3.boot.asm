; Independent nonbanked resident BOOT block. Load at 7800h separately from
; the D300h BIOS; the CCP must not overwrite 7000h-7fffh. Production image
; builder integration and relocation of FE62h SCB references are still TODO.
; Cold device and drive initialization is not implemented here: unlike DRI's
; BIOSKRNL, this standalone block has no linked ?cinit/?init or XDPH init hooks.
; The caller must initialize the platform (including the console ROM) first.
ORG 0x7800
    JP Boot
    JP Boot

; FE62 is the CP/M 3 symbolic @MXTPA placeholder. The CP/M relocator must
; translate this operand (to D0FE for the expected system), not this source.
MXTPA EQU 0xFE62
Boot:
    LD SP,StackTop
    LD A,0xC3
    LD (0),A
    LD (5),A
    LD HL,0xD303
    LD (1),HL
    LD HL,(MXTPA)
    LD (6),HL
    ; Reset the complete FCB on every boot, including BDOS-updated fields.
    LD HL,FcbTemplate
    LD DE,Fcb
    LD BC,36
    LDIR
    LD DE,Fcb
    LD C,15
    CALL 5
    INC A
    JP Z,Failure
    LD DE,1
    LD C,44
    CALL 5
    LD HL,0x0100
    LD (NextDma),HL
ReadLoop:
    LD DE,(NextDma)
    LD C,26
    CALL 5
    LD DE,Fcb
    LD C,20
    CALL 5
    OR A
    JR Z,ReadOk
    CP 1                     ; sequential EOF, not a disk error
    JP NZ,Failure
    LD HL,(NextDma)
    LD A,H
    CP 1
    JR NZ,NotEmpty
    LD A,L
    OR A                     ; empty CCP is not executable
    JP Z,Failure
NotEmpty:
    LD DE,0x0080
    LD C,26
    CALL 5
    JP 0x0100
ReadOk:
    LD HL,(NextDma)
    LD DE,128
    ADD HL,DE
    LD (NextDma),HL
    LD A,H
    CP 0x70                  ; never allow CCP to overwrite resident area
    JR C,ReadLoop
Failure:
    ; No usable CCP. The machine must not HALT: after a short delay the
    ; boot retries, so the system stays alive and boots as soon as the
    ; drive has a usable CCP. The JP 0x0100 above is the only path into the
    ; CCP, and it runs only after a full, clean load.
    LD B,0x80
FailDelay:
    DJNZ FailDelay
    JP Boot
NextDma: DW 0
; CP/M FCB drive codes are 1-based: 3 selects the system drive C:.
FcbTemplate: DB 3,"CCP     COM"
    DEFS 24,0
Fcb: DEFS 36,0
    DEFS 64,0
StackTop:
