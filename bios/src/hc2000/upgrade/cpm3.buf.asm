; Nonbanked CP/M 3 buffer block. Load at 8000h, below the BDOS, separate from
; the D300h BIOS and the 7800h resident BOOT block (binding: hc2000-cpm3-bios
; doc buffer-layout-ruling v2).
;
; This block must fit in 8000-B1FF without touching BDOS or page 7.
;
; Region constraint: the pages are the M: window. During an M: transfer this
; RAM is invisible; M: transfer integration must bounce accesses through a
; resident buffer. That integration is not yet implemented.
;
; Sizing (DRI System Guide 3.3):
;   directory/data buffers: one physical sector (PSH=2/PHM=3 => 512 bytes) for
;   both the 720K floppies and the C-F HDDs (all disk I/O is in physical
;   sector units, Table 3-7);
;   BCB: 12 bytes nonbanked, DRV=FF and BUFFAD the only fields that need
;   initial values (3.3.4);
;   CSV: (DRM/4)+1 bytes (CKS), removable media only - C-F is permanently
;   mounted (CKS=8000) and needs none;
;   ALV: (DSM/4)+2 bytes per drive; ALO/ALI overlay its first two bytes;
;   hash: 4*(DRM+1) bytes per hashed drive, unique per drive. C-F are the
;   in-scope hashed drives (DRM=511 => 2048 bytes each); A and B stay
;   unhashed (DPH HASH=FFFF), so no table is reserved for them.
;
; C-F share one directory BCB and one data BCB; A and B have their own.
; The image builder must load this block before BDOS uses the DPHs.
ORG 0x8000

; Directory buffers, one physical sector each.
DirBufA:    DEFS 512,0
DirBufB:    DEFS 512,0
DirBufCDEF: DEFS 512,0   ; shared by fixed drives C-F

; Data buffers, one physical sector each.
DataBufA:   DEFS 512,0
DataBufB:   DEFS 512,0
DataBufCDEF: DEFS 512,0  ; shared by fixed drives C-F

; BCBs (12 bytes): DRV, REC# (3), WFLG, scratch, TRACK, SECTOR, BUFFAD.
BcbDirA:    DB 0xFF      ; DRV: invalid until populated by BDOS
    DEFS 3,0
    DB 0,0               ; WFLG, scratch
    DEFS 4,0             ; TRACK and SECTOR, two bytes each
    DW DirBufA
BcbDirB:    DB 0xFF
    DEFS 3,0
    DB 0,0
    DEFS 4,0
    DW DirBufB
BcbDirCDEF: DB 0xFF
    DEFS 3,0
    DB 0,0
    DEFS 4,0
    DW DirBufCDEF
BcbDataA:   DB 0xFF
    DEFS 3,0
    DB 0,0
    DEFS 4,0
    DW DataBufA
BcbDataB:   DB 0xFF
    DEFS 3,0
    DB 0,0
    DEFS 4,0
    DW DataBufB
BcbDataCDEF: DB 0xFF
    DEFS 3,0
    DB 0,0
    DEFS 4,0
    DW DataBufCDEF

; Checksum vectors (DRM=127 => 33 bytes), removable media only.
CsvA: DEFS 33,0
CsvB: DEFS 33,0

; Allocation vectors: A/B (DSM=351 => 90 bytes), C-F (DSM=511 => 129 bytes).
AlvA: DEFS 90,0
AlvB: DEFS 90,0
AlvC: DEFS 129,0
AlvD: DEFS 129,0
AlvE: DEFS 129,0
AlvF: DEFS 129,0

; Directory hash tables (4*(DRM+1) = 2048 bytes), one per hashed drive.
HashC: DEFS 2048,0
HashD: DEFS 2048,0
HashE: DEFS 2048,0
HashF: DEFS 2048,0

; M: uses 128-byte physical records and its own allocation vector and BCBs.
; The transfer path must stage these buffers before mapping the page window.
AlvM:     DEFS 257,0
DirBufM:  DEFS 128,0
DataBufM: DEFS 128,0
BcbDirM:  DB 0xFF
    DEFS 3,0
    DB 0,0
    DEFS 4,0
    DW DirBufM
BcbDataM: DB 0xFF
    DEFS 3,0
    DB 0,0
    DEFS 4,0
    DW DataBufM
BufEnd:
