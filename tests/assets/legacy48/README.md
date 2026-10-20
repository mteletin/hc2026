# Synthetic legacy 48K layout fixtures

Authored test data from the accepted KB decision
`hc2000-legacy-basic-import-v1`, documents `decoder-contract` and `fixtures`.
These files contain synthetic headers/RAM, **not ROMs, games, BASIC programs,
real-machine snapshots, or evidence of program compatibility**. No upstream
implementation or third-party binary was copied. They are repository test data.

Regenerate deterministically with `python3 tests/assets/legacy48/generate.py`.
No network or external package is used. The focused `LegacySnapshotTests`
suite asserts each identity, normalized architectural fields and decoded RAM.

| File | Bytes | SHA256 |
| --- | ---: | --- |
| legacy48-pattern-v1.z80 | 49182 | 4233507a1d400338657c167e97682414f5db2885111756f9c7bb37ad25fc0c56 |
| legacy48-stack.sna | 49179 | c81405cd8bd4b18de2037d6bbecccf023f836fa6598653a66af72473b381810d |
| legacy48-zero-v1-rle.z80 | 806 | b5d0799537c47d560c6333ef0a2ba2af479e5301fc55d2f1e8248b89cbf2a4bf |
| legacy48-literal-ed-v1-rle.z80 | 812 | a71dc4ded7ffe592f69d5795b2328d9b49e5257dea4a28e6a93898388a7bd03f |
| legacy48-byte12-ff-v1.z80 | 49182 | f70d3504d22c1d508c100cb6f9f8cb9269893808842a15dd3e04d6c6d3e0deb7 |

RAM pattern: `(37*i+11)&255` for `i=0..49151`, replacing indexes
`0x5000..0x5001` with `23 81` (stacked PC at CPU address `9000`). Pattern RAM
SHA256: `9155f21b1fa64f70c61f5c43e1b05b1b001beaab6bb51c50a7e69d09c551fe03`.
The zero-RLE payload expands to all zeroes; literal-ED RLE expands to `ED`
followed by 49151 zeroes. The FF flag fixture is uncompressed after normalization.

Negative/boundary cases are derived in memory by the tests: lengths, banked or
extended layouts, invalid headers/stack pointers, SNA SP wrap, independent Z80
IFFs/R high-bit source, ignored hardware warnings, input bound and corrupt RLE.
Nothing is downloaded at test time. No live Machine import, boot, or `.hcs`
continuation is tested or implied by these fixtures.
