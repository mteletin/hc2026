; Multipass expressions and block directives accepted by pasmo 0.5.3.
org 0100h
COUNT equ 3
value equ 1
pair macro left,right
    db left,right
endm
    pair 5,6
    db value,value,value
    if COUNT-2
    db value+1,low(endlabel),high(endlabel)
    else
    db 0
    endif
    defm "hello;world"
    defw -1,65536,endlabel
    defs 3,0aah
position:
    dw position,$
endlabel:
    dw $
