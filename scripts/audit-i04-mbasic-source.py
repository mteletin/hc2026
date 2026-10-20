#!/usr/bin/env python3
"""Read-only I04 fixture/source audit. No HC library, guest, build, or utility replay."""
import hashlib
import math
from pathlib import Path
import struct

root = Path(__file__).resolve().parents[1]
path = root / 'disks/cpm/CPM22-HC.DSK'
original = path.read_bytes()
sha = lambda b: hashlib.sha256(b).hexdigest()
assert sha(original) == 'ff5a1c541c8aa80778a0f44d3ece398013084aafd9be1d3f32d2f68b774bb008'
assert original.startswith(b'EXTENDED CPC DSK File\r\nDisk-Info\r\n')
assert original[0x30:0x32] == bytes([80, 2])
# This pinned image has 160 physical tracks, 9 ordered 512-byte sectors each.
# Independent direct offsets, not an emulator/storage API export.
tracks = []
position = 256
for index in range(160):
    length = original[0x34 + index] * 256
    track = original[position:position + length]
    assert track.startswith(b'Track-Info\r\n')
    assert (track[0x10], track[0x11], track[0x15]) == (index // 2, index % 2, 9)
    sectors = []
    offset = 256
    for j in range(9):
        info = track[0x18 + j * 8:0x20 + j * 8]
        c, h, r, n, st1, st2, size = struct.unpack('<6BH', info)
        assert (c, h, n, st1, st2, size) == (index // 2, index % 2, 2, 0, 0, 512)
        sectors.append((r, track[offset:offset + size]))
        offset += size
    assert sorted(r for r, _ in sectors) == list(range(1, 10))
    # Original HC logical sector order: odd IDs, then even IDs (step2 modulo9).
    by_id = dict(sectors)
    tracks.append(b''.join(by_id[r] for r in (1, 3, 5, 7, 9, 2, 4, 6, 8)))
    position += length
assert position == len(original)
logical = b''.join(tracks)
assert len(logical) == 737280
# Published original HC DPB: OFF=4, SPT=36 128-byte records, block=2048,
# DRM=127, EXM=0, word allocation entries. Last physically backed block350.
data_start = 4 * 36 * 128
for name, size, expected in [
    ('MBASIC.COM', 24320, 'e6412bb1d8fcb2638b169dcde79741a4346975dae2de46272ce394d233ee2ada'),
    ('SQR.BAS', 256, 'c3613ec7e8b6b35e84ecbd8c27643273bef7179cc7f0bcc2c90ccb7fdbbc84d5')
]:
    stem, ext = name.split('.')
    field = (stem.ljust(8) + ext.ljust(3)).encode('ascii')
    extents = []
    for i in range(128):
        entry = logical[data_start + i * 32:data_start + (i + 1) * 32]
        if entry[0] != 0 or bytes(v & 127 for v in entry[1:12]) != field:
            continue
        extent = ((entry[14] & 63) << 5) | (entry[12] & 31)
        records = entry[15]
        blocks = [v for v in struct.unpack('<8H', entry[16:32]) if v]
        assert all(2 <= b <= 350 for b in blocks)
        payload = b''.join(logical[data_start + b * 2048:data_start + (b + 1) * 2048] for b in blocks)
        assert len(payload) >= records * 128
        extents.append((extent, payload[:records * 128]))
        print(name, 'extent', extent, 'records', records, 'blocks', blocks)
    extents.sort()
    assert [e for e, _ in extents] == list(range(len(extents)))
    payload = b''.join(data for _, data in extents)
    assert len(payload) == size and sha(payload) == expected
    print(name, 'bytes', size, 'SHA256', sha(payload))
    if name == 'SQR.BAS':
        eof = payload.index(0x1a)
        assert payload[:eof] == (
            b'10 FOR X%=0 TO 100\r\n20 P%=X%*X%\r\n30 S=SQR(P%)\r\n'
            b'40 E=X%-S\r\n50 IF E=0! THEN GOTO 90\r\n60 EA=ABS(E)\r\n'
            b'70 IF EA>EMAX THEN EMAX=EA\r\n80 PRINT X%,E\r\n90 NEXT X%\r\n'
            b'100 PRINT:PRINT"emax =";EMAX\r\n')
        print('firstCPMEOF', eof, 'tailBytesRetained', len(payload) - eof - 1)
        print(payload[:eof].decode('ascii'))
# Independent exact-integer oracle, not reproduced MBASIC floating arithmetic.
assert all(n * n <= 32767 and math.isqrt(n * n) == n for n in range(101))
print('oracle: 101 iterations; terminal X%=101 P%=10000 idealS=100 idealEMAX=0; preselected functional tolerance1e-4')
assert path.read_bytes() == original
print('image preserved SHA256', sha(original), 'logical SHA256', sha(logical))
