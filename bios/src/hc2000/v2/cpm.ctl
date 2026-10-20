; Physical origin E000. cpm.rom has a leading 00 and omits the final 55.
; Preserve the received dump exactly; do not silently repair it.
; See KB hc2000 doc source-cpm-rom sections 0-3.
; Give the non-executable leading pad a distinct display address to avoid
; a duplicate logical E000; physical output still begins at E000.
phase E000-E000 at DFFF
data E000-E000 bytes 1
phase E001-F80E at E000
phase F80F-F86D at 8000
phase F86E-FFFF at F86D
entry E000 F800 8000 EE58 E197 E1B1
jumptable E001-E018
label E000 RomBiosTable
label E021 BiosInit
label E089 ConsoleStatus
label E095 ConsoleInput
label E107 ConsoleOutput
label E913 SerialOutput
label E95C SerialInput
label EB0A DiskIo
label ECB2 Beep
label F800 BootCopy
label 8000 BootRam
label EE58 InterruptHandler
label E197 ConsoleDefault
label E1B1 ConsoleEscape
; Indirect console handlers/state callbacks from analysis section 3.
entry E1EC E1F5 E231 E21E E246 E24F E227 E252 E284 E27A
entry E28B E290 E2AE E2CC E307 E32C E332 E335 E342 E366 E36D
entry E37F E386 E39B E3A2 E3B3 E3BA E3F0 E3E9 E3C6 E3CB E3DB
label E284 EscapeDispatch
label E28B SetCursorRow
label E290 SetCursorRowArgument
label E2AE SetCursorColumnArgument
label E2CC ClearLine
label E307 ClearWindow
label E366 SetInk
label E37F SetPaper
label E39B SetBorder
label E3C6 CursorOff
label E3CA CursorOn
label E3D9 ClickOff
label E3DA ClickOn
label E402 GlyphWriter
label E9F8 FdcSendByte
label EA06 FdcCommand
label EA93 FdcReadData
label EACE FdcWriteData
label EADE FdcTerminalCount
label EC04 DiskLogin
comment E000 "8-entry ROM API; RAM-resident CP/M BIOS is disk-loaded, not present in ROM"
comment F800 "Copy 005F bytes into RAM at 8000 then transfer control"
comment EB0A "BC points to the IOPB; polled Intel 8272 disk driver"
comment E402 "64-column 4x8 glyph writer"
data E072-E089 bytes 8
words E14F-E164
words E176-E197
font E5AC-E8AB 4x8
data E8AC-E913 bytes 8
; OR A,AF / XOR A intentional overlapping opcodes; explicit DB settles it.
data E3CA-E3CB bytes 2
data E3DA-E3DB bytes 2
; EBE9 has LD BC,37C1; entries EBEA (POP BC) / EBEB (SCF) overlap it.
; Preserve these three executable bytes as DB; continue at EBEC.
data EBEA-EBEC bytes 3
entry EBEC
label EBEA DiskPopBc
label EBEB DiskCarry
comment EBE9 "Intentional overlapping LD BC / POP BC / SCF; exact DB fallback"
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
