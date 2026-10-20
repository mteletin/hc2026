; HC2000 wi-124 visual proof, origin 8000h; actual writable screen RAM.
; CPU paints 32 alternating bitmap bytes and 32 white-ink attributes. No boot claim.
        org 08000h
start:  di
        ld hl,04000h
        ld b,020h
bitmap: ld (hl),0aah
        inc hl
        djnz bitmap       ; relative offset FBh
        ld hl,05800h
        ld b,020h
attrs:  ld (hl),007h
        inc hl
        djnz attrs        ; relative offset FBh
        halt
