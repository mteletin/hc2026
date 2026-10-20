# Offline assembler compatibility corpus

These small original sources and expected binaries were generated on 2026-10-02
with Pasmo 0.5.3 (Ubuntu package `pasmo 0.5.3-7`, amd64). Tests only read the
checked-in files: no external assembler or network is needed at test time.

Generation:

```sh
pasmo --bin instructions.asm instructions.bin
pasmo --bin directives.asm directives.bin
```

Oracle package SHA256:
`65cf087453ce49c7d360255ac82cdb5555ed6059227497b02c3b5fc3289d2751`.

Expected binary SHA256:

| File | SHA256 |
|---|---|
| directives.bin | 85fb7cb113ccbf76e0f60d861c806443df323d93f553c5e08c406403d9320d05 |
| instructions.bin | 8b63fa4e374d73c146d5c2560608263696e11af80f00fb0d3479100869cb9938 |

Pasmo's supported overlap does not include PHASE/DEPHASE, redefinable `=`,
C `==`, `ENDR`, `IN F,(C)`, `OUT (C),0`, or indexed CB register-copy syntax.
Those required features have separate focused tests rather than pretending
Pasmo independently verifies them. No sjasmplus oracle has been run yet.
The oracle executable is not included; these sources are project-authored.
