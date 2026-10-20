#!/usr/bin/env python3
"""Authored synthetic layouts from hc2000-legacy-basic-import-v1/fixtures.
No ROM/program or historical compatibility evidence. Deterministic regeneration.
"""
from pathlib import Path
from hashlib import sha256

root = Path(__file__).parent
ram = bytearray((37*i+11) & 255 for i in range(49152))
ram[0x5000:0x5002] = bytes.fromhex('2381')
h = bytes.fromhex('123445236745238102903f560d563489679a78ab895678cdabbc9a010101')
s = bytes.fromhex('3fab899a7889677856674556344523cdabbc9a04d6341200900106')
compressed = h[:12] + bytes([h[12] | 0x20]) + h[13:]
terminator = bytes.fromhex('00eded00')

def zero_run(n):
    return bytes.fromhex('ededff00') * (n//255) + (bytes([0xed, 0xed, n%255, 0]) if n%255 else b'')

fixtures = {
    'legacy48-pattern-v1.z80': h + ram,
    'legacy48-stack.sna': s + ram,
    'legacy48-zero-v1-rle.z80': compressed + zero_run(49152) + terminator,
    'legacy48-literal-ed-v1-rle.z80': compressed + bytes.fromhex('ed00eded0500') + zero_run(49145) + terminator,
    'legacy48-byte12-ff-v1.z80': h[:12] + b'\xff' + h[13:] + ram,
}
for name, data in fixtures.items():
    (root/name).write_bytes(data)
    print(name, len(data), sha256(data).hexdigest())
