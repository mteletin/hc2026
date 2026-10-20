; Negative control at 8000h: executes actual Z80 but intentionally never paints.
        org 08000h
start:  di
        halt
