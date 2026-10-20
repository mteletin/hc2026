; Common documented/undocumented Zilog forms; pasmo 0.5.3 oracle.
org 08000h
start:
    ld sp,0ffffh
    ld bc,target
    ld a,low(target)
    ld ix,08100h
    ld iy,08200h
    ld (ix+5),42
    ld h,(ix-2)
    ld (iy+3),l
    add ix,de
    ld ixh,ixl
    ex af,af'
    sll (hl)
    rlc (ix+1)
    res 3,(iy-1)
    in a,(c)
    out (c),b
    jr nz,target
    djnz start
    rst 28h
target:
    ret
