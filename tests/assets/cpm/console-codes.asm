; Step 5 guest .COM: send every documented CP/M console control/escape sequence
; through the CP/M ROM BIOS CONOUT jump at E009, rather than BDOS (which expands
; TAB before it reaches the HC console driver) or an emulator/screen injection.
        ORG 0x100
start:
        LD HL, sequence
        LD B, sequence_end-sequence
next:
        LD C, (HL)
        INC HL
        PUSH HL
        PUSH BC
        CALL 0xE009
        POP BC
        POP HL
        DJNZ next
        RET

sequence:
        DB 0x18
        DB "HC2000 CONSOLE CODE TEST"
        DB 0x0D, 0x0A
        DB 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D
        DB 0x18, 0x1A, 0x1B, 0x1E
        DB 0x1B, 0x13
        DB 0x1B, 0x1A
        DB 0x1B, 'A', 0x22, 0x24
        DB 0x1B, 'B'
        DB 0x1B, 'Y'
        DB 0x1B, 'I', 0x03
        DB 0x1B, 'P', 0x05
        DB 0x1B, 'Q', 0x06
        DB 0x1B, 'R', 0x17
        DB 0x1B, 'I', 0x0B
        DB 0x1B, 'P', 0x0D
        DB 0x1B, 'Q', 0x0E
        DB 0x1B, 'R', 0x3F
        DB 0x1B, 'j', 0x1B, 'k'
        DB 0x1B, 'p', 0x1B, 'q'
        DB 0x1B, 'r', 0x1B, 's'
        DB 0x1B, 't', 0x1B, 'u'
        DB 0x1B, 'Z'      ; unknown ESC drops ESC and prints Z
        DB 0xC1, 0x7F     ; high-bit mask ('A') and solid block
        DB 0x00, 0x01     ; undefined controls are ignored
sequence_end:
