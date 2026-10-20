; Physical origin: 0000. Unknown bytes intentionally remain DB.
phase 2000-3FFF at E000
phase 001E-007C at 8000
entry 0000 8000 E000
jumptable 2000-2017
; Intentional executable overlap at logical EBE9-EBEB, kept as DB.
data 2BE9-2BEB bytes 3
entry EBEC
port 85 FDC_MSR
port 87 FDC_DATA
port 07 DRIVE_LATCH
port C5 VIDEO_BANK
port C7 RAM_BANK
port 7E ROM_CONFIG
port FE ULA_PORT
port F7 SERIAL_DATA
port EF SERIAL_HANDSHAKE
equ DB0F ColumnRow
equ DB0D ConsoleState
equ DF1C SavedSp
