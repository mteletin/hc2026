; Tape v2: a bound, in-range CP/M bank aliases 8000-BFFF.
; Code, state and private stack must therefore live below that window.
TapeOriginalBdos EQU 0xC506
TapeStackTop EQU 0x7F00
TapeDirectory EQU 0x7100
TapeBounce EQU 0x7180
TapeAlv EQU 0x7200
TapeCsv EQU 0x7300
TapeDescriptor EQU 0x7400
TapeFirstPage EQU TapeDescriptor+2
TapeBlocks EQU TapeDescriptor+4
TapeLetter EQU TapeDescriptor+6
TapeProtected EQU TapeDescriptor+7
