0000 F3           DI
0001 AF           XOR A
0002 11FFFF       LD DE,FFFF
0005 C3CB11       JP 11CB,START/NEW
0008 2A5D5C       LD HL,(5C5D)  {CH-ADD}
000B 225F5C       LD (5C5F),HL  {X-PTR}
000E 1843         JR 0053,ERROR-2
0010 C3F215       JP 15F2,PRINT-A-2
0013 FF           RST 38,MASK-INT
0014 FF           RST 38,MASK-INT
0015 FF           RST 38,MASK-INT
0016 FF           RST 38,MASK-INT
0017 FF           RST 38,MASK-INT
0018 2A5D5C       LD HL,(5C5D)  {CH-ADD}
001B 7E           LD A,(HL)
001C CD7D00       CALL 007D,SKIP-OVER
001F D0           RET NC
0020 CD7400       CALL 0074,CH-ADD+1
0023 18F7         JR 001C,TEST-CHAR
0025 FF           RST 38,MASK-INT
0026 FF           RST 38,MASK-INT
0027 FF           RST 38,MASK-INT
0028 C35B33       JP 335B,CALCULATE
002B FF           RST 38,MASK-INT
002C FF           RST 38,MASK-INT
002D FF           RST 38,MASK-INT
002E FF           RST 38,MASK-INT
002F FF           RST 38,MASK-INT
0030 C5           PUSH BC
0031 2A615C       LD HL,(5C61)  {WORKSP}
0034 E5           PUSH HL
0035 C39E16       JP 169E,RESERVE
0038 F5           PUSH AF
0039 E5           PUSH HL
003A 2A785C       LD HL,(5C78)  {FRAMES}
003D 23           INC HL
003E 22785C       LD (5C78),HL  {FRAMES}
0041 7C           LD A,H
0042 B5           OR L
0043 2003         JR NZ,0048,KEY-INT
0045 FD3440       INC (IY+40)
0048 C5           PUSH BC
0049 D5           PUSH DE
004A CDBF02       CALL 02BF,KEYBOARD
004D D1           POP DE
004E C1           POP BC
004F E1           POP HL
0050 F1           POP AF
0051 FB           EI
0052 C9           RET
0053 E1           POP HL
0054 6E           LD L,(HL)
0055 FD7500       LD (IY+00),L
0058 ED7B3D5C     LD SP,(5C3D)  {ERR-SP}
005C C3C516       JP 16C5,SET-STK
005F FF           RST 38,MASK-INT
0060 FF           RST 38,MASK-INT
0061 FF           RST 38,MASK-INT
0062 FF           RST 38,MASK-INT
0063 FF           RST 38,MASK-INT
0064 FF           RST 38,MASK-INT
0065 FF           RST 38,MASK-INT
0066 F5           PUSH AF
0067 E5           PUSH HL
0068 2AB05C       LD HL,(5CB0)  {NMIADD}
006B 7C           LD A,H
006C B5           OR L
006D 2001         JR NZ,0070,NO-RESET
006F E9           JP (HL)
0070 E1           POP HL
0071 F1           POP AF
0072 ED45         RETN
0074 2A5D5C       LD HL,(5C5D)  {CH-ADD}
0077 23           INC HL
0078 225D5C       LD (5C5D),HL  {CH-ADD}
007B 7E           LD A,(HL)
007C C9           RET
007D FE21         CP 21
007F D0           RET NC
0080 FE0D         CP 0D
0082 C8           RET Z
0083 FE10         CP 10
0085 D8           RET C
0086 FE18         CP 18
0088 3F           CCF
0089 D8           RET C
008A 23           INC HL
008B FE16         CP 16
008D 3801         JR C,0090,SKIPS
008F 23           INC HL
0090 37           SCF
0091 225D5C       LD (5C5D),HL  {CH-ADD}
0094 C9           RET
0095 BF           CP A
0096 52           LD D,D
0097 4E           LD C,(HL)
0098 C4494E       CALL NZ,4E49
009B 4B           LD C,E
009C 45           LD B,L
009D 59           LD E,C
009E A4           AND H
009F 50           LD D,B
00A0 C9           RET
00A1 46           LD B,(HL)
00A2 CE50         ADC A,50
00A4 4F           LD C,A
00A5 49           LD C,C
00A6 4E           LD C,(HL)
00A7 D45343       CALL NC,4353
00AA 52           LD D,D
00AB 45           LD B,L
00AC 45           LD B,L
00AD 4E           LD C,(HL)
00AE A4           AND H
00AF 41           LD B,C
00B0 54           LD D,H
00B1 54           LD D,H
00B2 D241D4       JP NC,D441
00B5 54           LD D,H
00B6 41           LD B,C
00B7 C25641       JP NZ,4156
00BA 4C           LD C,H
00BB A4           AND H
00BC 43           LD B,E
00BD 4F           LD C,A
00BE 44           LD B,H
00BF C5           PUSH BC
00C0 56           LD D,(HL)
00C1 41           LD B,C
00C2 CC4C45       CALL Z,454C
00C5 CE53         ADC A,53
00C7 49           LD C,C
00C8 CE43         ADC A,43
00CA 4F           LD C,A
00CB D354         OUT (54),A
00CD 41           LD B,C
00CE CE41         ADC A,41
00D0 53           LD D,E
00D1 CE41         ADC A,41
00D3 43           LD B,E
00D4 D341         OUT (41),A
00D6 54           LD D,H
00D7 CE4C         ADC A,4C
00D9 CE45         ADC A,45
00DB 58           LD E,B
00DC D0           RET NC
00DD 49           LD C,C
00DE 4E           LD C,(HL)
00DF D45351       CALL NC,5153
00E2 D25347       JP NC,4753
00E5 CE41         ADC A,41
00E7 42           LD B,D
00E8 D350         OUT (50),A
00EA 45           LD B,L
00EB 45           LD B,L
00EC CB49         BIT 1,C
00EE CE55         ADC A,55
00F0 53           LD D,E
00F1 D25354       JP NC,5453
00F4 52           LD D,D
00F5 A4           AND H
00F6 43           LD B,E
00F7 48           LD C,B
00F8 52           LD D,D
00F9 A4           AND H
00FA 4E           LD C,(HL)
00FB 4F           LD C,A
00FC D44249       CALL NC,4942
00FF CE4F         ADC A,4F
0101 D2414E       JP NC,4E41
0104 C43CBD       CALL NZ,BD3C
0107 3EBD         LD A,BD
0109 3C           INC A
010A BE           CP (HL)
010B 4C           LD C,H
010C 49           LD C,C
010D 4E           LD C,(HL)
010E C5           PUSH BC
010F 54           LD D,H
0110 48           LD C,B
0111 45           LD B,L
0112 CE54         ADC A,54
0114 CF           RST 08,ERROR-1
0115 53           DEFB 53        ;unknown
0116 54           LD D,H
0117 45           LD B,L
0118 D0           RET NC
0119 44           LD B,H
011A 45           LD B,L
011B 46           LD B,(HL)
011C 2046         JR NZ,0164
011E CE43         ADC A,43
0120 41           LD B,C
0121 D4464F       CALL NC,4F46
0124 52           LD D,D
0125 4D           LD C,L
0126 41           LD B,C
0127 D44D4F       CALL NC,4F4D
012A 56           LD D,(HL)
012B C5           PUSH BC
012C 45           LD B,L
012D 52           LD D,D
012E 41           LD B,C
012F 53           LD D,E
0130 C5           PUSH BC
0131 4F           LD C,A
0132 50           LD D,B
0133 45           LD B,L
0134 4E           LD C,(HL)
0135 20A3         JR NZ,00DA
0137 43           LD B,E
0138 4C           LD C,H
0139 4F           LD C,A
013A 53           LD D,E
013B 45           LD B,L
013C 20A3         JR NZ,00E1
013E 4D           LD C,L
013F 45           LD B,L
0140 52           LD D,D
0141 47           LD B,A
0142 C5           PUSH BC
0143 56           LD D,(HL)
0144 45           LD B,L
0145 52           LD D,D
0146 49           LD C,C
0147 46           LD B,(HL)
0148 D9           EXX
0149 42           LD B,D
014A 45           LD B,L
014B 45           LD B,L
014C D0           RET NC
014D 43           LD B,E
014E 49           LD C,C
014F 52           LD D,D
0150 43           LD B,E
0151 4C           LD C,H
0152 C5           PUSH BC
0153 49           LD C,C
0154 4E           LD C,(HL)
0155 CB50         BIT 2,B
0157 41           LD B,C
0158 50           LD D,B
0159 45           LD B,L
015A D2464C       JP NC,4C46
015D 41           LD B,C
015E 53           LD D,E
015F C8           RET Z
0160 42           LD B,D
0161 52           LD D,D
0162 49           LD C,C
0163 47           LD B,A
0164 48           LD C,B
0165 D4494E       CALL NC,4E49
0168 56           LD D,(HL)
0169 45           LD B,L
016A 52           LD D,D
016B 53           LD D,E
016C C5           PUSH BC
016D 4F           LD C,A
016E 56           LD D,(HL)
016F 45           LD B,L
0170 D24F55       JP NC,554F
0173 D44C50       CALL NC,504C
0176 52           LD D,D
0177 49           LD C,C
0178 4E           LD C,(HL)
0179 D44C4C       CALL NC,4C4C
017C 49           LD C,C
017D 53           LD D,E
017E D45354       CALL NC,5453
0181 4F           LD C,A
0182 D0           RET NC
0183 52           LD D,D
0184 45           LD B,L
0185 41           LD B,C
0186 C44441       CALL NZ,4144
0189 54           LD D,H
018A C1           POP BC
018B 52           LD D,D
018C 45           LD B,L
018D 53           LD D,E
018E 54           LD D,H
018F 4F           LD C,A
0190 52           LD D,D
0191 C5           PUSH BC
0192 4E           LD C,(HL)
0193 45           LD B,L
0194 D7           RST 10,PRINT-A-1
0195 42           LD B,D
0196 4F           LD C,A
0197 52           LD D,D
0198 44           LD B,H
0199 45           LD B,L
019A D2434F       JP NC,4F43
019D 4E           LD C,(HL)
019E 54           LD D,H
019F 49           LD C,C
01A0 4E           LD C,(HL)
01A1 55           LD D,L
01A2 C5           PUSH BC
01A3 44           LD B,H
01A4 49           LD C,C
01A5 CD5245       CALL 4552
01A8 CD464F       CALL 4F46
01AB D2474F       JP NC,4F47
01AE 2054         JR NZ,0204
01B0 CF           RST 08,ERROR-1
01B1 47           DEFB 47        ;unknown
01B2 4F           LD C,A
01B3 2053         JR NZ,0208
01B5 55           LD D,L
01B6 C2494E       JP NZ,4E49
01B9 50           LD D,B
01BA 55           LD D,L
01BB D44C4F       CALL NC,4F4C
01BE 41           LD B,C
01BF C44C49       CALL NZ,494C
01C2 53           LD D,E
01C3 D44C45       CALL NC,454C
01C6 D45041       CALL NC,4150
01C9 55           LD D,L
01CA 53           LD D,E
01CB C5           PUSH BC
01CC 4E           LD C,(HL)
01CD 45           LD B,L
01CE 58           LD E,B
01CF D4504F       CALL NC,4F50
01D2 4B           LD C,E
01D3 C5           PUSH BC
01D4 50           LD D,B
01D5 52           LD D,D
01D6 49           LD C,C
01D7 4E           LD C,(HL)
01D8 D4504C       CALL NC,4C50
01DB 4F           LD C,A
01DC D45255       CALL NC,5552
01DF CE53         ADC A,53
01E1 41           LD B,C
01E2 56           LD D,(HL)
01E3 C5           PUSH BC
01E4 52           LD D,D
01E5 41           LD B,C
01E6 4E           LD C,(HL)
01E7 44           LD B,H
01E8 4F           LD C,A
01E9 4D           LD C,L
01EA 49           LD C,C
01EB 5A           LD E,D
01EC C5           PUSH BC
01ED 49           LD C,C
01EE C643         ADD A,43
01F0 4C           LD C,H
01F1 D344         OUT (44),A
01F3 52           LD D,D
01F4 41           LD B,C
01F5 D7           RST 10,PRINT-A-1
01F6 43           LD B,E
01F7 4C           LD C,H
01F8 45           LD B,L
01F9 41           LD B,C
01FA D25245       JP NC,4552
01FD 54           LD D,H
01FE 55           LD D,L
01FF 52           LD D,D
0200 CE43         ADC A,43
0202 4F           LD C,A
0203 50           LD D,B
0204 D9           EXX
0205 42           LD B,D
0206 48           LD C,B
0207 59           LD E,C
0208 3635         LD (HL),35
020A 54           LD D,H
020B 47           LD B,A
020C 56           LD D,(HL)
020D 4E           LD C,(HL)
020E 4A           LD C,D
020F 55           LD D,L
0210 37           SCF
0211 34           INC (HL)
0212 52           LD D,D
0213 46           LD B,(HL)
0214 43           LD B,E
0215 4D           LD C,L
0216 4B           LD C,E
0217 49           LD C,C
0218 3833         JR C,024D
021A 45           LD B,L
021B 44           LD B,H
021C 58           LD E,B
021D 0E4C         LD C,4C
021F 4F           LD C,A
0220 39           ADD HL,SP
0221 325753       LD (5357),A
0224 5A           LD E,D
0225 200D         JR NZ,0234
0227 50           LD D,B
0228 3031         JR NC,025B
022A 51           LD D,C
022B 41           LD B,C
022C E3           EX SP,(HL)
022D C4E0E4       CALL NZ,E4E0
0230 B4           OR H
0231 BC           CP H
0232 BD           CP L
0233 BB           CP E
0234 AF           XOR A
0235 B0           OR B
0236 B1           OR C
0237 C0           RET NZ
0238 A7           AND A
0239 A6           AND (HL)
023A BE           CP (HL)
023B AD           XOR L
023C B2           OR D
023D BA           CP D
023E E5           PUSH HL
023F A5           AND L
0240 C2E1B3       JP NZ,B3E1
0243 B9           CP C
0244 C1           POP BC
0245 B8           CP B
0246 7E           LD A,(HL)
0247 DCDA5C       CALL C,5CDA
024A B7           OR A
024B 7B           LD A,E
024C 7D           LD A,L
024D D8           RET C
024E BF           CP A
024F AE           XOR (HL)
0250 AA           XOR D
0251 AB           XOR E
0252 DD           NOP
0253 DEDF         SBC A,DF
0255 7F           LD A,A
0256 B5           OR L
0257 D67C         SUB 7C
0259 D5           PUSH DE
025A 5D           LD E,L
025B DBB6         IN A,(B6)
025D D9           EXX
025E 5B           LD E,E
025F D7           RST 10,PRINT-A-1
0260 0C           INC C
0261 07           RLCA
0262 0604         LD B,04
0264 05           DEC B
0265 08           EX AF,AF'
0266 0A           LD A,(BC)
0267 0B           DEC BC
0268 09           ADD HL,BC
0269 0F           RRCA
026A E22A3F       JP PO,3F2A
026D CDC8CC       CALL CCC8
0270 CB5E         BIT 3,(HL)
0272 AC           XOR H
0273 2D           DEC L
0274 2B           DEC HL
0275 3D           DEC A
0276 2E2C         LD L,2C
0278 3B           DEC SP
0279 22C73C       LD (3CC7),HL
027C C33EC5       JP C53E
027F 2F           CPL
0280 C9           RET
0281 60           LD H,B
0282 C63A         ADD A,3A
0284 D0           RET NC
0285 CEA8         ADC A,A8
0287 CAD3D4       JP Z,D4D3
028A D1           POP DE
028B D2A9CF       JP NC,CFA9
028E 2E2F         LD L,2F
0290 11FFFF       LD DE,FFFF
0293 01FEFE       LD BC,FEFE
0296 ED78         IN A,(C)
0298 2F           CPL
0299 E61F         AND 1F
029B 280E         JR Z,02AB,KEY-DONE
029D 67           LD H,A
029E 7D           LD A,L
029F 14           INC D
02A0 C0           RET NZ
02A1 D608         SUB 08
02A3 CB3C         SRL H
02A5 30FA         JR NC,02A1,KEY-BITS
02A7 53           LD D,E
02A8 5F           LD E,A
02A9 20F4         JR NZ,029F,KEY-3KEYS
02AB 2D           DEC L
02AC CB00         RLC B
02AE 38E6         JR C,0296,KEY-LINE
02B0 7A           LD A,D
02B1 3C           INC A
02B2 C8           RET Z
02B3 FE28         CP 28
02B5 C8           RET Z
02B6 FE19         CP 19
02B8 C8           RET Z
02B9 7B           LD A,E
02BA 5A           LD E,D
02BB 57           LD D,A
02BC FE18         CP 18
02BE C9           RET
02BF CD8E02       CALL 028E,KEY-SCAN
02C2 C0           RET NZ
02C3 21005C       LD HL,5C00
02C6 CB7E         BIT 7,(HL)
02C8 2007         JR NZ,02D1,K-CH-SET
02CA 23           INC HL
02CB 35           DEC (HL)
02CC 2B           DEC HL
02CD 2002         JR NZ,02D1,K-CH-SET
02CF 36FF         LD (HL),FF
02D1 7D           LD A,L
02D2 21045C       LD HL,5C04
02D5 BD           CP L
02D6 20EE         JR NZ,02C6,K-ST-LOOP
02D8 CD1E03       CALL 031E,K-TEST
02DB D0           RET NC
02DC 21005C       LD HL,5C00
02DF BE           CP (HL)
02E0 282E         JR Z,0310,K-REPEAT
02E2 EB           EX DE,HL
02E3 21045C       LD HL,5C04
02E6 BE           CP (HL)
02E7 2827         JR Z,0310,K-REPEAT
02E9 CB7E         BIT 7,(HL)
02EB 2004         JR NZ,02F1,K-NEW
02ED EB           EX DE,HL
02EE CB7E         BIT 7,(HL)
02F0 C8           RET Z
02F1 5F           LD E,A
02F2 77           LD (HL),A
02F3 23           INC HL
02F4 3605         LD (HL),05
02F6 23           INC HL
02F7 3A095C       LD A,(5C09)  {REPDEL}
02FA 77           LD (HL),A
02FB 23           INC HL
02FC FD4E07       LD C,(IY+07)
02FF FD5601       LD D,(IY+01)
0302 E5           PUSH HL
0303 CD3303       CALL 0333,K-DECODE
0306 E1           POP HL
0307 77           LD (HL),A
0308 32085C       LD (5C08),A  {LAST-K}
030B FDCB01EE     SET 5,(IY+01)
030F C9           RET
0310 23           INC HL
0311 3605         LD (HL),05
0313 23           INC HL
0314 35           DEC (HL)
0315 C0           RET NZ
0316 3A0A5C       LD A,(5C0A)  {REPPER}
0319 77           LD (HL),A
031A 23           INC HL
031B 7E           LD A,(HL)
031C 18EA         JR 0308,K-END
031E 42           LD B,D
031F 1600         LD D,00
0321 7B           LD A,E
0322 FE27         CP 27
0324 D0           RET NC
0325 FE18         CP 18
0327 2003         JR NZ,032C,K-MAIN
0329 CB78         BIT 7,B
032B C0           RET NZ
032C 210502       LD HL,0205
032F 19           ADD HL,DE
0330 7E           LD A,(HL)
0331 37           SCF
0332 C9           RET
0333 7B           LD A,E
0334 FE3A         CP 3A
0336 382F         JR C,0367,K-DIGIT
0338 0D           DEC C
0339 FA4F03       JP M,034F,K-KLC-LET
033C 2803         JR Z,0341,K-E-LET
033E C64F         ADD A,4F
0340 C9           RET
0341 21EB01       LD HL,01EB
0344 04           INC B
0345 2803         JR Z,034A,K-LOOK-UP
0347 210502       LD HL,0205
034A 1600         LD D,00
034C 19           ADD HL,DE
034D 7E           LD A,(HL)
034E C9           RET
034F 212902       LD HL,0229
0352 CB40         BIT 0,B
0354 28F4         JR Z,034A,K-LOOK-UP
0356 CB5A         BIT 3,D
0358 280A         JR Z,0364,K-TOKENS
035A FDCB305E     BIT 3,(IY+30)
035E C0           RET NZ
035F 04           INC B
0360 C0           RET NZ
0361 C620         ADD A,20
0363 C9           RET
0364 C6A5         ADD A,A5
0366 C9           RET
0367 FE30         CP 30
0369 D8           RET C
036A 0D           DEC C
036B FA9D03       JP M,039D,K-KLC-DGT
036E 2019         JR NZ,0389,K-GRA-DGT
0370 215402       LD HL,0254
0373 CB68         BIT 5,B
0375 28D3         JR Z,034A,K-LOOK-UP
0377 FE38         CP 38
0379 3007         JR NC,0382,K-8-&-9
037B D620         SUB 20
037D 04           INC B
037E C8           RET Z
037F C608         ADD A,08
0381 C9           RET
0382 D636         SUB 36
0384 04           INC B
0385 C8           RET Z
0386 C6FE         ADD A,FE
0388 C9           RET
0389 213002       LD HL,0230
038C FE39         CP 39
038E 28BA         JR Z,034A,K-LOOK-UP
0390 FE30         CP 30
0392 28B6         JR Z,034A,K-LOOK-UP
0394 E607         AND 07
0396 C680         ADD A,80
0398 04           INC B
0399 C8           RET Z
039A EE0F         XOR 0F
039C C9           RET
039D 04           INC B
039E C8           RET Z
039F CB68         BIT 5,B
03A1 213002       LD HL,0230
03A4 20A4         JR NZ,034A,K-LOOK-UP
03A6 D610         SUB 10
03A8 FE22         CP 22
03AA 2806         JR Z,03B2,K-@-CHAR
03AC FE20         CP 20
03AE C0           RET NZ
03AF 3E5F         LD A,5F
03B1 C9           RET
03B2 3E40         LD A,40
03B4 C9           RET
03B5 F3           DI
03B6 7D           LD A,L
03B7 CB3D         SRL L
03B9 CB3D         SRL L
03BB 2F           CPL
03BC E603         AND 03
03BE 4F           LD C,A
03BF 0600         LD B,00
03C1 DD21D103     LD IX,03D1
03C5 DD09         ADD IX,BC
03C7 3A485C       LD A,(5C48)  {BORDCR}
03CA E638         AND 38
03CC 0F           RRCA
03CD 0F           RRCA
03CE 0F           RRCA
03CF F608         OR 08
03D1 00           NOP
03D2 00           NOP
03D3 00           NOP
03D4 04           INC B
03D5 0C           INC C
03D6 0D           DEC C
03D7 20FD         JR NZ,03D6,BE-H&L-LP
03D9 0E3F         LD C,3F
03DB 05           DEC B
03DC C2D603       JP NZ,03D6,BE-H&L-LP
03DF EE10         XOR 10
03E1 D3FE         OUT (FE),A
03E3 44           LD B,H
03E4 4F           LD C,A
03E5 CB67         BIT 4,A
03E7 2009         JR NZ,03F2,BE-AGAIN
03E9 7A           LD A,D
03EA B3           OR E
03EB 2809         JR Z,03F6,BE-END
03ED 79           LD A,C
03EE 4D           LD C,L
03EF 1B           DEC DE
03F0 DDE9         JP (IX)
03F2 4D           LD C,L
03F3 0C           INC C
03F4 DDE9         JP (IX)
03F6 FB           EI
03F7 C9           RET
03F8 EF           RST 28,FP-CALC
03F9 31           DEFB 31,duplicate
03FA 27           DEFB 27,int
03FB C0           DEFB C0,st-mem-0
03FC 03           DEFB 03,subtract
03FD 34           DEFB 34,stk-data
03FE EC6C981FF5   DEFB EC,6C,98,1F,F5        ;0.0EC981FF5
0403 04           DEFB 04,multiply
0404 A1           DEFB A1,stk-one
0405 0F           DEFB 0F,addition
0406 38           DEFB 38,end-calc
0407 21925C       LD HL,5C92
040A 7E           LD A,(HL)
040B A7           AND A
040C 205E         JR NZ,046C,REPORT-B
040E 23           INC HL
040F 4E           LD C,(HL)
0410 23           INC HL
0411 46           LD B,(HL)
0412 78           LD A,B
0413 17           RLA
0414 9F           SBC A,A
0415 B9           CP C
0416 2054         JR NZ,046C,REPORT-B
0418 23           INC HL
0419 BE           CP (HL)
041A 2050         JR NZ,046C,REPORT-B
041C 78           LD A,B
041D C63C         ADD A,3C
041F F22504       JP P,0425,BE-i-OK
0422 E26C04       JP PO,046C,REPORT-B
0425 06FA         LD B,FA
0427 04           INC B
0428 D60C         SUB 0C
042A 30FB         JR NC,0427,BE-OCTAVE
042C C60C         ADD A,0C
042E C5           PUSH BC
042F 216E04       LD HL,046E
0432 CD0634       CALL 3406,LOC-MEM
0435 CDB433       CALL 33B4,STACK-NUM
0438 EF           RST 28,FP-CALC
0439 04           DEFB 04,multiply
043A 38           DEFB 38,end-calc
043B F1           POP AF
043C 86           ADD A,(HL)
043D 77           LD (HL),A
043E EF           RST 28,FP-CALC
043F C0           DEFB C0,st-mem-0
0440 02           DEFB 02,delete
0441 31           DEFB 31,duplicate
0442 38           DEFB 38,end-calc
0443 CD941E       CALL 1E94,FIND-INT1
0446 FE0B         CP 0B
0448 3022         JR NC,046C,REPORT-B
044A EF           RST 28,FP-CALC
044B E0           DEFB E0,get-mem-0
044C 04           DEFB 04,multiply
044D E0           DEFB E0,get-mem-0
044E 34           DEFB 34,stk-data
044F 8043559F80   DEFB 80,43,55,9F,80        ;6ACFC
0454 01           DEFB 01,exchange
0455 05           DEFB 05,division
0456 34           DEFB 34,stk-data
0457 3571         DEFB 35,71        ;1E.2
0459 03           DEFB 03,subtract
045A 38           DEFB 38,end-calc
045B CD991E       CALL 1E99,FIND-INT2
045E C5           PUSH BC
045F CD991E       CALL 1E99,FIND-INT2
0462 E1           POP HL
0463 50           LD D,B
0464 59           LD E,C
0465 7A           LD A,D
0466 B3           OR E
0467 C8           RET Z
0468 1B           DEC DE
0469 C3B503       JP 03B5,BEEPER
046C CF           RST 08,ERROR-1
046D 0A           DEFB 0A        ;B Integer out of range
046E 89           ADC A,C
046F 02           LD (BC),A
0470 D0           RET NC
0471 12           LD (DE),A
0472 86           ADD A,(HL)
0473 89           ADC A,C
0474 0A           LD A,(BC)
0475 97           SUB A
0476 60           LD H,B
0477 75           LD (HL),L
0478 89           ADC A,C
0479 12           LD (DE),A
047A D5           PUSH DE
047B 17           RLA
047C 1F           RRA
047D 89           ADC A,C
047E 1B           DEC DE
047F 90           SUB B
0480 41           LD B,C
0481 02           LD (BC),A
0482 89           ADC A,C
0483 24           INC H
0484 D0           RET NC
0485 53           LD D,E
0486 CA892E       JP Z,2E89
0489 9D           SBC A,L
048A 36B1         LD (HL),B1
048C 89           ADC A,C
048D 38FF         JR C,048E
048F 49           LD C,C
0490 3E89         LD A,89
0492 43           LD B,E
0493 FF           RST 38,MASK-INT
0494 6A           LD L,D
0495 73           LD (HL),E
0496 89           ADC A,C
0497 4F           LD C,A
0498 A7           AND A
0499 00           NOP
049A 54           LD D,H
049B 89           ADC A,C
049C 5C           LD E,H
049D 00           NOP
049E 00           NOP
049F 00           NOP
04A0 89           ADC A,C
04A1 69           LD L,C
04A2 14           INC D
04A3 F624         OR 24
04A5 89           ADC A,C
04A6 76           HALT
04A7 F1           POP AF
04A8 1005         DJNZ 04AF
04AA CDFB24       CALL 24FB,SCANNING
04AD 3A3B5C       LD A,(5C3B)  {FLAGS}
04B0 87           ADD A,A
04B1 FA8A1C       JP M,1C8A,REPORT-C
04B4 E1           POP HL
04B5 D0           RET NC
04B6 E5           PUSH HL
04B7 CDF12B       CALL 2BF1,STK-FETCH
04BA 62           LD H,D
04BB 6B           LD L,E
04BC 0D           DEC C
04BD F8           RET M
04BE 09           ADD HL,BC
04BF CBFE         SET 7,(HL)
04C1 C9           RET
04C2 213F05       LD HL,053F
04C5 E5           PUSH HL
04C6 21801F       LD HL,1F80
04C9 CB7F         BIT 7,A
04CB 2803         JR Z,04D0,SA-FLAG
04CD 21980C       LD HL,0C98
04D0 08           EX AF,AF'
04D1 13           INC DE
04D2 DD2B         DEC IX
04D4 F3           DI
04D5 3E02         LD A,02
04D7 47           LD B,A
04D8 10FE         DJNZ 04D8,SA-LEADER
04DA D3FE         OUT (FE),A
04DC EE0F         XOR 0F
04DE 06A4         LD B,A4
04E0 2D           DEC L
04E1 20F5         JR NZ,04D8,SA-LEADER
04E3 05           DEC B
04E4 25           DEC H
04E5 F2D804       JP P,04D8,SA-LEADER
04E8 062F         LD B,2F
04EA 10FE         DJNZ 04EA,SA-SYNC-1
04EC D3FE         OUT (FE),A
04EE 3E0D         LD A,0D
04F0 0637         LD B,37
04F2 10FE         DJNZ 04F2,SA-SYNC-2
04F4 D3FE         OUT (FE),A
04F6 010E3B       LD BC,3B0E
04F9 08           EX AF,AF'
04FA 6F           LD L,A
04FB C30705       JP 0507,SA-START
04FE 7A           LD A,D
04FF B3           OR E
0500 280C         JR Z,050E,SA-PARITY
0502 DD6E00       LD L,(IX+00)
0505 7C           LD A,H
0506 AD           XOR L
0507 67           LD H,A
0508 3E01         LD A,01
050A 37           SCF
050B C32505       JP 0525,SA-8-BITS
050E 6C           LD L,H
050F 18F4         JR 0505,SA-LOOP-P
0511 79           LD A,C
0512 CB78         BIT 7,B
0514 10FE         DJNZ 0514,SA-BIT-1
0516 3004         JR NC,051C,SA-OUT
0518 0642         LD B,42
051A 10FE         DJNZ 051A,SA-SET
051C D3FE         OUT (FE),A
051E 063E         LD B,3E
0520 20EF         JR NZ,0511,SA-BIT-2
0522 05           DEC B
0523 AF           XOR A
0524 3C           INC A
0525 CB15         RL L
0527 C21405       JP NZ,0514,SA-BIT-1
052A 1B           DEC DE
052B DD23         INC IX
052D 0631         LD B,31
052F 3E7F         LD A,7F
0531 DBFE         IN A,(FE)
0533 1F           RRA
0534 D0           RET NC
0535 7A           LD A,D
0536 3C           INC A
0537 C2FE04       JP NZ,04FE,SA-LOOP
053A 063B         LD B,3B
053C 10FE         DJNZ 053C,SA-DELAY
053E C9           RET
053F F5           PUSH AF
0540 3A485C       LD A,(5C48)  {BORDCR}
0543 E638         AND 38
0545 0F           RRCA
0546 0F           RRCA
0547 0F           RRCA
0548 D3FE         OUT (FE),A
054A 3E7F         LD A,7F
054C DBFE         IN A,(FE)
054E 1F           RRA
054F FB           EI
0550 3802         JR C,0554,SA/LD-END
0552 CF           RST 08,ERROR-1
0553 0C           DEFB 0C        ;D BREAK - CONT repeats
0554 F1           POP AF
0555 C9           RET
0556 14           INC D
0557 08           EX AF,AF'
0558 15           DEC D
0559 F3           DI
055A 3E0F         LD A,0F
055C D3FE         OUT (FE),A
055E 213F05       LD HL,053F
0561 E5           PUSH HL
0562 DBFE         IN A,(FE)
0564 1F           RRA
0565 E620         AND 20
0567 F602         OR 02
0569 4F           LD C,A
056A BF           CP A
056B C0           RET NZ
056C CDE705       CALL 05E7,LD-EDGE-1
056F 30FA         JR NC,056B,LD-BREAK
0571 211504       LD HL,0415
0574 10FE         DJNZ 0574,LD-WAIT
0576 2B           DEC HL
0577 7C           LD A,H
0578 B5           OR L
0579 20F9         JR NZ,0574,LD-WAIT
057B CDE305       CALL 05E3,LD-EDGE-2
057E 30EB         JR NC,056B,LD-BREAK
0580 069C         LD B,9C
0582 CDE305       CALL 05E3,LD-EDGE-2
0585 30E4         JR NC,056B,LD-BREAK
0587 3EC6         LD A,C6
0589 B8           CP B
058A 30E0         JR NC,056C,LD-START
058C 24           INC H
058D 20F1         JR NZ,0580,LD-LEADER
058F 06C9         LD B,C9
0591 CDE705       CALL 05E7,LD-EDGE-1
0594 30D5         JR NC,056B,LD-BREAK
0596 78           LD A,B
0597 FED4         CP D4
0599 30F4         JR NC,058F,LD-SYNC
059B CDE705       CALL 05E7,LD-EDGE-1
059E D0           RET NC
059F 79           LD A,C
05A0 EE03         XOR 03
05A2 4F           LD C,A
05A3 2600         LD H,00
05A5 06B0         LD B,B0
05A7 181F         JR 05C8,LD-MARKER
05A9 08           EX AF,AF'
05AA 2007         JR NZ,05B3,LD-FLAG
05AC 300F         JR NC,05BD,LD-VERIFY
05AE DD7500       LD (IX+00),L
05B1 180F         JR 05C2,LD-NEXT
05B3 CB11         RL C
05B5 AD           XOR L
05B6 C0           RET NZ
05B7 79           LD A,C
05B8 1F           RRA
05B9 4F           LD C,A
05BA 13           INC DE
05BB 1807         JR 05C4,LD-DEC
05BD DD7E00       LD A,(IX+00)
05C0 AD           XOR L
05C1 C0           RET NZ
05C2 DD23         INC IX
05C4 1B           DEC DE
05C5 08           EX AF,AF'
05C6 06B2         LD B,B2
05C8 2E01         LD L,01
05CA CDE305       CALL 05E3,LD-EDGE-2
05CD D0           RET NC
05CE 3ECB         LD A,CB
05D0 B8           CP B
05D1 CB15         RL L
05D3 06B0         LD B,B0
05D5 D2CA05       JP NC,05CA,LD-8-BITS
05D8 7C           LD A,H
05D9 AD           XOR L
05DA 67           LD H,A
05DB 7A           LD A,D
05DC B3           OR E
05DD 20CA         JR NZ,05A9,LD-LOOP
05DF 7C           LD A,H
05E0 FE01         CP 01
05E2 C9           RET
05E3 CDE705       CALL 05E7,LD-EDGE-1
05E6 D0           RET NC
05E7 3E16         LD A,16
05E9 3D           DEC A
05EA 20FD         JR NZ,05E9,LD-DELAY
05EC A7           AND A
05ED 04           INC B
05EE C8           RET Z
05EF 3E7F         LD A,7F
05F1 DBFE         IN A,(FE)
05F3 1F           RRA
05F4 D0           RET NC
05F5 A9           XOR C
05F6 E620         AND 20
05F8 28F3         JR Z,05ED,LD-SAMPLE
05FA 79           LD A,C
05FB 2F           CPL
05FC 4F           LD C,A
05FD E607         AND 07
05FF F608         OR 08
0601 D3FE         OUT (FE),A
0603 37           SCF
0604 C9           RET
0605 F1           POP AF
0606 3A745C       LD A,(5C74)  {T-ADDR}
0609 D6E0         SUB E0
060B 32745C       LD (5C74),A  {T-ADDR}
060E CD8C1C       CALL 1C8C,EXPT-EXP
0611 CD3025       CALL 2530,SYNTAX-Z
0614 283C         JR Z,0652,SA-DATA
0616 011100       LD BC,0011
0619 3A745C       LD A,(5C74)  {T-ADDR}
061C A7           AND A
061D 2802         JR Z,0621,SA-SPACE
061F 0E22         LD C,22
0621 F7           RST 30,BC-SPACES
0622 D5           PUSH DE
0623 DDE1         POP IX
0625 060B         LD B,0B
0627 3E20         LD A,20
0629 12           LD (DE),A
062A 13           INC DE
062B 10FC         DJNZ 0629,SA-BLANK
062D DD3601FF     LD (IX+01),FF
0631 CDF12B       CALL 2BF1,STK-FETCH
0634 21F6FF       LD HL,FFF6
0637 0B           DEC BC
0638 09           ADD HL,BC
0639 03           INC BC
063A 300F         JR NC,064B,SA-NAME
063C 3A745C       LD A,(5C74)  {T-ADDR}
063F A7           AND A
0640 2002         JR NZ,0644,SA-NULL
0642 CF           RST 08,ERROR-1
0643 0E           DEFB 0E        ;F Invalid file name
0644 78           LD A,B
0645 B1           OR C
0646 280A         JR Z,0652,SA-DATA
0648 010A00       LD BC,000A
064B DDE5         PUSH IX
064D E1           POP HL
064E 23           INC HL
064F EB           EX DE,HL
0650 EDB0         LDIR
0652 DF           RST 18,GET-CHAR
0653 FEE4         CP E4
0655 2049         JR NZ,06A0,SA-SCR$
0657 3A745C       LD A,(5C74)  {T-ADDR}
065A FE03         CP 03
065C CA8A1C       JP Z,1C8A,REPORT-C
065F E7           RST 20,NEXT-CHAR
0660 CDB228       CALL 28B2,LOOK-VARS
0663 CBF9         SET 7,C
0665 300B         JR NC,0672,SA-V-OLD
0667 210000       LD HL,0000
066A 3A745C       LD A,(5C74)  {T-ADDR}
066D 3D           DEC A
066E 2815         JR Z,0685,SA-V-NEW
0670 CF           RST 08,ERROR-1
0671 01           DEFB 01        ;2 Variable not found
0672 C28A1C       JP NZ,1C8A,REPORT-C
0675 CD3025       CALL 2530,SYNTAX-Z
0678 2818         JR Z,0692,SA-DATA-1
067A 23           INC HL
067B 7E           LD A,(HL)
067C DD770B       LD (IX+0B),A
067F 23           INC HL
0680 7E           LD A,(HL)
0681 DD770C       LD (IX+0C),A
0684 23           INC HL
0685 DD710E       LD (IX+0E),C
0688 3E01         LD A,01
068A CB71         BIT 6,C
068C 2801         JR Z,068F,SA-V-TYPE
068E 3C           INC A
068F DD7700       LD (IX+00),A
0692 EB           EX DE,HL
0693 E7           RST 20,NEXT-CHAR
0694 FE29         CP 29
0696 20DA         JR NZ,0672,SA-V-OLD
0698 E7           RST 20,NEXT-CHAR
0699 CDEE1B       CALL 1BEE,CHECK-END
069C EB           EX DE,HL
069D C35A07       JP 075A,SA-ALL
06A0 FEAA         CP AA
06A2 201F         JR NZ,06C3,SA-CODE
06A4 3A745C       LD A,(5C74)  {T-ADDR}
06A7 FE03         CP 03
06A9 CA8A1C       JP Z,1C8A,REPORT-C
06AC E7           RST 20,NEXT-CHAR
06AD CDEE1B       CALL 1BEE,CHECK-END
06B0 DD360B00     LD (IX+0B),00
06B4 DD360C1B     LD (IX+0C),1B
06B8 210040       LD HL,4000
06BB DD750D       LD (IX+0D),L
06BE DD740E       LD (IX+0E),H
06C1 184D         JR 0710,SA-TYPE-3
06C3 FEAF         CP AF
06C5 204F         JR NZ,0716,SA-LINE
06C7 3A745C       LD A,(5C74)  {T-ADDR}
06CA FE03         CP 03
06CC CA8A1C       JP Z,1C8A,REPORT-C
06CF E7           RST 20,NEXT-CHAR
06D0 CD4820       CALL 2048,PR-ST-END
06D3 200C         JR NZ,06E1,SA-CODE-1
06D5 3A745C       LD A,(5C74)  {T-ADDR}
06D8 A7           AND A
06D9 CA8A1C       JP Z,1C8A,REPORT-C
06DC CDE61C       CALL 1CE6,USE-ZERO
06DF 180F         JR 06F0,SA-CODE-2
06E1 CD821C       CALL 1C82,EXPT-1NUM
06E4 DF           RST 18,GET-CHAR
06E5 FE2C         CP 2C
06E7 280C         JR Z,06F5,SA-CODE-3
06E9 3A745C       LD A,(5C74)  {T-ADDR}
06EC A7           AND A
06ED CA8A1C       JP Z,1C8A,REPORT-C
06F0 CDE61C       CALL 1CE6,USE-ZERO
06F3 1804         JR 06F9,SA-CODE-4
06F5 E7           RST 20,NEXT-CHAR
06F6 CD821C       CALL 1C82,EXPT-1NUM
06F9 CDEE1B       CALL 1BEE,CHECK-END
06FC CD991E       CALL 1E99,FIND-INT2
06FF DD710B       LD (IX+0B),C
0702 DD700C       LD (IX+0C),B
0705 CD991E       CALL 1E99,FIND-INT2
0708 DD710D       LD (IX+0D),C
070B DD700E       LD (IX+0E),B
070E 60           LD H,B
070F 69           LD L,C
0710 DD360003     LD (IX+00),03
0714 1844         JR 075A,SA-ALL
0716 FECA         CP CA
0718 2809         JR Z,0723,SA-LINE-1
071A CDEE1B       CALL 1BEE,CHECK-END
071D DD360E80     LD (IX+0E),80
0721 1817         JR 073A,SA-TYPE-0
0723 3A745C       LD A,(5C74)  {T-ADDR}
0726 A7           AND A
0727 C28A1C       JP NZ,1C8A,REPORT-C
072A E7           RST 20,NEXT-CHAR
072B CD821C       CALL 1C82,EXPT-1NUM
072E CDEE1B       CALL 1BEE,CHECK-END
0731 CD991E       CALL 1E99,FIND-INT2
0734 DD710D       LD (IX+0D),C
0737 DD700E       LD (IX+0E),B
073A DD360000     LD (IX+00),00
073E 2A595C       LD HL,(5C59)  {E-LINE}
0741 ED5B535C     LD DE,(5C53)  {PROG}
0745 37           SCF
0746 ED52         SBC HL,DE
0748 DD750B       LD (IX+0B),L
074B DD740C       LD (IX+0C),H
074E 2A4B5C       LD HL,(5C4B)  {VARS}
0751 ED52         SBC HL,DE
0753 DD750F       LD (IX+0F),L
0756 DD7410       LD (IX+10),H
0759 EB           EX DE,HL
075A 3A745C       LD A,(5C74)  {T-ADDR}
075D A7           AND A
075E CA7009       JP Z,0970,SA-CONTRL
0761 E5           PUSH HL
0762 011100       LD BC,0011
0765 DD09         ADD IX,BC
0767 DDE5         PUSH IX
0769 111100       LD DE,0011
076C AF           XOR A
076D 37           SCF
076E CD5605       CALL 0556,LD-BYTES
0771 DDE1         POP IX
0773 30F2         JR NC,0767,LD-LOOK-H
0775 3EFE         LD A,FE
0777 CD0116       CALL 1601,CHAN-OPEN
077A FD365203     LD (IY+52),03
077E 0E80         LD C,80
0780 DD7E00       LD A,(IX+00)
0783 DDBEEF       CP (IX-11)
0786 2002         JR NZ,078A,LD-TYPE
0788 0EF6         LD C,F6
078A FE04         CP 04
078C 30D9         JR NC,0767,LD-LOOK-H
078E 11C009       LD DE,09C0
0791 C5           PUSH BC
0792 CD0A0C       CALL 0C0A,PO-MSG
0795 C1           POP BC
0796 DDE5         PUSH IX
0798 D1           POP DE
0799 21F0FF       LD HL,FFF0
079C 19           ADD HL,DE
079D 060A         LD B,0A
079F 7E           LD A,(HL)
07A0 3C           INC A
07A1 2003         JR NZ,07A6,LD-NAME
07A3 79           LD A,C
07A4 80           ADD A,B
07A5 4F           LD C,A
07A6 13           INC DE
07A7 1A           LD A,(DE)
07A8 BE           CP (HL)
07A9 23           INC HL
07AA 2001         JR NZ,07AD,LD-CH-PR
07AC 0C           INC C
07AD D7           RST 10,PRINT-A-1
07AE 10F6         DJNZ 07A6,LD-NAME
07B0 CB79         BIT 7,C
07B2 20B3         JR NZ,0767,LD-LOOK-H
07B4 3E0D         LD A,0D
07B6 D7           RST 10,PRINT-A-1
07B7 E1           POP HL
07B8 DD7E00       LD A,(IX+00)
07BB FE03         CP 03
07BD 280C         JR Z,07CB,VR-CONTRL
07BF 3A745C       LD A,(5C74)  {T-ADDR}
07C2 3D           DEC A
07C3 CA0808       JP Z,0808,LD-CONTRL
07C6 FE02         CP 02
07C8 CAB608       JP Z,08B6,ME-CONTRL
07CB E5           PUSH HL
07CC DD6EFA       LD L,(IX-06)
07CF DD66FB       LD H,(IX-05)
07D2 DD5E0B       LD E,(IX+0B)
07D5 DD560C       LD D,(IX+0C)
07D8 7C           LD A,H
07D9 B5           OR L
07DA 280D         JR Z,07E9,VR-CONT-1
07DC ED52         SBC HL,DE
07DE 3826         JR C,0806,REPORT-R
07E0 2807         JR Z,07E9,VR-CONT-1
07E2 DD7E00       LD A,(IX+00)
07E5 FE03         CP 03
07E7 201D         JR NZ,0806,REPORT-R
07E9 E1           POP HL
07EA 7C           LD A,H
07EB B5           OR L
07EC 2006         JR NZ,07F4,VR-CONT-2
07EE DD6E0D       LD L,(IX+0D)
07F1 DD660E       LD H,(IX+0E)
07F4 E5           PUSH HL
07F5 DDE1         POP IX
07F7 3A745C       LD A,(5C74)  {T-ADDR}
07FA FE02         CP 02
07FC 37           SCF
07FD 2001         JR NZ,0800,VR-CONT-3
07FF A7           AND A
0800 3EFF         LD A,FF
0802 CD5605       CALL 0556,LD-BYTES
0805 D8           RET C
0806 CF           RST 08,ERROR-1
0807 1A           DEFB 1A        ;R Tape loading error
0808 DD5E0B       LD E,(IX+0B)
080B DD560C       LD D,(IX+0C)
080E E5           PUSH HL
080F 7C           LD A,H
0810 B5           OR L
0811 2006         JR NZ,0819,LD-CONT-1
0813 13           INC DE
0814 13           INC DE
0815 13           INC DE
0816 EB           EX DE,HL
0817 180C         JR 0825,LD-CONT-2
0819 DD6EFA       LD L,(IX-06)
081C DD66FB       LD H,(IX-05)
081F EB           EX DE,HL
0820 37           SCF
0821 ED52         SBC HL,DE
0823 3809         JR C,082E,LD-DATA
0825 110500       LD DE,0005
0828 19           ADD HL,DE
0829 44           LD B,H
082A 4D           LD C,L
082B CD051F       CALL 1F05,TEST-ROOM
082E E1           POP HL
082F DD7E00       LD A,(IX+00)
0832 A7           AND A
0833 283E         JR Z,0873,LD-PROG
0835 7C           LD A,H
0836 B5           OR L
0837 2813         JR Z,084C,LD-DATA-1
0839 2B           DEC HL
083A 46           LD B,(HL)
083B 2B           DEC HL
083C 4E           LD C,(HL)
083D 2B           DEC HL
083E 03           INC BC
083F 03           INC BC
0840 03           INC BC
0841 DD225F5C     LD (5C5F),IX  {X-PTR}
0845 CDE819       CALL 19E8,RECLAIM-2
0848 DD2A5F5C     LD IX,(5C5F)  {X-PTR}
084C 2A595C       LD HL,(5C59)  {E-LINE}
084F 2B           DEC HL
0850 DD4E0B       LD C,(IX+0B)
0853 DD460C       LD B,(IX+0C)
0856 C5           PUSH BC
0857 03           INC BC
0858 03           INC BC
0859 03           INC BC
085A DD7EFD       LD A,(IX-03)
085D F5           PUSH AF
085E CD5516       CALL 1655,MAKE-ROOM
0861 23           INC HL
0862 F1           POP AF
0863 77           LD (HL),A
0864 D1           POP DE
0865 23           INC HL
0866 73           LD (HL),E
0867 23           INC HL
0868 72           LD (HL),D
0869 23           INC HL
086A E5           PUSH HL
086B DDE1         POP IX
086D 37           SCF
086E 3EFF         LD A,FF
0870 C30208       JP 0802,LD-BLOCK
0873 EB           EX DE,HL
0874 2A595C       LD HL,(5C59)  {E-LINE}
0877 2B           DEC HL
0878 DD225F5C     LD (5C5F),IX  {X-PTR}
087C DD4E0B       LD C,(IX+0B)
087F DD460C       LD B,(IX+0C)
0882 C5           PUSH BC
0883 CDE519       CALL 19E5,RECLAIM-1
0886 C1           POP BC
0887 E5           PUSH HL
0888 C5           PUSH BC
0889 CD5516       CALL 1655,MAKE-ROOM
088C DD2A5F5C     LD IX,(5C5F)  {X-PTR}
0890 23           INC HL
0891 DD4E0F       LD C,(IX+0F)
0894 DD4610       LD B,(IX+10)
0897 09           ADD HL,BC
0898 224B5C       LD (5C4B),HL  {VARS}
089B DD660E       LD H,(IX+0E)
089E 7C           LD A,H
089F E6C0         AND C0
08A1 200A         JR NZ,08AD,LD-PROG-1
08A3 DD6E0D       LD L,(IX+0D)
08A6 22425C       LD (5C42),HL  {NEWPPC}
08A9 FD360A00     LD (IY+0A),00
08AD D1           POP DE
08AE DDE1         POP IX
08B0 37           SCF
08B1 3EFF         LD A,FF
08B3 C30208       JP 0802,LD-BLOCK
08B6 DD4E0B       LD C,(IX+0B)
08B9 DD460C       LD B,(IX+0C)
08BC C5           PUSH BC
08BD 03           INC BC
08BE F7           RST 30,BC-SPACES
08BF 3680         LD (HL),80
08C1 EB           EX DE,HL
08C2 D1           POP DE
08C3 E5           PUSH HL
08C4 E5           PUSH HL
08C5 DDE1         POP IX
08C7 37           SCF
08C8 3EFF         LD A,FF
08CA CD0208       CALL 0802,LD-BLOCK
08CD E1           POP HL
08CE ED5B535C     LD DE,(5C53)  {PROG}
08D2 7E           LD A,(HL)
08D3 E6C0         AND C0
08D5 2019         JR NZ,08F0,ME-VAR-LP
08D7 1A           LD A,(DE)
08D8 13           INC DE
08D9 BE           CP (HL)
08DA 23           INC HL
08DB 2002         JR NZ,08DF,ME-OLD-L1
08DD 1A           LD A,(DE)
08DE BE           CP (HL)
08DF 1B           DEC DE
08E0 2B           DEC HL
08E1 3008         JR NC,08EB,ME-NEW-L2
08E3 E5           PUSH HL
08E4 EB           EX DE,HL
08E5 CDB819       CALL 19B8,NEXT-ONE
08E8 E1           POP HL
08E9 18EC         JR 08D7,ME-OLD-LP
08EB CD2C09       CALL 092C,ME-ENTER
08EE 18E2         JR 08D2,ME-NEW-LP
08F0 7E           LD A,(HL)
08F1 4F           LD C,A
08F2 FE80         CP 80
08F4 C8           RET Z
08F5 E5           PUSH HL
08F6 2A4B5C       LD HL,(5C4B)  {VARS}
08F9 7E           LD A,(HL)
08FA FE80         CP 80
08FC 2825         JR Z,0923,ME-VAR-L2
08FE B9           CP C
08FF 2808         JR Z,0909,ME-OLD-V2
0901 C5           PUSH BC
0902 CDB819       CALL 19B8,NEXT-ONE
0905 C1           POP BC
0906 EB           EX DE,HL
0907 18F0         JR 08F9,ME-OLD-VP
0909 E6E0         AND E0
090B FEA0         CP A0
090D 2012         JR NZ,0921,ME-VAR-L1
090F D1           POP DE
0910 D5           PUSH DE
0911 E5           PUSH HL
0912 23           INC HL
0913 13           INC DE
0914 1A           LD A,(DE)
0915 BE           CP (HL)
0916 2006         JR NZ,091E,ME-OLD-V4
0918 17           RLA
0919 30F7         JR NC,0912,ME-OLD-V3
091B E1           POP HL
091C 1803         JR 0921,ME-VAR-L1
091E E1           POP HL
091F 18E0         JR 0901,ME-OLD-V1
0921 3EFF         LD A,FF
0923 D1           POP DE
0924 EB           EX DE,HL
0925 3C           INC A
0926 37           SCF
0927 CD2C09       CALL 092C,ME-ENTER
092A 18C4         JR 08F0,ME-VAR-LP
092C 2010         JR NZ,093E,ME-ENT-1
092E 08           EX AF,AF'
092F 225F5C       LD (5C5F),HL  {X-PTR}
0932 EB           EX DE,HL
0933 CDB819       CALL 19B8,NEXT-ONE
0936 CDE819       CALL 19E8,RECLAIM-2
0939 EB           EX DE,HL
093A 2A5F5C       LD HL,(5C5F)  {X-PTR}
093D 08           EX AF,AF'
093E 08           EX AF,AF'
093F D5           PUSH DE
0940 CDB819       CALL 19B8,NEXT-ONE
0943 225F5C       LD (5C5F),HL  {X-PTR}
0946 2A535C       LD HL,(5C53)  {PROG}
0949 E3           EX SP,(HL)
094A C5           PUSH BC
094B 08           EX AF,AF'
094C 3807         JR C,0955,ME-ENT-2
094E 2B           DEC HL
094F CD5516       CALL 1655,MAKE-ROOM
0952 23           INC HL
0953 1803         JR 0958,ME-ENT-3
0955 CD5516       CALL 1655,MAKE-ROOM
0958 23           INC HL
0959 C1           POP BC
095A D1           POP DE
095B ED53535C     LD (5C53),DE  {PROG}
095F ED5B5F5C     LD DE,(5C5F)  {X-PTR}
0963 C5           PUSH BC
0964 D5           PUSH DE
0965 EB           EX DE,HL
0966 EDB0         LDIR
0968 E1           POP HL
0969 C1           POP BC
096A D5           PUSH DE
096B CDE819       CALL 19E8,RECLAIM-2
096E D1           POP DE
096F C9           RET
0970 E5           PUSH HL
0971 3EFD         LD A,FD
0973 CD0116       CALL 1601,CHAN-OPEN
0976 AF           XOR A
0977 11A109       LD DE,09A1
097A CD0A0C       CALL 0C0A,PO-MSG
097D FDCB02EE     SET 5,(IY+02)
0981 CDD415       CALL 15D4,WAIT-KEY
0984 DDE5         PUSH IX
0986 111100       LD DE,0011
0989 AF           XOR A
098A CDC204       CALL 04C2,SA-BYTES
098D DDE1         POP IX
098F 0632         LD B,32
0991 76           HALT
0992 10FD         DJNZ 0991,SA-1-SEC
0994 DD5E0B       LD E,(IX+0B)
0997 DD560C       LD D,(IX+0C)
099A 3EFF         LD A,FF
099C DDE1         POP IX
099E C3C204       JP 04C2,SA-BYTES
09A1 80           ADD A,B
09A2 53           LD D,E
09A3 74           LD (HL),H
09A4 61           LD H,C
09A5 72           LD (HL),D
09A6 74           LD (HL),H
09A7 2074         JR NZ,0A1D
09A9 61           LD H,C
09AA 70           LD (HL),B
09AB 65           LD H,L
09AC 2C           INC L
09AD 2074         JR NZ,0A23,PO-BACK-1
09AF 68           LD L,B
09B0 65           LD H,L
09B1 6E           LD L,(HL)
09B2 2070         JR NZ,0A24
09B4 72           LD (HL),D
09B5 65           LD H,L
09B6 73           LD (HL),E
09B7 73           LD (HL),E
09B8 2061         JR NZ,0A1B
09BA 6E           LD L,(HL)
09BB 79           LD A,C
09BC 206B         JR NZ,0A29
09BE 65           LD H,L
09BF 79           LD A,C
09C0 AE           XOR (HL)
09C1 0D           DEC C
09C2 50           LD D,B
09C3 72           LD (HL),D
09C4 6F           LD L,A
09C5 67           LD H,A
09C6 72           LD (HL),D
09C7 61           LD H,C
09C8 6D           LD L,L
09C9 3AA00D       LD A,(0DA0)  {CL-CHAN-A}
09CC 4E           LD C,(HL)
09CD 75           LD (HL),L
09CE 6D           LD L,L
09CF 62           LD H,D
09D0 65           LD H,L
09D1 72           LD (HL),D
09D2 2061         JR NZ,0A35
09D4 72           LD (HL),D
09D5 72           LD (HL),D
09D6 61           LD H,C
09D7 79           LD A,C
09D8 3AA00D       LD A,(0DA0)  {CL-CHAN-A}
09DB 43           LD B,E
09DC 68           LD L,B
09DD 61           LD H,C
09DE 72           LD (HL),D
09DF 61           LD H,C
09E0 63           LD H,E
09E1 74           LD (HL),H
09E2 65           LD H,L
09E3 72           LD (HL),D
09E4 2061         JR NZ,0A47
09E6 72           LD (HL),D
09E7 72           LD (HL),D
09E8 61           LD H,C
09E9 79           LD A,C
09EA 3AA00D       LD A,(0DA0)  {CL-CHAN-A}
09ED 42           LD B,D
09EE 79           LD A,C
09EF 74           LD (HL),H
09F0 65           LD H,L
09F1 73           LD (HL),E
09F2 3AA0CD       LD A,(CDA0)
09F5 03           INC BC
09F6 0B           DEC BC
09F7 FE20         CP 20
09F9 D2D90A       JP NC,0AD9,PO-ABLE
09FC FE06         CP 06
09FE 3869         JR C,0A69,PO-QUEST
0A00 FE18         CP 18
0A02 3065         JR NC,0A69,PO-QUEST
0A04 210B0A       LD HL,0A0B
0A07 5F           LD E,A
0A08 1600         LD D,00
0A0A 19           ADD HL,DE
0A0B 5E           LD E,(HL)
0A0C 19           ADD HL,DE
0A0D E5           PUSH HL
0A0E C3030B       JP 0B03,PO-FETCH
0A11 4E           LD C,(HL)
0A12 57           LD D,A
0A13 1029         DJNZ 0A3E
0A15 54           LD D,H
0A16 53           LD D,E
0A17 52           LD D,D
0A18 37           SCF
0A19 50           LD D,B
0A1A 4F           LD C,A
0A1B 5F           LD E,A
0A1C 5E           LD E,(HL)
0A1D 5D           LD E,L
0A1E 5C           LD E,H
0A1F 5B           LD E,E
0A20 5A           LD E,D
0A21 54           LD D,H
0A22 53           LD D,E
0A23 0C           INC C
0A24 3E22         LD A,22
0A26 B9           CP C
0A27 2011         JR NZ,0A3A,PO-BACK-3
0A29 FDCB014E     BIT 1,(IY+01)
0A2D 2009         JR NZ,0A38,PO-BACK-2
0A2F 04           INC B
0A30 0E02         LD C,02
0A32 3E18         LD A,18
0A34 B8           CP B
0A35 2003         JR NZ,0A3A,PO-BACK-3
0A37 05           DEC B
0A38 0E21         LD C,21
0A3A C3D90D       JP 0DD9,CL-SET
0A3D 3A915C       LD A,(5C91)  {P-FLAG}
0A40 F5           PUSH AF
0A41 FD365701     LD (IY+57),01
0A45 3E20         LD A,20
0A47 CD650B       CALL 0B65,PO-CHAR
0A4A F1           POP AF
0A4B 32915C       LD (5C91),A  {P-FLAG}
0A4E C9           RET
0A4F FDCB014E     BIT 1,(IY+01)
0A53 C2CD0E       JP NZ,0ECD,COPY-BUFF
0A56 0E21         LD C,21
0A58 CD550C       CALL 0C55,PO-SCR
0A5B 05           DEC B
0A5C C3D90D       JP 0DD9,CL-SET
0A5F CD030B       CALL 0B03,PO-FETCH
0A62 79           LD A,C
0A63 3D           DEC A
0A64 3D           DEC A
0A65 E610         AND 10
0A67 185A         JR 0AC3,PO-FILL
0A69 3E3F         LD A,3F
0A6B 186C         JR 0AD9,PO-ABLE
0A6D 11870A       LD DE,0A87
0A70 320F5C       LD (5C0F),A
0A73 180B         JR 0A80,PO-CHANGE
0A75 116D0A       LD DE,0A6D
0A78 1803         JR 0A7D,PO-TV-1
0A7A 11870A       LD DE,0A87
0A7D 320E5C       LD (5C0E),A  {TVDATA}
0A80 2A515C       LD HL,(5C51)  {CURCHL}
0A83 73           LD (HL),E
0A84 23           INC HL
0A85 72           LD (HL),D
0A86 C9           RET
0A87 11F409       LD DE,09F4
0A8A CD800A       CALL 0A80,PO-CHANGE
0A8D 2A0E5C       LD HL,(5C0E)  {TVDATA}
0A90 57           LD D,A
0A91 7D           LD A,L
0A92 FE16         CP 16
0A94 DA1122       JP C,2211,CO-TEMP-5
0A97 2029         JR NZ,0AC2,PO-TAB
0A99 44           LD B,H
0A9A 4A           LD C,D
0A9B 3E1F         LD A,1F
0A9D 91           SUB C
0A9E 380C         JR C,0AAC,PO-AT-ERR
0AA0 C602         ADD A,02
0AA2 4F           LD C,A
0AA3 FDCB014E     BIT 1,(IY+01)
0AA7 2016         JR NZ,0ABF,PO-AT-SET
0AA9 3E16         LD A,16
0AAB 90           SUB B
0AAC DA9F1E       JP C,1E9F,REPORT-B
0AAF 3C           INC A
0AB0 47           LD B,A
0AB1 04           INC B
0AB2 FDCB0246     BIT 0,(IY+02)
0AB6 C2550C       JP NZ,0C55,PO-SCR
0AB9 FDBE31       CP (IY+31)
0ABC DA860C       JP C,0C86,REPORT-5
0ABF C3D90D       JP 0DD9,CL-SET
0AC2 7C           LD A,H
0AC3 CD030B       CALL 0B03,PO-FETCH
0AC6 81           ADD A,C
0AC7 3D           DEC A
0AC8 E61F         AND 1F
0ACA C8           RET Z
0ACB 57           LD D,A
0ACC FDCB01C6     SET 0,(IY+01)
0AD0 3E20         LD A,20
0AD2 CD3B0C       CALL 0C3B,PO-SAVE
0AD5 15           DEC D
0AD6 20F8         JR NZ,0AD0,PO-SPACE
0AD8 C9           RET
0AD9 CD240B       CALL 0B24,PO-ANY
0ADC FDCB014E     BIT 1,(IY+01)
0AE0 201A         JR NZ,0AFC,PO-ST-PR
0AE2 FDCB0246     BIT 0,(IY+02)
0AE6 2008         JR NZ,0AF0,PO-ST-E
0AE8 ED43885C     LD (5C88),BC  {S-POSN}
0AEC 22845C       LD (5C84),HL  {DF-CC}
0AEF C9           RET
0AF0 ED438A5C     LD (5C8A),BC  {SPOSNL}
0AF4 ED43825C     LD (5C82),BC  {ECHO-E}
0AF8 22865C       LD (5C86),HL  {DF-CCL}
0AFB C9           RET
0AFC FD7145       LD (IY+45),C
0AFF 22805C       LD (5C80),HL  {PR-CC}
0B02 C9           RET
0B03 FDCB014E     BIT 1,(IY+01)
0B07 2014         JR NZ,0B1D,PO-F-PR
0B09 ED4B885C     LD BC,(5C88)  {S-POSN}
0B0D 2A845C       LD HL,(5C84)  {DF-CC}
0B10 FDCB0246     BIT 0,(IY+02)
0B14 C8           RET Z
0B15 ED4B8A5C     LD BC,(5C8A)  {SPOSNL}
0B19 2A865C       LD HL,(5C86)  {DF-CCL}
0B1C C9           RET
0B1D FD4E45       LD C,(IY+45)
0B20 2A805C       LD HL,(5C80)  {PR-CC}
0B23 C9           RET
0B24 FE80         CP 80
0B26 383D         JR C,0B65,PO-CHAR
0B28 FE90         CP 90
0B2A 3026         JR NC,0B52,PO-T&UDG
0B2C 47           LD B,A
0B2D CD380B       CALL 0B38,PO-GR-1
0B30 CD030B       CALL 0B03,PO-FETCH
0B33 11925C       LD DE,5C92
0B36 1847         JR 0B7F,PR-ALL
0B38 21925C       LD HL,5C92
0B3B CD3E0B       CALL 0B3E,PO-GR-2
0B3E CB18         RR B
0B40 9F           SBC A,A
0B41 E60F         AND 0F
0B43 4F           LD C,A
0B44 CB18         RR B
0B46 9F           SBC A,A
0B47 E6F0         AND F0
0B49 B1           OR C
0B4A 0E04         LD C,04
0B4C 77           LD (HL),A
0B4D 23           INC HL
0B4E 0D           DEC C
0B4F 20FB         JR NZ,0B4C,PO-GR-3
0B51 C9           RET
0B52 D6A5         SUB A5
0B54 3009         JR NC,0B5F,PO-T
0B56 C615         ADD A,15
0B58 C5           PUSH BC
0B59 ED4B7B5C     LD BC,(5C7B)  {UDG}
0B5D 180B         JR 0B6A,PO-CHAR-2
0B5F CD100C       CALL 0C10,PO-TOKENS
0B62 C3030B       JP 0B03,PO-FETCH
0B65 C5           PUSH BC
0B66 ED4B365C     LD BC,(5C36)  {CHARS}
0B6A EB           EX DE,HL
0B6B 213B5C       LD HL,5C3B
0B6E CB86         RES 0,(HL)
0B70 FE20         CP 20
0B72 2002         JR NZ,0B76,PO-CHAR-3
0B74 CBC6         SET 0,(HL)
0B76 2600         LD H,00
0B78 6F           LD L,A
0B79 29           ADD HL,HL
0B7A 29           ADD HL,HL
0B7B 29           ADD HL,HL
0B7C 09           ADD HL,BC
0B7D C1           POP BC
0B7E EB           EX DE,HL
0B7F 79           LD A,C
0B80 3D           DEC A
0B81 3E21         LD A,21
0B83 200E         JR NZ,0B93,PR-ALL-1
0B85 05           DEC B
0B86 4F           LD C,A
0B87 FDCB014E     BIT 1,(IY+01)
0B8B 2806         JR Z,0B93,PR-ALL-1
0B8D D5           PUSH DE
0B8E CDCD0E       CALL 0ECD,COPY-BUFF
0B91 D1           POP DE
0B92 79           LD A,C
0B93 B9           CP C
0B94 D5           PUSH DE
0B95 CC550C       CALL Z,0C55,PO-SCR
0B98 D1           POP DE
0B99 C5           PUSH BC
0B9A E5           PUSH HL
0B9B 3A915C       LD A,(5C91)  {P-FLAG}
0B9E 06FF         LD B,FF
0BA0 1F           RRA
0BA1 3801         JR C,0BA4,PR-ALL-2
0BA3 04           INC B
0BA4 1F           RRA
0BA5 1F           RRA
0BA6 9F           SBC A,A
0BA7 4F           LD C,A
0BA8 3E08         LD A,08
0BAA A7           AND A
0BAB FDCB014E     BIT 1,(IY+01)
0BAF 2805         JR Z,0BB6,PR-ALL-3
0BB1 FDCB30CE     SET 1,(IY+30)
0BB5 37           SCF
0BB6 EB           EX DE,HL
0BB7 08           EX AF,AF'
0BB8 1A           LD A,(DE)
0BB9 A0           AND B
0BBA AE           XOR (HL)
0BBB A9           XOR C
0BBC 12           LD (DE),A
0BBD 08           EX AF,AF'
0BBE 3813         JR C,0BD3,PR-ALL-6
0BC0 14           INC D
0BC1 23           INC HL
0BC2 3D           DEC A
0BC3 20F2         JR NZ,0BB7,PR-ALL-4
0BC5 EB           EX DE,HL
0BC6 25           DEC H
0BC7 FDCB014E     BIT 1,(IY+01)
0BCB CCDB0B       CALL Z,0BDB,PO-ATTR
0BCE E1           POP HL
0BCF C1           POP BC
0BD0 0D           DEC C
0BD1 23           INC HL
0BD2 C9           RET
0BD3 08           EX AF,AF'
0BD4 3E20         LD A,20
0BD6 83           ADD A,E
0BD7 5F           LD E,A
0BD8 08           EX AF,AF'
0BD9 18E6         JR 0BC1,PR-ALL-5
0BDB 7C           LD A,H
0BDC 0F           RRCA
0BDD 0F           RRCA
0BDE 0F           RRCA
0BDF E603         AND 03
0BE1 F658         OR 58
0BE3 67           LD H,A
0BE4 ED5B8F5C     LD DE,(5C8F)  {ATTR-T}
0BE8 7E           LD A,(HL)
0BE9 AB           XOR E
0BEA A2           AND D
0BEB AB           XOR E
0BEC FDCB5776     BIT 6,(IY+57)
0BF0 2808         JR Z,0BFA,PO-ATTR-1
0BF2 E6C7         AND C7
0BF4 CB57         BIT 2,A
0BF6 2002         JR NZ,0BFA,PO-ATTR-1
0BF8 EE38         XOR 38
0BFA FDCB5766     BIT 4,(IY+57)
0BFE 2808         JR Z,0C08,PO-ATTR-2
0C00 E6F8         AND F8
0C02 CB6F         BIT 5,A
0C04 2002         JR NZ,0C08,PO-ATTR-2
0C06 EE07         XOR 07
0C08 77           LD (HL),A
0C09 C9           RET
0C0A E5           PUSH HL
0C0B 2600         LD H,00
0C0D E3           EX SP,(HL)
0C0E 1804         JR 0C14,PO-TABLE
0C10 119500       LD DE,0095
0C13 F5           PUSH AF
0C14 CD410C       CALL 0C41,PO-SEARCH
0C17 3809         JR C,0C22,PO-EACH
0C19 3E20         LD A,20
0C1B FDCB0146     BIT 0,(IY+01)
0C1F CC3B0C       CALL Z,0C3B,PO-SAVE
0C22 1A           LD A,(DE)
0C23 E67F         AND 7F
0C25 CD3B0C       CALL 0C3B,PO-SAVE
0C28 1A           LD A,(DE)
0C29 13           INC DE
0C2A 87           ADD A,A
0C2B 30F5         JR NC,0C22,PO-EACH
0C2D D1           POP DE
0C2E FE48         CP 48
0C30 2803         JR Z,0C35,PO-TR-SP
0C32 FE82         CP 82
0C34 D8           RET C
0C35 7A           LD A,D
0C36 FE03         CP 03
0C38 D8           RET C
0C39 3E20         LD A,20
0C3B D5           PUSH DE
0C3C D9           EXX
0C3D D7           RST 10,PRINT-A-1
0C3E D9           EXX
0C3F D1           POP DE
0C40 C9           RET
0C41 F5           PUSH AF
0C42 EB           EX DE,HL
0C43 3C           INC A
0C44 CB7E         BIT 7,(HL)
0C46 23           INC HL
0C47 28FB         JR Z,0C44,PO-STEP
0C49 3D           DEC A
0C4A 20F8         JR NZ,0C44,PO-STEP
0C4C EB           EX DE,HL
0C4D F1           POP AF
0C4E FE20         CP 20
0C50 D8           RET C
0C51 1A           LD A,(DE)
0C52 D641         SUB 41
0C54 C9           RET
0C55 FDCB014E     BIT 1,(IY+01)
0C59 C0           RET NZ
0C5A 11D90D       LD DE,0DD9
0C5D D5           PUSH DE
0C5E 78           LD A,B
0C5F FDCB0246     BIT 0,(IY+02)
0C63 C2020D       JP NZ,0D02,PO-SCR-4
0C66 FDBE31       CP (IY+31)
0C69 381B         JR C,0C86,REPORT-5
0C6B C0           RET NZ
0C6C FDCB0266     BIT 4,(IY+02)
0C70 2816         JR Z,0C88,PO-SCR-2
0C72 FD5E2D       LD E,(IY+2D)
0C75 1D           DEC E
0C76 285A         JR Z,0CD2,PO-SCR-3
0C78 3E00         LD A,00
0C7A CD0116       CALL 1601,CHAN-OPEN
0C7D ED7B3F5C     LD SP,(5C3F)  {LISTSP}
0C81 FDCB02A6     RES 4,(IY+02)
0C85 C9           RET
0C86 CF           RST 08,ERROR-1
0C87 04           DEFB 04        ;5 Out of screen
0C88 FD3552       DEC (IY+52)
0C8B 2045         JR NZ,0CD2,PO-SCR-3
0C8D 3E18         LD A,18
0C8F 90           SUB B
0C90 328C5C       LD (5C8C),A  {SCR-CT}
0C93 2A8F5C       LD HL,(5C8F)  {ATTR-T}
0C96 E5           PUSH HL
0C97 3A915C       LD A,(5C91)  {P-FLAG}
0C9A F5           PUSH AF
0C9B 3EFD         LD A,FD
0C9D CD0116       CALL 1601,CHAN-OPEN
0CA0 AF           XOR A
0CA1 11F80C       LD DE,0CF8
0CA4 CD0A0C       CALL 0C0A,PO-MSG
0CA7 FDCB02EE     SET 5,(IY+02)
0CAB 213B5C       LD HL,5C3B
0CAE CBDE         SET 3,(HL)
0CB0 CBAE         RES 5,(HL)
0CB2 D9           EXX
0CB3 CDD415       CALL 15D4,WAIT-KEY
0CB6 D9           EXX
0CB7 FE20         CP 20
0CB9 2845         JR Z,0D00,REPORT-D
0CBB FEE2         CP E2
0CBD 2841         JR Z,0D00,REPORT-D
0CBF F620         OR 20
0CC1 FE6E         CP 6E
0CC3 283B         JR Z,0D00,REPORT-D
0CC5 3EFE         LD A,FE
0CC7 CD0116       CALL 1601,CHAN-OPEN
0CCA F1           POP AF
0CCB 32915C       LD (5C91),A  {P-FLAG}
0CCE E1           POP HL
0CCF 228F5C       LD (5C8F),HL  {ATTR-T}
0CD2 CDFE0D       CALL 0DFE,CL-SC-ALL
0CD5 FD4631       LD B,(IY+31)
0CD8 04           INC B
0CD9 0E21         LD C,21
0CDB C5           PUSH BC
0CDC CD9B0E       CALL 0E9B,CL-ADDR
0CDF 7C           LD A,H
0CE0 0F           RRCA
0CE1 0F           RRCA
0CE2 0F           RRCA
0CE3 E603         AND 03
0CE5 F658         OR 58
0CE7 67           LD H,A
0CE8 11E05A       LD DE,5AE0
0CEB 1A           LD A,(DE)
0CEC 4E           LD C,(HL)
0CED 0620         LD B,20
0CEF EB           EX DE,HL
0CF0 12           LD (DE),A
0CF1 71           LD (HL),C
0CF2 13           INC DE
0CF3 23           INC HL
0CF4 10FA         DJNZ 0CF0,PO-SCR-3A
0CF6 C1           POP BC
0CF7 C9           RET
0CF8 80           ADD A,B
0CF9 73           LD (HL),E
0CFA 63           LD H,E
0CFB 72           LD (HL),D
0CFC 6F           LD L,A
0CFD 6C           LD L,H
0CFE 6C           LD L,H
0CFF BF           CP A
0D00 CF           RST 08,ERROR-1
0D01 0C           DEFB 0C        ;D BREAK - CONT repeats
0D02 FE02         CP 02
0D04 3880         JR C,0C86,REPORT-5
0D06 FD8631       ADD A,(IY+31)
0D09 D619         SUB 19
0D0B D0           RET NC
0D0C ED44         NEG
0D0E C5           PUSH BC
0D0F 47           LD B,A
0D10 2A8F5C       LD HL,(5C8F)  {ATTR-T}
0D13 E5           PUSH HL
0D14 2A915C       LD HL,(5C91)  {P-FLAG}
0D17 E5           PUSH HL
0D18 CD4D0D       CALL 0D4D,TEMPS
0D1B 78           LD A,B
0D1C F5           PUSH AF
0D1D 216B5C       LD HL,5C6B
0D20 46           LD B,(HL)
0D21 78           LD A,B
0D22 3C           INC A
0D23 77           LD (HL),A
0D24 21895C       LD HL,5C89
0D27 BE           CP (HL)
0D28 3803         JR C,0D2D,PO-SCR-4B
0D2A 34           INC (HL)
0D2B 0618         LD B,18
0D2D CD000E       CALL 0E00,CL-SCROLL
0D30 F1           POP AF
0D31 3D           DEC A
0D32 20E8         JR NZ,0D1C,PO-SCR-4A
0D34 E1           POP HL
0D35 FD7557       LD (IY+57),L
0D38 E1           POP HL
0D39 228F5C       LD (5C8F),HL  {ATTR-T}
0D3C ED4B885C     LD BC,(5C88)  {S-POSN}
0D40 FDCB0286     RES 0,(IY+02)
0D44 CDD90D       CALL 0DD9,CL-SET
0D47 FDCB02C6     SET 0,(IY+02)
0D4B C1           POP BC
0D4C C9           RET
0D4D AF           XOR A
0D4E 2A8D5C       LD HL,(5C8D)  {ATTR-P}
0D51 FDCB0246     BIT 0,(IY+02)
0D55 2804         JR Z,0D5B,TEMPS-1
0D57 67           LD H,A
0D58 FD6E0E       LD L,(IY+0E)
0D5B 228F5C       LD (5C8F),HL  {ATTR-T}
0D5E 21915C       LD HL,5C91
0D61 2002         JR NZ,0D65,TEMPS-2
0D63 7E           LD A,(HL)
0D64 0F           RRCA
0D65 AE           XOR (HL)
0D66 E655         AND 55
0D68 AE           XOR (HL)
0D69 77           LD (HL),A
0D6A C9           RET
0D6B CDAF0D       CALL 0DAF,CL-ALL
0D6E 213C5C       LD HL,5C3C
0D71 CBAE         RES 5,(HL)
0D73 CBC6         SET 0,(HL)
0D75 CD4D0D       CALL 0D4D,TEMPS
0D78 FD4631       LD B,(IY+31)
0D7B CD440E       CALL 0E44,CL-LINE
0D7E 21C05A       LD HL,5AC0
0D81 3A8D5C       LD A,(5C8D)  {ATTR-P}
0D84 05           DEC B
0D85 1807         JR 0D8E,CLS-3
0D87 0E20         LD C,20
0D89 2B           DEC HL
0D8A 77           LD (HL),A
0D8B 0D           DEC C
0D8C 20FB         JR NZ,0D89,CLS-2
0D8E 10F7         DJNZ 0D87,CLS-1
0D90 FD363102     LD (IY+31),02
0D94 3EFD         LD A,FD
0D96 CD0116       CALL 1601,CHAN-OPEN
0D99 2A515C       LD HL,(5C51)  {CURCHL}
0D9C 11F409       LD DE,09F4
0D9F A7           AND A
0DA0 73           LD (HL),E
0DA1 23           INC HL
0DA2 72           LD (HL),D
0DA3 23           INC HL
0DA4 11A810       LD DE,10A8
0DA7 3F           CCF
0DA8 38F6         JR C,0DA0,CL-CHAN-A
0DAA 012117       LD BC,1721
0DAD 182A         JR 0DD9,CL-SET
0DAF 210000       LD HL,0000
0DB2 227D5C       LD (5C7D),HL  {COORDS}
0DB5 FDCB3086     RES 0,(IY+30)
0DB9 CD940D       CALL 0D94,CL-CHAN
0DBC 3EFE         LD A,FE
0DBE CD0116       CALL 1601,CHAN-OPEN
0DC1 CD4D0D       CALL 0D4D,TEMPS
0DC4 0618         LD B,18
0DC6 CD440E       CALL 0E44,CL-LINE
0DC9 2A515C       LD HL,(5C51)  {CURCHL}
0DCC 11F409       LD DE,09F4
0DCF 73           LD (HL),E
0DD0 23           INC HL
0DD1 72           LD (HL),D
0DD2 FD365201     LD (IY+52),01
0DD6 012118       LD BC,1821
0DD9 21005B       LD HL,5B00
0DDC FDCB014E     BIT 1,(IY+01)
0DE0 2012         JR NZ,0DF4,CL-SET-2
0DE2 78           LD A,B
0DE3 FDCB0246     BIT 0,(IY+02)
0DE7 2805         JR Z,0DEE,CL-SET-1
0DE9 FD8631       ADD A,(IY+31)
0DEC D618         SUB 18
0DEE C5           PUSH BC
0DEF 47           LD B,A
0DF0 CD9B0E       CALL 0E9B,CL-ADDR
0DF3 C1           POP BC
0DF4 3E21         LD A,21
0DF6 91           SUB C
0DF7 5F           LD E,A
0DF8 1600         LD D,00
0DFA 19           ADD HL,DE
0DFB C3DC0A       JP 0ADC,PO-STORE
0DFE 0617         LD B,17
0E00 CD9B0E       CALL 0E9B,CL-ADDR
0E03 0E08         LD C,08
0E05 C5           PUSH BC
0E06 E5           PUSH HL
0E07 78           LD A,B
0E08 E607         AND 07
0E0A 78           LD A,B
0E0B 200C         JR NZ,0E19,CL-SCR-3
0E0D EB           EX DE,HL
0E0E 21E0F8       LD HL,F8E0
0E11 19           ADD HL,DE
0E12 EB           EX DE,HL
0E13 012000       LD BC,0020
0E16 3D           DEC A
0E17 EDB0         LDIR
0E19 EB           EX DE,HL
0E1A 21E0FF       LD HL,FFE0
0E1D 19           ADD HL,DE
0E1E EB           EX DE,HL
0E1F 47           LD B,A
0E20 E607         AND 07
0E22 0F           RRCA
0E23 0F           RRCA
0E24 0F           RRCA
0E25 4F           LD C,A
0E26 78           LD A,B
0E27 0600         LD B,00
0E29 EDB0         LDIR
0E2B 0607         LD B,07
0E2D 09           ADD HL,BC
0E2E E6F8         AND F8
0E30 20DB         JR NZ,0E0D,CL-SCR-2
0E32 E1           POP HL
0E33 24           INC H
0E34 C1           POP BC
0E35 0D           DEC C
0E36 20CD         JR NZ,0E05,CL-SCR-1
0E38 CD880E       CALL 0E88,CL-ATTR
0E3B 21E0FF       LD HL,FFE0
0E3E 19           ADD HL,DE
0E3F EB           EX DE,HL
0E40 EDB0         LDIR
0E42 0601         LD B,01
0E44 C5           PUSH BC
0E45 CD9B0E       CALL 0E9B,CL-ADDR
0E48 0E08         LD C,08
0E4A C5           PUSH BC
0E4B E5           PUSH HL
0E4C 78           LD A,B
0E4D E607         AND 07
0E4F 0F           RRCA
0E50 0F           RRCA
0E51 0F           RRCA
0E52 4F           LD C,A
0E53 78           LD A,B
0E54 0600         LD B,00
0E56 0D           DEC C
0E57 54           LD D,H
0E58 5D           LD E,L
0E59 3600         LD (HL),00
0E5B 13           INC DE
0E5C EDB0         LDIR
0E5E 110107       LD DE,0701
0E61 19           ADD HL,DE
0E62 3D           DEC A
0E63 E6F8         AND F8
0E65 47           LD B,A
0E66 20E5         JR NZ,0E4D,CL-LINE-2
0E68 E1           POP HL
0E69 24           INC H
0E6A C1           POP BC
0E6B 0D           DEC C
0E6C 20DC         JR NZ,0E4A,CL-LINE-1
0E6E CD880E       CALL 0E88,CL-ATTR
0E71 62           LD H,D
0E72 6B           LD L,E
0E73 13           INC DE
0E74 3A8D5C       LD A,(5C8D)  {ATTR-P}
0E77 FDCB0246     BIT 0,(IY+02)
0E7B 2803         JR Z,0E80,CL-LINE-3
0E7D 3A485C       LD A,(5C48)  {BORDCR}
0E80 77           LD (HL),A
0E81 0B           DEC BC
0E82 EDB0         LDIR
0E84 C1           POP BC
0E85 0E21         LD C,21
0E87 C9           RET
0E88 7C           LD A,H
0E89 0F           RRCA
0E8A 0F           RRCA
0E8B 0F           RRCA
0E8C 3D           DEC A
0E8D F650         OR 50
0E8F 67           LD H,A
0E90 EB           EX DE,HL
0E91 61           LD H,C
0E92 68           LD L,B
0E93 29           ADD HL,HL
0E94 29           ADD HL,HL
0E95 29           ADD HL,HL
0E96 29           ADD HL,HL
0E97 29           ADD HL,HL
0E98 44           LD B,H
0E99 4D           LD C,L
0E9A C9           RET
0E9B 3E18         LD A,18
0E9D 90           SUB B
0E9E 57           LD D,A
0E9F 0F           RRCA
0EA0 0F           RRCA
0EA1 0F           RRCA
0EA2 E6E0         AND E0
0EA4 6F           LD L,A
0EA5 7A           LD A,D
0EA6 E618         AND 18
0EA8 F640         OR 40
0EAA 67           LD H,A
0EAB C9           RET
0EAC F3           DI
0EAD 06B0         LD B,B0
0EAF 210040       LD HL,4000
0EB2 E5           PUSH HL
0EB3 C5           PUSH BC
0EB4 CDF40E       CALL 0EF4,COPY-LINE
0EB7 C1           POP BC
0EB8 E1           POP HL
0EB9 24           INC H
0EBA 7C           LD A,H
0EBB E607         AND 07
0EBD 200A         JR NZ,0EC9,COPY-2
0EBF 7D           LD A,L
0EC0 C620         ADD A,20
0EC2 6F           LD L,A
0EC3 3F           CCF
0EC4 9F           SBC A,A
0EC5 E6F8         AND F8
0EC7 84           ADD A,H
0EC8 67           LD H,A
0EC9 10E7         DJNZ 0EB2,COPY-1
0ECB 180D         JR 0EDA,COPY-END
0ECD F3           DI
0ECE 21005B       LD HL,5B00
0ED1 0608         LD B,08
0ED3 C5           PUSH BC
0ED4 CDF40E       CALL 0EF4,COPY-LINE
0ED7 C1           POP BC
0ED8 10F9         DJNZ 0ED3,COPY-3
0EDA 3E04         LD A,04
0EDC D3FB         OUT (FB),A
0EDE FB           EI
0EDF 21005B       LD HL,5B00
0EE2 FD7546       LD (IY+46),L
0EE5 AF           XOR A
0EE6 47           LD B,A
0EE7 77           LD (HL),A
0EE8 23           INC HL
0EE9 10FC         DJNZ 0EE7,PRB-BYTES
0EEB FDCB308E     RES 1,(IY+30)
0EEF 0E21         LD C,21
0EF1 C3D90D       JP 0DD9,CL-SET
0EF4 78           LD A,B
0EF5 FE03         CP 03
0EF7 9F           SBC A,A
0EF8 E602         AND 02
0EFA D3FB         OUT (FB),A
0EFC 57           LD D,A
0EFD CD541F       CALL 1F54,BREAK-KEY
0F00 380A         JR C,0F0C,COPY-L-2
0F02 3E04         LD A,04
0F04 D3FB         OUT (FB),A
0F06 FB           EI
0F07 CDDF0E       CALL 0EDF,CLEAR-PRB
0F0A CF           RST 08,ERROR-1
0F0B 0C           DEFB 0C        ;D BREAK - CONT repeats
0F0C DBFB         IN A,(FB)
0F0E 87           ADD A,A
0F0F F8           RET M
0F10 30EB         JR NC,0EFD,COPY-L-1
0F12 0E20         LD C,20
0F14 5E           LD E,(HL)
0F15 23           INC HL
0F16 0608         LD B,08
0F18 CB12         RL D
0F1A CB13         RL E
0F1C CB1A         RR D
0F1E DBFB         IN A,(FB)
0F20 1F           RRA
0F21 30FB         JR NC,0F1E,COPY-L-5
0F23 7A           LD A,D
0F24 D3FB         OUT (FB),A
0F26 10F0         DJNZ 0F18,COPY-L-4
0F28 0D           DEC C
0F29 20E9         JR NZ,0F14,COPY-L-3
0F2B C9           RET
0F2C 2A3D5C       LD HL,(5C3D)  {ERR-SP}
0F2F E5           PUSH HL
0F30 217F10       LD HL,107F
0F33 E5           PUSH HL
0F34 ED733D5C     LD (5C3D),SP  {ERR-SP}
0F38 CDD415       CALL 15D4,WAIT-KEY
0F3B F5           PUSH AF
0F3C 1600         LD D,00
0F3E FD5EFF       LD E,(IY-01)
0F41 21C800       LD HL,00C8
0F44 CDB503       CALL 03B5,BEEPER
0F47 F1           POP AF
0F48 21380F       LD HL,0F38
0F4B E5           PUSH HL
0F4C FE18         CP 18
0F4E 3031         JR NC,0F81,ADD-CHAR
0F50 FE07         CP 07
0F52 382D         JR C,0F81,ADD-CHAR
0F54 FE10         CP 10
0F56 383A         JR C,0F92,ED-KEYS
0F58 010200       LD BC,0002
0F5B 57           LD D,A
0F5C FE16         CP 16
0F5E 380C         JR C,0F6C,ED-CONTR
0F60 03           INC BC
0F61 FDCB377E     BIT 7,(IY+37)
0F65 CA1E10       JP Z,101E,ED-IGNORE
0F68 CDD415       CALL 15D4,WAIT-KEY
0F6B 5F           LD E,A
0F6C CDD415       CALL 15D4,WAIT-KEY
0F6F D5           PUSH DE
0F70 2A5B5C       LD HL,(5C5B)  {K-CUR}
0F73 FDCB0786     RES 0,(IY+07)
0F77 CD5516       CALL 1655,MAKE-ROOM
0F7A C1           POP BC
0F7B 23           INC HL
0F7C 70           LD (HL),B
0F7D 23           INC HL
0F7E 71           LD (HL),C
0F7F 180A         JR 0F8B,ADD-CH-1
0F81 FDCB0786     RES 0,(IY+07)
0F85 2A5B5C       LD HL,(5C5B)  {K-CUR}
0F88 CD5216       CALL 1652,ONE-SPACE
0F8B 12           LD (DE),A
0F8C 13           INC DE
0F8D ED535B5C     LD (5C5B),DE  {K-CUR}
0F91 C9           RET
0F92 5F           LD E,A
0F93 1600         LD D,00
0F95 21990F       LD HL,0F99
0F98 19           ADD HL,DE
0F99 5E           LD E,(HL)
0F9A 19           ADD HL,DE
0F9B E5           PUSH HL
0F9C 2A5B5C       LD HL,(5C5B)  {K-CUR}
0F9F C9           RET
0FA0 09           ADD HL,BC
0FA1 66           LD H,(HL)
0FA2 6A           LD L,D
0FA3 50           LD D,B
0FA4 B5           OR L
0FA5 70           LD (HL),B
0FA6 7E           LD A,(HL)
0FA7 CF           RST 08,ERROR-1
0FA8 D4           DEFB D4        ;unknown
0FA9 2A495C       LD HL,(5C49)  {E-PPC}
0FAC FDCB376E     BIT 5,(IY+37)
0FB0 C29710       JP NZ,1097,CLEAR-SP
0FB3 CD6E19       CALL 196E,LINE-ADDR
0FB6 CD9516       CALL 1695,LINE-NO
0FB9 7A           LD A,D
0FBA B3           OR E
0FBB CA9710       JP Z,1097,CLEAR-SP
0FBE E5           PUSH HL
0FBF 23           INC HL
0FC0 4E           LD C,(HL)
0FC1 23           INC HL
0FC2 46           LD B,(HL)
0FC3 210A00       LD HL,000A
0FC6 09           ADD HL,BC
0FC7 44           LD B,H
0FC8 4D           LD C,L
0FC9 CD051F       CALL 1F05,TEST-ROOM
0FCC CD9710       CALL 1097,CLEAR-SP
0FCF 2A515C       LD HL,(5C51)  {CURCHL}
0FD2 E3           EX SP,(HL)
0FD3 E5           PUSH HL
0FD4 3EFF         LD A,FF
0FD6 CD0116       CALL 1601,CHAN-OPEN
0FD9 E1           POP HL
0FDA 2B           DEC HL
0FDB FD350F       DEC (IY+0F)
0FDE CD5518       CALL 1855,OUT-LINE
0FE1 FD340F       INC (IY+0F)
0FE4 2A595C       LD HL,(5C59)  {E-LINE}
0FE7 23           INC HL
0FE8 23           INC HL
0FE9 23           INC HL
0FEA 23           INC HL
0FEB 225B5C       LD (5C5B),HL  {K-CUR}
0FEE E1           POP HL
0FEF CD1516       CALL 1615,CHAN-FLAG
0FF2 C9           RET
0FF3 FDCB376E     BIT 5,(IY+37)
0FF7 2008         JR NZ,1001,ED-STOP
0FF9 21495C       LD HL,5C49
0FFC CD0F19       CALL 190F,LN-FETCH
0FFF 186D         JR 106E,ED-LIST
1001 FD360010     LD (IY+00),10
1005 181D         JR 1024,ED-ENTER
1007 CD3110       CALL 1031,ED-EDGE
100A 1805         JR 1011,ED-CUR
100C 7E           LD A,(HL)
100D FE0D         CP 0D
100F C8           RET Z
1010 23           INC HL
1011 225B5C       LD (5C5B),HL  {K-CUR}
1014 C9           RET
1015 CD3110       CALL 1031,ED-EDGE
1018 010100       LD BC,0001
101B C3E819       JP 19E8,RECLAIM-2
101E CDD415       CALL 15D4,WAIT-KEY
1021 CDD415       CALL 15D4,WAIT-KEY
1024 E1           POP HL
1025 E1           POP HL
1026 E1           POP HL
1027 223D5C       LD (5C3D),HL  {ERR-SP}
102A FDCB007E     BIT 7,(IY+00)
102E C0           RET NZ
102F F9           LD SP,HL
1030 C9           RET
1031 37           SCF
1032 CD9511       CALL 1195,SET-DE
1035 ED52         SBC HL,DE
1037 19           ADD HL,DE
1038 23           INC HL
1039 C1           POP BC
103A D8           RET C
103B C5           PUSH BC
103C 44           LD B,H
103D 4D           LD C,L
103E 62           LD H,D
103F 6B           LD L,E
1040 23           INC HL
1041 1A           LD A,(DE)
1042 E6F0         AND F0
1044 FE10         CP 10
1046 2009         JR NZ,1051,ED-EDGE-2
1048 23           INC HL
1049 1A           LD A,(DE)
104A D617         SUB 17
104C CE00         ADC A,00
104E 2001         JR NZ,1051,ED-EDGE-2
1050 23           INC HL
1051 A7           AND A
1052 ED42         SBC HL,BC
1054 09           ADD HL,BC
1055 EB           EX DE,HL
1056 38E6         JR C,103E,ED-EDGE-1
1058 C9           RET
1059 FDCB376E     BIT 5,(IY+37)
105D C0           RET NZ
105E 2A495C       LD HL,(5C49)  {E-PPC}
1061 CD6E19       CALL 196E,LINE-ADDR
1064 EB           EX DE,HL
1065 CD9516       CALL 1695,LINE-NO
1068 214A5C       LD HL,5C4A
106B CD1C19       CALL 191C,LN-STORE
106E CD9517       CALL 1795,AUTO-LIST
1071 3E00         LD A,00
1073 C30116       JP 1601,CHAN-OPEN
1076 FDCB377E     BIT 7,(IY+37)
107A 28A8         JR Z,1024,ED-ENTER
107C C3810F       JP 0F81,ADD-CHAR
107F FDCB3066     BIT 4,(IY+30)
1083 28A1         JR Z,1026,ED-END
1085 FD3600FF     LD (IY+00),FF
1089 1600         LD D,00
108B FD5EFE       LD E,(IY-02)
108E 21901A       LD HL,1A90
1091 CDB503       CALL 03B5,BEEPER
1094 C3300F       JP 0F30,ED-AGAIN
1097 E5           PUSH HL
1098 CD9011       CALL 1190,SET-HL
109B 2B           DEC HL
109C CDE519       CALL 19E5,RECLAIM-1
109F 225B5C       LD (5C5B),HL  {K-CUR}
10A2 FD360700     LD (IY+07),00
10A6 E1           POP HL
10A7 C9           RET
10A8 FDCB025E     BIT 3,(IY+02)
10AC C41D11       CALL NZ,111D,ED-COPY
10AF A7           AND A
10B0 FDCB016E     BIT 5,(IY+01)
10B4 C8           RET Z
10B5 3A085C       LD A,(5C08)  {LAST-K}
10B8 FDCB01AE     RES 5,(IY+01)
10BC F5           PUSH AF
10BD FDCB026E     BIT 5,(IY+02)
10C1 C46E0D       CALL NZ,0D6E,CLS-LOWER
10C4 F1           POP AF
10C5 FE20         CP 20
10C7 3052         JR NC,111B,KEY-DONE
10C9 FE10         CP 10
10CB 302D         JR NC,10FA,KEY-CONTR
10CD FE06         CP 06
10CF 300A         JR NC,10DB,KEY-M&CL
10D1 47           LD B,A
10D2 E601         AND 01
10D4 4F           LD C,A
10D5 78           LD A,B
10D6 1F           RRA
10D7 C612         ADD A,12
10D9 182A         JR 1105,KEY-DATA
10DB 2009         JR NZ,10E6,KEY-MODE
10DD 216A5C       LD HL,5C6A
10E0 3E08         LD A,08
10E2 AE           XOR (HL)
10E3 77           LD (HL),A
10E4 180E         JR 10F4,KEY-FLAG
10E6 FE0E         CP 0E
10E8 D8           RET C
10E9 D60D         SUB 0D
10EB 21415C       LD HL,5C41
10EE BE           CP (HL)
10EF 77           LD (HL),A
10F0 2002         JR NZ,10F4,KEY-FLAG
10F2 3600         LD (HL),00
10F4 FDCB02DE     SET 3,(IY+02)
10F8 BF           CP A
10F9 C9           RET
10FA 47           LD B,A
10FB E607         AND 07
10FD 4F           LD C,A
10FE 3E10         LD A,10
1100 CB58         BIT 3,B
1102 2001         JR NZ,1105,KEY-DATA
1104 3C           INC A
1105 FD71D3       LD (IY-2D),C
1108 110D11       LD DE,110D
110B 1806         JR 1113,KEY-CHAN
110D 3A0D5C       LD A,(5C0D)  {K-DATA}
1110 11A810       LD DE,10A8
1113 2A4F5C       LD HL,(5C4F)  {CHANS}
1116 23           INC HL
1117 23           INC HL
1118 73           LD (HL),E
1119 23           INC HL
111A 72           LD (HL),D
111B 37           SCF
111C C9           RET
111D CD4D0D       CALL 0D4D,TEMPS
1120 FDCB029E     RES 3,(IY+02)
1124 FDCB02AE     RES 5,(IY+02)
1128 2A8A5C       LD HL,(5C8A)  {SPOSNL}
112B E5           PUSH HL
112C 2A3D5C       LD HL,(5C3D)  {ERR-SP}
112F E5           PUSH HL
1130 216711       LD HL,1167
1133 E5           PUSH HL
1134 ED733D5C     LD (5C3D),SP  {ERR-SP}
1138 2A825C       LD HL,(5C82)  {ECHO-E}
113B E5           PUSH HL
113C 37           SCF
113D CD9511       CALL 1195,SET-DE
1140 EB           EX DE,HL
1141 CD7D18       CALL 187D,OUT-LINE2
1144 EB           EX DE,HL
1145 CDE118       CALL 18E1,OUT-CURS
1148 2A8A5C       LD HL,(5C8A)  {SPOSNL}
114B E3           EX SP,(HL)
114C EB           EX DE,HL
114D CD4D0D       CALL 0D4D,TEMPS
1150 3A8B5C       LD A,(5C8B)
1153 92           SUB D
1154 3826         JR C,117C,ED-C-DONE
1156 2006         JR NZ,115E,ED-SPACES
1158 7B           LD A,E
1159 FD9650       SUB (IY+50)
115C 301E         JR NC,117C,ED-C-DONE
115E 3E20         LD A,20
1160 D5           PUSH DE
1161 CDF409       CALL 09F4,PRINT-OUT
1164 D1           POP DE
1165 18E9         JR 1150,ED-BLANK
1167 1600         LD D,00
1169 FD5EFE       LD E,(IY-02)
116C 21901A       LD HL,1A90
116F CDB503       CALL 03B5,BEEPER
1172 FD3600FF     LD (IY+00),FF
1176 ED5B8A5C     LD DE,(5C8A)  {SPOSNL}
117A 1802         JR 117E,ED-C-END
117C D1           POP DE
117D E1           POP HL
117E E1           POP HL
117F 223D5C       LD (5C3D),HL  {ERR-SP}
1182 C1           POP BC
1183 D5           PUSH DE
1184 CDD90D       CALL 0DD9,CL-SET
1187 E1           POP HL
1188 22825C       LD (5C82),HL  {ECHO-E}
118B FD362600     LD (IY+26),00
118F C9           RET
1190 2A615C       LD HL,(5C61)  {WORKSP}
1193 2B           DEC HL
1194 A7           AND A
1195 ED5B595C     LD DE,(5C59)  {E-LINE}
1199 FDCB376E     BIT 5,(IY+37)
119D C8           RET Z
119E ED5B615C     LD DE,(5C61)  {WORKSP}
11A2 D8           RET C
11A3 2A635C       LD HL,(5C63)  {STKBOT}
11A6 C9           RET
11A7 7E           LD A,(HL)
11A8 FE0E         CP 0E
11AA 010600       LD BC,0006
11AD CCE819       CALL Z,19E8,RECLAIM-2
11B0 7E           LD A,(HL)
11B1 23           INC HL
11B2 FE0D         CP 0D
11B4 20F1         JR NZ,11A7,REMOVE-FP
11B6 C9           RET
11B7 F3           DI
11B8 3EFF         LD A,FF
11BA ED5BB25C     LD DE,(5CB2)  {RAMTOP}
11BE D9           EXX
11BF ED4BB45C     LD BC,(5CB4)  {P-RAMT}
11C3 ED5B385C     LD DE,(5C38)  {RASP}
11C7 2A7B5C       LD HL,(5C7B)  {UDG}
11CA D9           EXX
11CB 47           LD B,A
11CC 3E07         LD A,07
11CE D3FE         OUT (FE),A
11D0 3E3F         LD A,3F
11D2 ED47         LD I,A
11D4 00           NOP
11D5 00           NOP
11D6 00           NOP
11D7 00           NOP
11D8 00           NOP
11D9 00           NOP
11DA 62           LD H,D
11DB 6B           LD L,E
11DC 3602         LD (HL),02
11DE 2B           DEC HL
11DF BC           CP H
11E0 20FA         JR NZ,11DC,RAM-FILL
11E2 A7           AND A
11E3 ED52         SBC HL,DE
11E5 19           ADD HL,DE
11E6 23           INC HL
11E7 3006         JR NC,11EF,RAM-DONE
11E9 35           DEC (HL)
11EA 2803         JR Z,11EF,RAM-DONE
11EC 35           DEC (HL)
11ED 28F3         JR Z,11E2,RAM-READ
11EF 2B           DEC HL
11F0 D9           EXX
11F1 ED43B45C     LD (5CB4),BC  {P-RAMT}
11F5 ED53385C     LD (5C38),DE  {RASP}
11F9 227B5C       LD (5C7B),HL  {UDG}
11FC D9           EXX
11FD 04           INC B
11FE 2819         JR Z,1219,RAM-SET
1200 22B45C       LD (5CB4),HL  {P-RAMT}
1203 11AF3E       LD DE,3EAF
1206 01A800       LD BC,00A8
1209 EB           EX DE,HL
120A EDB8         LDDR
120C EB           EX DE,HL
120D 23           INC HL
120E 227B5C       LD (5C7B),HL  {UDG}
1211 2B           DEC HL
1212 014000       LD BC,0040
1215 ED43385C     LD (5C38),BC  {RASP}
1219 22B25C       LD (5CB2),HL  {RAMTOP}
121C 21003C       LD HL,3C00
121F 22365C       LD (5C36),HL  {CHARS}
1222 2AB25C       LD HL,(5CB2)  {RAMTOP}
1225 363E         LD (HL),3E
1227 2B           DEC HL
1228 F9           LD SP,HL
1229 2B           DEC HL
122A 2B           DEC HL
122B 223D5C       LD (5C3D),HL  {ERR-SP}
122E ED56         IM 1
1230 FD213A5C     LD IY,5C3A
1234 FB           EI
1235 21B65C       LD HL,5CB6
1238 224F5C       LD (5C4F),HL  {CHANS}
123B 11AF15       LD DE,15AF
123E 011500       LD BC,0015
1241 EB           EX DE,HL
1242 EDB0         LDIR
1244 EB           EX DE,HL
1245 2B           DEC HL
1246 22575C       LD (5C57),HL  {DATADD}
1249 23           INC HL
124A 22535C       LD (5C53),HL  {PROG}
124D 224B5C       LD (5C4B),HL  {VARS}
1250 3680         LD (HL),80
1252 23           INC HL
1253 22595C       LD (5C59),HL  {E-LINE}
1256 360D         LD (HL),0D
1258 23           INC HL
1259 3680         LD (HL),80
125B 23           INC HL
125C 22615C       LD (5C61),HL  {WORKSP}
125F 22635C       LD (5C63),HL  {STKBOT}
1262 22655C       LD (5C65),HL  {STKEND}
1265 3E38         LD A,38
1267 328D5C       LD (5C8D),A  {ATTR-P}
126A 328F5C       LD (5C8F),A  {ATTR-T}
126D 32485C       LD (5C48),A  {BORDCR}
1270 212305       LD HL,0523
1273 22095C       LD (5C09),HL  {REPDEL}
1276 FD35C6       DEC (IY-3A)
1279 FD35CA       DEC (IY-36)
127C 21C615       LD HL,15C6
127F 11105C       LD DE,5C10
1282 010E00       LD BC,000E
1285 EDB0         LDIR
1287 FDCB01CE     SET 1,(IY+01)
128B CDDF0E       CALL 0EDF,CLEAR-PRB
128E FD363102     LD (IY+31),02
1292 CD6B0D       CALL 0D6B,CLS
1295 AF           XOR A
1296 113815       LD DE,1538
1299 CD0A0C       CALL 0C0A,PO-MSG
129C FDCB02EE     SET 5,(IY+02)
12A0 1807         JR 12A9,MAIN-1
12A2 FD363102     LD (IY+31),02
12A6 CD9517       CALL 1795,AUTO-LIST
12A9 CDB016       CALL 16B0,SET-MIN
12AC 3E00         LD A,00
12AE CD0116       CALL 1601,CHAN-OPEN
12B1 CD2C0F       CALL 0F2C,EDITOR
12B4 CD171B       CALL 1B17,LINE-SCAN
12B7 FDCB007E     BIT 7,(IY+00)
12BB 2012         JR NZ,12CF,MAIN-3
12BD FDCB3066     BIT 4,(IY+30)
12C1 2840         JR Z,1303,MAIN-4
12C3 2A595C       LD HL,(5C59)  {E-LINE}
12C6 CDA711       CALL 11A7,REMOVE-FP
12C9 FD3600FF     LD (IY+00),FF
12CD 18DD         JR 12AC,MAIN-2
12CF 2A595C       LD HL,(5C59)  {E-LINE}
12D2 225D5C       LD (5C5D),HL  {CH-ADD}
12D5 CDFB19       CALL 19FB,E-LINE-NO
12D8 78           LD A,B
12D9 B1           OR C
12DA C25D15       JP NZ,155D,MAIN-ADD
12DD DF           RST 18,GET-CHAR
12DE FE0D         CP 0D
12E0 28C0         JR Z,12A2,MAIN-EXEC
12E2 FDCB3046     BIT 0,(IY+30)
12E6 C4AF0D       CALL NZ,0DAF,CL-ALL
12E9 CD6E0D       CALL 0D6E,CLS-LOWER
12EC 3E19         LD A,19
12EE FD964F       SUB (IY+4F)
12F1 328C5C       LD (5C8C),A  {SCR-CT}
12F4 FDCB01FE     SET 7,(IY+01)
12F8 FD3600FF     LD (IY+00),FF
12FC FD360A01     LD (IY+0A),01
1300 CD8A1B       CALL 1B8A,LINE-RUN
1303 76           HALT
1304 FDCB01AE     RES 5,(IY+01)
1308 FDCB304E     BIT 1,(IY+30)
130C C4CD0E       CALL NZ,0ECD,COPY-BUFF
130F 3A3A5C       LD A,(5C3A)  {ERR-NR}
1312 3C           INC A
1313 F5           PUSH AF
1314 210000       LD HL,0000
1317 FD7437       LD (IY+37),H
131A FD7426       LD (IY+26),H
131D 220B5C       LD (5C0B),HL  {DEFADD}
1320 210100       LD HL,0001
1323 22165C       LD (5C16),HL
1326 CDB016       CALL 16B0,SET-MIN
1329 FDCB37AE     RES 5,(IY+37)
132D CD6E0D       CALL 0D6E,CLS-LOWER
1330 FDCB02EE     SET 5,(IY+02)
1334 F1           POP AF
1335 47           LD B,A
1336 FE0A         CP 0A
1338 3802         JR C,133C,MAIN-5
133A C607         ADD A,07
133C CDEF15       CALL 15EF,OUT-CODE
133F 3E20         LD A,20
1341 D7           RST 10,PRINT-A-1
1342 78           LD A,B
1343 119113       LD DE,1391
1346 CD0A0C       CALL 0C0A,PO-MSG
1349 AF           XOR A
134A 113615       LD DE,1536
134D CD0A0C       CALL 0C0A,PO-MSG
1350 ED4B455C     LD BC,(5C45)  {PPC}
1354 CD1B1A       CALL 1A1B,OUT-NUM-1
1357 3E3A         LD A,3A
1359 D7           RST 10,PRINT-A-1
135A FD4E0D       LD C,(IY+0D)
135D 0600         LD B,00
135F CD1B1A       CALL 1A1B,OUT-NUM-1
1362 CD9710       CALL 1097,CLEAR-SP
1365 3A3A5C       LD A,(5C3A)  {ERR-NR}
1368 3C           INC A
1369 281B         JR Z,1386,MAIN-9
136B FE09         CP 09
136D 2804         JR Z,1373,MAIN-6
136F FE15         CP 15
1371 2003         JR NZ,1376,MAIN-7
1373 FD340D       INC (IY+0D)
1376 010300       LD BC,0003
1379 11705C       LD DE,5C70
137C 21445C       LD HL,5C44
137F CB7E         BIT 7,(HL)
1381 2801         JR Z,1384,MAIN-8
1383 09           ADD HL,BC
1384 EDB8         LDDR
1386 FD360AFF     LD (IY+0A),FF
138A FDCB019E     RES 3,(IY+01)
138E C3AC12       JP 12AC,MAIN-2
1391 80           ADD A,B
1392 4F           LD C,A
1393 CB4E         BIT 1,(HL)
1395 45           LD B,L
1396 58           LD E,B
1397 54           LD D,H
1398 2077         JR NZ,1411
139A 69           LD L,C
139B 74           LD (HL),H
139C 68           LD L,B
139D 6F           LD L,A
139E 75           LD (HL),L
139F 74           LD (HL),H
13A0 2046         JR NZ,13E8
13A2 4F           LD C,A
13A3 D25661       JP NC,6156
13A6 72           LD (HL),D
13A7 69           LD L,C
13A8 61           LD H,C
13A9 62           LD H,D
13AA 6C           LD L,H
13AB 65           LD H,L
13AC 206E         JR NZ,141C
13AE 6F           LD L,A
13AF 74           LD (HL),H
13B0 2066         JR NZ,1418
13B2 6F           LD L,A
13B3 75           LD (HL),L
13B4 6E           LD L,(HL)
13B5 E45375       CALL PO,7553
13B8 62           LD H,D
13B9 73           LD (HL),E
13BA 63           LD H,E
13BB 72           LD (HL),D
13BC 69           LD L,C
13BD 70           LD (HL),B
13BE 74           LD (HL),H
13BF 2077         JR NZ,1438
13C1 72           LD (HL),D
13C2 6F           LD L,A
13C3 6E           LD L,(HL)
13C4 E7           RST 20,NEXT-CHAR
13C5 4F           LD C,A
13C6 75           LD (HL),L
13C7 74           LD (HL),H
13C8 206F         JR NZ,1439
13CA 66           LD H,(HL)
13CB 206D         JR NZ,143A
13CD 65           LD H,L
13CE 6D           LD L,L
13CF 6F           LD L,A
13D0 72           LD (HL),D
13D1 F9           LD SP,HL
13D2 4F           LD C,A
13D3 75           LD (HL),L
13D4 74           LD (HL),H
13D5 206F         JR NZ,1446
13D7 66           LD H,(HL)
13D8 2073         JR NZ,144D
13DA 63           LD H,E
13DB 72           LD (HL),D
13DC 65           LD H,L
13DD 65           LD H,L
13DE EE4E         XOR 4E
13E0 75           LD (HL),L
13E1 6D           LD L,L
13E2 62           LD H,D
13E3 65           LD H,L
13E4 72           LD (HL),D
13E5 2074         JR NZ,145B
13E7 6F           LD L,A
13E8 6F           LD L,A
13E9 2062         JR NZ,144D
13EB 69           LD L,C
13EC E7           RST 20,NEXT-CHAR
13ED 52           LD D,D
13EE 45           LD B,L
13EF 54           LD D,H
13F0 55           LD D,L
13F1 52           LD D,D
13F2 4E           LD C,(HL)
13F3 2077         JR NZ,146C
13F5 69           LD L,C
13F6 74           LD (HL),H
13F7 68           LD L,B
13F8 6F           LD L,A
13F9 75           LD (HL),L
13FA 74           LD (HL),H
13FB 2047         JR NZ,1444
13FD 4F           LD C,A
13FE 53           LD D,E
13FF 55           LD D,L
1400 C2456E       JP NZ,6E45
1403 64           LD H,H
1404 206F         JR NZ,1475
1406 66           LD H,(HL)
1407 2066         JR NZ,146F
1409 69           LD L,C
140A 6C           LD L,H
140B E5           PUSH HL
140C 53           LD D,E
140D 54           LD D,H
140E 4F           LD C,A
140F 50           LD D,B
1410 2073         JR NZ,1485
1412 74           LD (HL),H
1413 61           LD H,C
1414 74           LD (HL),H
1415 65           LD H,L
1416 6D           LD L,L
1417 65           LD H,L
1418 6E           LD L,(HL)
1419 F4496E       CALL P,6E49
141C 76           HALT
141D 61           LD H,C
141E 6C           LD L,H
141F 69           LD L,C
1420 64           LD H,H
1421 2061         JR NZ,1484
1423 72           LD (HL),D
1424 67           LD H,A
1425 75           LD (HL),L
1426 6D           LD L,L
1427 65           LD H,L
1428 6E           LD L,(HL)
1429 F4496E       CALL P,6E49
142C 74           LD (HL),H
142D 65           LD H,L
142E 67           LD H,A
142F 65           LD H,L
1430 72           LD (HL),D
1431 206F         JR NZ,14A2
1433 75           LD (HL),L
1434 74           LD (HL),H
1435 206F         JR NZ,14A6
1437 66           LD H,(HL)
1438 2072         JR NZ,14AC
143A 61           LD H,C
143B 6E           LD L,(HL)
143C 67           LD H,A
143D E5           PUSH HL
143E 4E           LD C,(HL)
143F 6F           LD L,A
1440 6E           LD L,(HL)
1441 73           LD (HL),E
1442 65           LD H,L
1443 6E           LD L,(HL)
1444 73           LD (HL),E
1445 65           LD H,L
1446 2069         JR NZ,14B1
1448 6E           LD L,(HL)
1449 2042         JR NZ,148D
144B 41           LD B,C
144C 53           LD D,E
144D 49           LD C,C
144E C34252       JP 5242
1451 45           LD B,L
1452 41           LD B,C
1453 4B           LD C,E
1454 202D         JR NZ,1483
1456 2043         JR NZ,149B
1458 4F           LD C,A
1459 4E           LD C,(HL)
145A 54           LD D,H
145B 2072         JR NZ,14CF
145D 65           LD H,L
145E 70           LD (HL),B
145F 65           LD H,L
1460 61           LD H,C
1461 74           LD (HL),H
1462 F3           DI
1463 4F           LD C,A
1464 75           LD (HL),L
1465 74           LD (HL),H
1466 206F         JR NZ,14D7
1468 66           LD H,(HL)
1469 2044         JR NZ,14AF
146B 41           LD B,C
146C 54           LD D,H
146D C1           POP BC
146E 49           LD C,C
146F 6E           LD L,(HL)
1470 76           HALT
1471 61           LD H,C
1472 6C           LD L,H
1473 69           LD L,C
1474 64           LD H,H
1475 2066         JR NZ,14DD
1477 69           LD L,C
1478 6C           LD L,H
1479 65           LD H,L
147A 206E         JR NZ,14EA
147C 61           LD H,C
147D 6D           LD L,L
147E E5           PUSH HL
147F 4E           LD C,(HL)
1480 6F           LD L,A
1481 2072         JR NZ,14F5
1483 6F           LD L,A
1484 6F           LD L,A
1485 6D           LD L,L
1486 2066         JR NZ,14EE
1488 6F           LD L,A
1489 72           LD (HL),D
148A 206C         JR NZ,14F8
148C 69           LD L,C
148D 6E           LD L,(HL)
148E E5           PUSH HL
148F 53           LD D,E
1490 54           LD D,H
1491 4F           LD C,A
1492 50           LD D,B
1493 2069         JR NZ,14FE
1495 6E           LD L,(HL)
1496 2049         JR NZ,14E1
1498 4E           LD C,(HL)
1499 50           LD D,B
149A 55           LD D,L
149B D4464F       CALL NC,4F46
149E 52           LD D,D
149F 2077         JR NZ,1518
14A1 69           LD L,C
14A2 74           LD (HL),H
14A3 68           LD L,B
14A4 6F           LD L,A
14A5 75           LD (HL),L
14A6 74           LD (HL),H
14A7 204E         JR NZ,14F7
14A9 45           LD B,L
14AA 58           LD E,B
14AB D4496E       CALL NC,6E49
14AE 76           HALT
14AF 61           LD H,C
14B0 6C           LD L,H
14B1 69           LD L,C
14B2 64           LD H,H
14B3 2049         JR NZ,14FE
14B5 2F           CPL
14B6 4F           LD C,A
14B7 2064         JR NZ,151D
14B9 65           LD H,L
14BA 76           HALT
14BB 69           LD L,C
14BC 63           LD H,E
14BD E5           PUSH HL
14BE 49           LD C,C
14BF 6E           LD L,(HL)
14C0 76           HALT
14C1 61           LD H,C
14C2 6C           LD L,H
14C3 69           LD L,C
14C4 64           LD H,H
14C5 2063         JR NZ,152A
14C7 6F           LD L,A
14C8 6C           LD L,H
14C9 6F           LD L,A
14CA 75           LD (HL),L
14CB F24252       JP P,5242
14CE 45           LD B,L
14CF 41           LD B,C
14D0 4B           LD C,E
14D1 2069         JR NZ,153C
14D3 6E           LD L,(HL)
14D4 74           LD (HL),H
14D5 6F           LD L,A
14D6 2070         JR NZ,1548
14D8 72           LD (HL),D
14D9 6F           LD L,A
14DA 67           LD H,A
14DB 72           LD (HL),D
14DC 61           LD H,C
14DD ED52         SBC HL,DE
14DF 41           LD B,C
14E0 4D           LD C,L
14E1 54           LD D,H
14E2 4F           LD C,A
14E3 50           LD D,B
14E4 206E         JR NZ,1554
14E6 6F           LD L,A
14E7 2067         JR NZ,1550
14E9 6F           LD L,A
14EA 6F           LD L,A
14EB E45374       CALL PO,7453
14EE 61           LD H,C
14EF 74           LD (HL),H
14F0 65           LD H,L
14F1 6D           LD L,L
14F2 65           LD H,L
14F3 6E           LD L,(HL)
14F4 74           LD (HL),H
14F5 206C         JR NZ,1563
14F7 6F           LD L,A
14F8 73           LD (HL),E
14F9 F4496E       CALL P,6E49
14FC 76           HALT
14FD 61           LD H,C
14FE 6C           LD L,H
14FF 69           LD L,C
1500 64           LD H,H
1501 2073         JR NZ,1576
1503 74           LD (HL),H
1504 72           LD (HL),D
1505 65           LD H,L
1506 61           LD H,C
1507 ED46         IM 0
1509 4E           LD C,(HL)
150A 2077         JR NZ,1583
150C 69           LD L,C
150D 74           LD (HL),H
150E 68           LD L,B
150F 6F           LD L,A
1510 75           LD (HL),L
1511 74           LD (HL),H
1512 2044         JR NZ,1558
1514 45           LD B,L
1515 C650         ADD A,50
1517 61           LD H,C
1518 72           LD (HL),D
1519 61           LD H,C
151A 6D           LD L,L
151B 65           LD H,L
151C 74           LD (HL),H
151D 65           LD H,L
151E 72           LD (HL),D
151F 2065         JR NZ,1586
1521 72           LD (HL),D
1522 72           LD (HL),D
1523 6F           LD L,A
1524 F25461       JP P,6154
1527 70           LD (HL),B
1528 65           LD H,L
1529 206C         JR NZ,1597
152B 6F           LD L,A
152C 61           LD H,C
152D 64           LD H,H
152E 69           LD L,C
152F 6E           LD L,(HL)
1530 67           LD H,A
1531 2065         JR NZ,1598
1533 72           LD (HL),D
1534 72           LD (HL),D
1535 6F           LD L,A
1536 F22CA0       JP P,A02C
1539 48           LD C,B
153A 43           LD B,E
153B 323030       LD (3030),A
153E 3020         JR NC,1560
1540 49           LD C,C
1541 43           LD B,E
1542 45           LD B,L
1543 2046         JR NZ,158B
1545 45           LD B,L
1546 4C           LD C,H
1547 49           LD C,C
1548 58           LD E,B
1549 2043         JR NZ,158E
154B 4F           LD C,A
154C 4D           LD C,L
154D 50           LD D,B
154E 55           LD D,L
154F 54           LD D,H
1550 45           LD B,L
1551 52           LD D,D
1552 2053         JR NZ,15A7
1554 C1           POP BC
1555 3E10         LD A,10
1557 010000       LD BC,0000
155A C31313       JP 1313,MAIN-G
155D ED43495C     LD (5C49),BC  {E-PPC}
1561 2A5D5C       LD HL,(5C5D)  {CH-ADD}
1564 EB           EX DE,HL
1565 215515       LD HL,1555
1568 E5           PUSH HL
1569 2A615C       LD HL,(5C61)  {WORKSP}
156C 37           SCF
156D ED52         SBC HL,DE
156F E5           PUSH HL
1570 60           LD H,B
1571 69           LD L,C
1572 CD6E19       CALL 196E,LINE-ADDR
1575 2006         JR NZ,157D,MAIN-ADD1
1577 CDB819       CALL 19B8,NEXT-ONE
157A CDE819       CALL 19E8,RECLAIM-2
157D C1           POP BC
157E 79           LD A,C
157F 3D           DEC A
1580 B0           OR B
1581 2828         JR Z,15AB,MAIN-ADD2
1583 C5           PUSH BC
1584 03           INC BC
1585 03           INC BC
1586 03           INC BC
1587 03           INC BC
1588 2B           DEC HL
1589 ED5B535C     LD DE,(5C53)  {PROG}
158D D5           PUSH DE
158E CD5516       CALL 1655,MAKE-ROOM
1591 E1           POP HL
1592 22535C       LD (5C53),HL  {PROG}
1595 C1           POP BC
1596 C5           PUSH BC
1597 13           INC DE
1598 2A615C       LD HL,(5C61)  {WORKSP}
159B 2B           DEC HL
159C 2B           DEC HL
159D EDB8         LDDR
159F 2A495C       LD HL,(5C49)  {E-PPC}
15A2 EB           EX DE,HL
15A3 C1           POP BC
15A4 70           LD (HL),B
15A5 2B           DEC HL
15A6 71           LD (HL),C
15A7 2B           DEC HL
15A8 73           LD (HL),E
15A9 2B           DEC HL
15AA 72           LD (HL),D
15AB F1           POP AF
15AC C3A212       JP 12A2,MAIN-EXEC
15AF F409A8       CALL P,A809
15B2 104B         DJNZ 15FF
15B4 F409C4       CALL P,C409
15B7 15           DEC D
15B8 53           LD D,E
15B9 81           ADD A,C
15BA 0F           RRCA
15BB C41552       CALL NZ,5215
15BE F409C4       CALL P,C409
15C1 15           DEC D
15C2 50           LD D,B
15C3 80           ADD A,B
15C4 CF           RST 08,ERROR-1
15C5 12           DEFB 12        ;J Invalid I/O device
15C6 010006       LD BC,0600
15C9 00           NOP
15CA 0B           DEC BC
15CB 00           NOP
15CC 010001       LD BC,0100
15CF 00           NOP
15D0 0600         LD B,00
15D2 1000         DJNZ 15D4,WAIT-KEY
15D4 FDCB026E     BIT 5,(IY+02)
15D8 2004         JR NZ,15DE,WAIT-KEY1
15DA FDCB02DE     SET 3,(IY+02)
15DE CDE615       CALL 15E6,INPUT-AD
15E1 D8           RET C
15E2 28FA         JR Z,15DE,WAIT-KEY1
15E4 CF           RST 08,ERROR-1
15E5 07           DEFB 07        ;8 End of file
15E6 D9           EXX
15E7 E5           PUSH HL
15E8 2A515C       LD HL,(5C51)  {CURCHL}
15EB 23           INC HL
15EC 23           INC HL
15ED 1808         JR 15F7,CALL-SUB
15EF 1E30         LD E,30
15F1 83           ADD A,E
15F2 D9           EXX
15F3 E5           PUSH HL
15F4 2A515C       LD HL,(5C51)  {CURCHL}
15F7 5E           LD E,(HL)
15F8 23           INC HL
15F9 56           LD D,(HL)
15FA EB           EX DE,HL
15FB CD2C16       CALL 162C,CALL-JUMP
15FE E1           POP HL
15FF D9           EXX
1600 C9           RET
1601 87           ADD A,A
1602 C616         ADD A,16
1604 6F           LD L,A
1605 265C         LD H,5C
1607 5E           LD E,(HL)
1608 23           INC HL
1609 56           LD D,(HL)
160A 7A           LD A,D
160B B3           OR E
160C 2002         JR NZ,1610,CHAN-OP-1
160E CF           RST 08,ERROR-1
160F 17           DEFB 17        ;O Invalid stream
1610 1B           DEC DE
1611 2A4F5C       LD HL,(5C4F)  {CHANS}
1614 19           ADD HL,DE
1615 22515C       LD (5C51),HL  {CURCHL}
1618 FDCB30A6     RES 4,(IY+30)
161C 23           INC HL
161D 23           INC HL
161E 23           INC HL
161F 23           INC HL
1620 4E           LD C,(HL)
1621 212D16       LD HL,162D
1624 CDDC16       CALL 16DC,INDEXER
1627 D0           RET NC
1628 1600         LD D,00
162A 5E           LD E,(HL)
162B 19           ADD HL,DE
162C E9           JP (HL)
162D 4B           LD C,E
162E 0653         LD B,53
1630 12           LD (DE),A
1631 50           LD D,B
1632 1B           DEC DE
1633 00           NOP
1634 FDCB02C6     SET 0,(IY+02)
1638 FDCB01AE     RES 5,(IY+01)
163C FDCB30E6     SET 4,(IY+30)
1640 1804         JR 1646,CHAN-S-1
1642 FDCB0286     RES 0,(IY+02)
1646 FDCB018E     RES 1,(IY+01)
164A C34D0D       JP 0D4D,TEMPS
164D FDCB01CE     SET 1,(IY+01)
1651 C9           RET
1652 010100       LD BC,0001
1655 E5           PUSH HL
1656 CD051F       CALL 1F05,TEST-ROOM
1659 E1           POP HL
165A CD6416       CALL 1664,POINTERS
165D 2A655C       LD HL,(5C65)  {STKEND}
1660 EB           EX DE,HL
1661 EDB8         LDDR
1663 C9           RET
1664 F5           PUSH AF
1665 E5           PUSH HL
1666 214B5C       LD HL,5C4B
1669 3E0E         LD A,0E
166B 5E           LD E,(HL)
166C 23           INC HL
166D 56           LD D,(HL)
166E E3           EX SP,(HL)
166F A7           AND A
1670 ED52         SBC HL,DE
1672 19           ADD HL,DE
1673 E3           EX SP,(HL)
1674 3009         JR NC,167F,PTR-DONE
1676 D5           PUSH DE
1677 EB           EX DE,HL
1678 09           ADD HL,BC
1679 EB           EX DE,HL
167A 72           LD (HL),D
167B 2B           DEC HL
167C 73           LD (HL),E
167D 23           INC HL
167E D1           POP DE
167F 23           INC HL
1680 3D           DEC A
1681 20E8         JR NZ,166B,PTR-NEXT
1683 EB           EX DE,HL
1684 D1           POP DE
1685 F1           POP AF
1686 A7           AND A
1687 ED52         SBC HL,DE
1689 44           LD B,H
168A 4D           LD C,L
168B 03           INC BC
168C 19           ADD HL,DE
168D EB           EX DE,HL
168E C9           RET
168F 00           NOP
1690 00           NOP
1691 EB           EX DE,HL
1692 118F16       LD DE,168F
1695 7E           LD A,(HL)
1696 E6C0         AND C0
1698 20F7         JR NZ,1691,LINE-NO-A
169A 56           LD D,(HL)
169B 23           INC HL
169C 5E           LD E,(HL)
169D C9           RET
169E 2A635C       LD HL,(5C63)  {STKBOT}
16A1 2B           DEC HL
16A2 CD5516       CALL 1655,MAKE-ROOM
16A5 23           INC HL
16A6 23           INC HL
16A7 C1           POP BC
16A8 ED43615C     LD (5C61),BC  {WORKSP}
16AC C1           POP BC
16AD EB           EX DE,HL
16AE 23           INC HL
16AF C9           RET
16B0 2A595C       LD HL,(5C59)  {E-LINE}
16B3 360D         LD (HL),0D
16B5 225B5C       LD (5C5B),HL  {K-CUR}
16B8 23           INC HL
16B9 3680         LD (HL),80
16BB 23           INC HL
16BC 22615C       LD (5C61),HL  {WORKSP}
16BF 2A615C       LD HL,(5C61)  {WORKSP}
16C2 22635C       LD (5C63),HL  {STKBOT}
16C5 2A635C       LD HL,(5C63)  {STKBOT}
16C8 22655C       LD (5C65),HL  {STKEND}
16CB E5           PUSH HL
16CC 21925C       LD HL,5C92
16CF 22685C       LD (5C68),HL  {MEM}
16D2 E1           POP HL
16D3 C9           RET
16D4 ED5B595C     LD DE,(5C59)  {E-LINE}
16D8 C3E519       JP 19E5,RECLAIM-1
16DB 23           INC HL
16DC 7E           LD A,(HL)
16DD A7           AND A
16DE C8           RET Z
16DF B9           CP C
16E0 23           INC HL
16E1 20F8         JR NZ,16DB,INDEXER-1
16E3 37           SCF
16E4 C9           RET
16E5 CD1E17       CALL 171E,STR-DATA
16E8 CD0117       CALL 1701,CLOSE-2
16EB 010000       LD BC,0000
16EE 11E2A3       LD DE,A3E2
16F1 EB           EX DE,HL
16F2 19           ADD HL,DE
16F3 3807         JR C,16FC,CLOSE-1
16F5 01D415       LD BC,15D4
16F8 09           ADD HL,BC
16F9 4E           LD C,(HL)
16FA 23           INC HL
16FB 46           LD B,(HL)
16FC EB           EX DE,HL
16FD 71           LD (HL),C
16FE 23           INC HL
16FF 70           LD (HL),B
1700 C9           RET
1701 E5           PUSH HL
1702 2A4F5C       LD HL,(5C4F)  {CHANS}
1705 09           ADD HL,BC
1706 23           INC HL
1707 23           INC HL
1708 23           INC HL
1709 4E           LD C,(HL)
170A EB           EX DE,HL
170B 211617       LD HL,1716
170E CDDC16       CALL 16DC,INDEXER
1711 4E           LD C,(HL)
1712 0600         LD B,00
1714 09           ADD HL,BC
1715 E9           JP (HL)
1716 4B           LD C,E
1717 05           DEC B
1718 53           LD D,E
1719 03           INC BC
171A 50           LD D,B
171B 01E1C9       LD BC,C9E1
171E CD941E       CALL 1E94,FIND-INT1
1721 FE10         CP 10
1723 3802         JR C,1727,STR-DATA1
1725 CF           RST 08,ERROR-1
1726 17           DEFB 17        ;O Invalid stream
1727 C603         ADD A,03
1729 07           RLCA
172A 21105C       LD HL,5C10
172D 4F           LD C,A
172E 0600         LD B,00
1730 09           ADD HL,BC
1731 4E           LD C,(HL)
1732 23           INC HL
1733 46           LD B,(HL)
1734 2B           DEC HL
1735 C9           RET
1736 EF           RST 28,FP-CALC
1737 01           DEFB 01,exchange
1738 38           DEFB 38,end-calc
1739 CD1E17       CALL 171E,STR-DATA
173C 78           LD A,B
173D B1           OR C
173E 2816         JR Z,1756,OPEN-1
1740 EB           EX DE,HL
1741 2A4F5C       LD HL,(5C4F)  {CHANS}
1744 09           ADD HL,BC
1745 23           INC HL
1746 23           INC HL
1747 23           INC HL
1748 7E           LD A,(HL)
1749 EB           EX DE,HL
174A FE4B         CP 4B
174C 2808         JR Z,1756,OPEN-1
174E FE53         CP 53
1750 2804         JR Z,1756,OPEN-1
1752 FE50         CP 50
1754 20CF         JR NZ,1725,REPORT-O
1756 CD5D17       CALL 175D,OPEN-2
1759 73           LD (HL),E
175A 23           INC HL
175B 72           LD (HL),D
175C C9           RET
175D E5           PUSH HL
175E CDF12B       CALL 2BF1,STK-FETCH
1761 78           LD A,B
1762 B1           OR C
1763 2002         JR NZ,1767,OPEN-3
1765 CF           RST 08,ERROR-1
1766 0E           DEFB 0E        ;F Invalid file name
1767 C5           PUSH BC
1768 1A           LD A,(DE)
1769 E6DF         AND DF
176B 4F           LD C,A
176C 217A17       LD HL,177A
176F CDDC16       CALL 16DC,INDEXER
1772 30F1         JR NC,1765,REPORT-F
1774 4E           LD C,(HL)
1775 0600         LD B,00
1777 09           ADD HL,BC
1778 C1           POP BC
1779 E9           JP (HL)
177A 4B           LD C,E
177B 0653         LD B,53
177D 08           EX AF,AF'
177E 50           LD D,B
177F 0A           LD A,(BC)
1780 00           NOP
1781 1E01         LD E,01
1783 1806         JR 178B,OPEN-END
1785 1E06         LD E,06
1787 1802         JR 178B,OPEN-END
1789 1E10         LD E,10
178B 0B           DEC BC
178C 78           LD A,B
178D B1           OR C
178E 20D5         JR NZ,1765,REPORT-F
1790 57           LD D,A
1791 E1           POP HL
1792 C9           RET
1793 1890         JR 1725,REPORT-O
1795 ED733F5C     LD (5C3F),SP  {LISTSP}
1799 FD360210     LD (IY+02),10
179D CDAF0D       CALL 0DAF,CL-ALL
17A0 FDCB02C6     SET 0,(IY+02)
17A4 FD4631       LD B,(IY+31)
17A7 CD440E       CALL 0E44,CL-LINE
17AA FDCB0286     RES 0,(IY+02)
17AE FDCB30C6     SET 0,(IY+30)
17B2 2A495C       LD HL,(5C49)  {E-PPC}
17B5 ED5B6C5C     LD DE,(5C6C)  {S-TOP}
17B9 A7           AND A
17BA ED52         SBC HL,DE
17BC 19           ADD HL,DE
17BD 3822         JR C,17E1,AUTO-L-2
17BF D5           PUSH DE
17C0 CD6E19       CALL 196E,LINE-ADDR
17C3 11C002       LD DE,02C0
17C6 EB           EX DE,HL
17C7 ED52         SBC HL,DE
17C9 E3           EX SP,(HL)
17CA CD6E19       CALL 196E,LINE-ADDR
17CD C1           POP BC
17CE C5           PUSH BC
17CF CDB819       CALL 19B8,NEXT-ONE
17D2 C1           POP BC
17D3 09           ADD HL,BC
17D4 380E         JR C,17E4,AUTO-L-3
17D6 EB           EX DE,HL
17D7 56           LD D,(HL)
17D8 23           INC HL
17D9 5E           LD E,(HL)
17DA 2B           DEC HL
17DB ED536C5C     LD (5C6C),DE  {S-TOP}
17DF 18ED         JR 17CE,AUTO-L-1
17E1 226C5C       LD (5C6C),HL  {S-TOP}
17E4 2A6C5C       LD HL,(5C6C)  {S-TOP}
17E7 CD6E19       CALL 196E,LINE-ADDR
17EA 2801         JR Z,17ED,AUTO-L-4
17EC EB           EX DE,HL
17ED CD3318       CALL 1833,LIST-ALL
17F0 FDCB02A6     RES 4,(IY+02)
17F4 C9           RET
17F5 3E03         LD A,03
17F7 1802         JR 17FB,LIST-1
17F9 3E02         LD A,02
17FB FD360200     LD (IY+02),00
17FF CD3025       CALL 2530,SYNTAX-Z
1802 C40116       CALL NZ,1601,CHAN-OPEN
1805 DF           RST 18,GET-CHAR
1806 CD7020       CALL 2070,STR-ALTER
1809 3814         JR C,181F,LIST-4
180B DF           RST 18,GET-CHAR
180C FE3B         CP 3B
180E 2804         JR Z,1814,LIST-2
1810 FE2C         CP 2C
1812 2006         JR NZ,181A,LIST-3
1814 E7           RST 20,NEXT-CHAR
1815 CD821C       CALL 1C82,EXPT-1NUM
1818 1808         JR 1822,LIST-5
181A CDE61C       CALL 1CE6,USE-ZERO
181D 1803         JR 1822,LIST-5
181F CDDE1C       CALL 1CDE,FETCH-NUM
1822 CDEE1B       CALL 1BEE,CHECK-END
1825 CD991E       CALL 1E99,FIND-INT2
1828 78           LD A,B
1829 E63F         AND 3F
182B 67           LD H,A
182C 69           LD L,C
182D 22495C       LD (5C49),HL  {E-PPC}
1830 CD6E19       CALL 196E,LINE-ADDR
1833 1E01         LD E,01
1835 CD5518       CALL 1855,OUT-LINE
1838 D7           RST 10,PRINT-A-1
1839 FDCB0266     BIT 4,(IY+02)
183D 28F6         JR Z,1835,LIST-ALL-1
183F 3A6B5C       LD A,(5C6B)  {DF-SZ}
1842 FD964F       SUB (IY+4F)
1845 20EE         JR NZ,1835,LIST-ALL-1
1847 AB           XOR E
1848 C8           RET Z
1849 E5           PUSH HL
184A D5           PUSH DE
184B 216C5C       LD HL,5C6C
184E CD0F19       CALL 190F,LN-FETCH
1851 D1           POP DE
1852 E1           POP HL
1853 18E0         JR 1835,LIST-ALL-1
1855 ED4B495C     LD BC,(5C49)  {E-PPC}
1859 CD8019       CALL 1980,CP-LINES
185C 163E         LD D,3E
185E 2805         JR Z,1865,OUT-LINE1
1860 110000       LD DE,0000
1863 CB13         RL E
1865 FD732D       LD (IY+2D),E
1868 7E           LD A,(HL)
1869 FE40         CP 40
186B C1           POP BC
186C D0           RET NC
186D C5           PUSH BC
186E CD281A       CALL 1A28,OUT-NUM-2
1871 23           INC HL
1872 23           INC HL
1873 23           INC HL
1874 FDCB0186     RES 0,(IY+01)
1878 7A           LD A,D
1879 A7           AND A
187A 2805         JR Z,1881,OUT-LINE3
187C D7           RST 10,PRINT-A-1
187D FDCB01C6     SET 0,(IY+01)
1881 D5           PUSH DE
1882 EB           EX DE,HL
1883 FDCB3096     RES 2,(IY+30)
1887 213B5C       LD HL,5C3B
188A CB96         RES 2,(HL)
188C FDCB376E     BIT 5,(IY+37)
1890 2802         JR Z,1894,OUT-LINE4
1892 CBD6         SET 2,(HL)
1894 2A5F5C       LD HL,(5C5F)  {X-PTR}
1897 A7           AND A
1898 ED52         SBC HL,DE
189A 2005         JR NZ,18A1,OUT-LINE5
189C 3E3F         LD A,3F
189E CDC118       CALL 18C1,OUT-FLASH
18A1 CDE118       CALL 18E1,OUT-CURS
18A4 EB           EX DE,HL
18A5 7E           LD A,(HL)
18A6 CDB618       CALL 18B6,NUMBER
18A9 23           INC HL
18AA FE0D         CP 0D
18AC 2806         JR Z,18B4,OUT-LINE6
18AE EB           EX DE,HL
18AF CD3719       CALL 1937
18B2 18E0         JR 1894,OUT-LINE4
18B4 D1           POP DE
18B5 C9           RET
18B6 FE0E         CP 0E
18B8 C0           RET NZ
18B9 23           INC HL
18BA 23           INC HL
18BB 23           INC HL
18BC 23           INC HL
18BD 23           INC HL
18BE 23           INC HL
18BF 7E           LD A,(HL)
18C0 C9           RET
18C1 D9           EXX
18C2 2A8F5C       LD HL,(5C8F)  {ATTR-T}
18C5 E5           PUSH HL
18C6 CBBC         RES 7,H
18C8 CBFD         SET 7,L
18CA 228F5C       LD (5C8F),HL  {ATTR-T}
18CD 21915C       LD HL,5C91
18D0 56           LD D,(HL)
18D1 D5           PUSH DE
18D2 3600         LD (HL),00
18D4 CDF409       CALL 09F4,PRINT-OUT
18D7 E1           POP HL
18D8 FD7457       LD (IY+57),H
18DB E1           POP HL
18DC 228F5C       LD (5C8F),HL  {ATTR-T}
18DF D9           EXX
18E0 C9           RET
18E1 2A5B5C       LD HL,(5C5B)  {K-CUR}
18E4 A7           AND A
18E5 ED52         SBC HL,DE
18E7 C0           RET NZ
18E8 3A415C       LD A,(5C41)  {MODE}
18EB CB07         RLC A
18ED 2804         JR Z,18F3,OUT-C-1
18EF C643         ADD A,43
18F1 1816         JR 1909,OUT-C-2
18F3 213B5C       LD HL,5C3B
18F6 CB9E         RES 3,(HL)
18F8 3E4B         LD A,4B
18FA CB56         BIT 2,(HL)
18FC 280B         JR Z,1909,OUT-C-2
18FE CBDE         SET 3,(HL)
1900 3C           INC A
1901 FDCB305E     BIT 3,(IY+30)
1905 2802         JR Z,1909,OUT-C-2
1907 3E43         LD A,43
1909 D5           PUSH DE
190A CDC118       CALL 18C1,OUT-FLASH
190D D1           POP DE
190E C9           RET
190F 5E           LD E,(HL)
1910 23           INC HL
1911 56           LD D,(HL)
1912 E5           PUSH HL
1913 EB           EX DE,HL
1914 23           INC HL
1915 CD6E19       CALL 196E,LINE-ADDR
1918 CD9516       CALL 1695,LINE-NO
191B E1           POP HL
191C FDCB376E     BIT 5,(IY+37)
1920 C0           RET NZ
1921 72           LD (HL),D
1922 2B           DEC HL
1923 73           LD (HL),E
1924 C9           RET
1925 7B           LD A,E
1926 A7           AND A
1927 F8           RET M
1928 180D         JR 1937
192A AF           XOR A
192B 09           ADD HL,BC
192C 3C           INC A
192D 38FC         JR C,192B,OUT-SP-1
192F ED42         SBC HL,BC
1931 3D           DEC A
1932 28F1         JR Z,1925,OUT-SP-2
1934 C3EF15       JP 15EF,OUT-CODE
1937 CD1B2D       CALL 2D1B,NUMERIC
193A 3030         JR NC,196C
193C FE21         CP 21
193E 382C         JR C,196C
1940 FDCB0196     RES 2,(IY+01)
1944 FECB         CP CB
1946 2824         JR Z,196C
1948 FE3A         CP 3A
194A 200E         JR NZ,195A,OUT-CH-1
194C FDCB376E     BIT 5,(IY+37)
1950 2016         JR NZ,1968,OUT-CH-2
1952 FDCB3056     BIT 2,(IY+30)
1956 2814         JR Z,196C
1958 180E         JR 1968,OUT-CH-2
195A FE22         CP 22
195C 200A         JR NZ,1968,OUT-CH-2
195E F5           PUSH AF
195F 3A6A5C       LD A,(5C6A)  {FLAGS2}
1962 EE04         XOR 04
1964 326A5C       LD (5C6A),A  {FLAGS2}
1967 F1           POP AF
1968 FDCB01D6     SET 2,(IY+01)
196C D7           RST 10,PRINT-A-1
196D C9           RET
196E E5           PUSH HL
196F 2A535C       LD HL,(5C53)  {PROG}
1972 54           LD D,H
1973 5D           LD E,L
1974 C1           POP BC
1975 CD8019       CALL 1980,CP-LINES
1978 D0           RET NC
1979 C5           PUSH BC
197A CDB819       CALL 19B8,NEXT-ONE
197D EB           EX DE,HL
197E 18F4         JR 1974,LINE-AD-1
1980 7E           LD A,(HL)
1981 B8           CP B
1982 C0           RET NZ
1983 23           INC HL
1984 7E           LD A,(HL)
1985 2B           DEC HL
1986 B9           CP C
1987 C9           RET
1988 23           INC HL
1989 23           INC HL
198A 23           INC HL
198B 225D5C       LD (5C5D),HL  {CH-ADD}
198E 0E00         LD C,00
1990 15           DEC D
1991 C8           RET Z
1992 E7           RST 20,NEXT-CHAR
1993 BB           CP E
1994 2004         JR NZ,199A,EACH-S-3
1996 A7           AND A
1997 C9           RET
1998 23           INC HL
1999 7E           LD A,(HL)
199A CDB618       CALL 18B6,NUMBER
199D 225D5C       LD (5C5D),HL  {CH-ADD}
19A0 FE22         CP 22
19A2 2001         JR NZ,19A5,EACH-S-4
19A4 0D           DEC C
19A5 FE3A         CP 3A
19A7 2804         JR Z,19AD,EACH-S-5
19A9 FECB         CP CB
19AB 2004         JR NZ,19B1,EACH-S-6
19AD CB41         BIT 0,C
19AF 28DF         JR Z,1990,EACH-S-1
19B1 FE0D         CP 0D
19B3 20E3         JR NZ,1998,EACH-S-2
19B5 15           DEC D
19B6 37           SCF
19B7 C9           RET
19B8 E5           PUSH HL
19B9 7E           LD A,(HL)
19BA FE40         CP 40
19BC 3817         JR C,19D5,NEXT-O-3
19BE CB6F         BIT 5,A
19C0 2814         JR Z,19D6,NEXT-O-4
19C2 87           ADD A,A
19C3 FAC719       JP M,19C7,NEXT-O-1
19C6 3F           CCF
19C7 010500       LD BC,0005
19CA 3002         JR NC,19CE,NEXT-O-2
19CC 0E12         LD C,12
19CE 17           RLA
19CF 23           INC HL
19D0 7E           LD A,(HL)
19D1 30FB         JR NC,19CE,NEXT-O-2
19D3 1806         JR 19DB,NEXT-O-5
19D5 23           INC HL
19D6 23           INC HL
19D7 4E           LD C,(HL)
19D8 23           INC HL
19D9 46           LD B,(HL)
19DA 23           INC HL
19DB 09           ADD HL,BC
19DC D1           POP DE
19DD A7           AND A
19DE ED52         SBC HL,DE
19E0 44           LD B,H
19E1 4D           LD C,L
19E2 19           ADD HL,DE
19E3 EB           EX DE,HL
19E4 C9           RET
19E5 CDDD19       CALL 19DD,DIFFER
19E8 C5           PUSH BC
19E9 78           LD A,B
19EA 2F           CPL
19EB 47           LD B,A
19EC 79           LD A,C
19ED 2F           CPL
19EE 4F           LD C,A
19EF 03           INC BC
19F0 CD6416       CALL 1664,POINTERS
19F3 EB           EX DE,HL
19F4 E1           POP HL
19F5 19           ADD HL,DE
19F6 D5           PUSH DE
19F7 EDB0         LDIR
19F9 E1           POP HL
19FA C9           RET
19FB 2A595C       LD HL,(5C59)  {E-LINE}
19FE 2B           DEC HL
19FF 225D5C       LD (5C5D),HL  {CH-ADD}
1A02 E7           RST 20,NEXT-CHAR
1A03 21925C       LD HL,5C92
1A06 22655C       LD (5C65),HL  {STKEND}
1A09 CD3B2D       CALL 2D3B,INT-TO-FP
1A0C CDA22D       CALL 2DA2,FP-TO-BC
1A0F 3804         JR C,1A15,E-L-1
1A11 21F0D8       LD HL,D8F0
1A14 09           ADD HL,BC
1A15 DA8A1C       JP C,1C8A,REPORT-C
1A18 C3C516       JP 16C5,SET-STK
1A1B D5           PUSH DE
1A1C E5           PUSH HL
1A1D AF           XOR A
1A1E CB78         BIT 7,B
1A20 2020         JR NZ,1A42,OUT-NUM-4
1A22 60           LD H,B
1A23 69           LD L,C
1A24 1EFF         LD E,FF
1A26 1808         JR 1A30,OUT-NUM-3
1A28 D5           PUSH DE
1A29 56           LD D,(HL)
1A2A 23           INC HL
1A2B 5E           LD E,(HL)
1A2C E5           PUSH HL
1A2D EB           EX DE,HL
1A2E 1E20         LD E,20
1A30 0118FC       LD BC,FC18
1A33 CD2A19       CALL 192A,OUT-SP-NO
1A36 019CFF       LD BC,FF9C
1A39 CD2A19       CALL 192A,OUT-SP-NO
1A3C 0EF6         LD C,F6
1A3E CD2A19       CALL 192A,OUT-SP-NO
1A41 7D           LD A,L
1A42 CDEF15       CALL 15EF,OUT-CODE
1A45 E1           POP HL
1A46 D1           POP DE
1A47 C9           RET
1A48 B1           OR C
1A49 CBBC         RES 7,H
1A4B BF           CP A
1A4C C4AFB4       CALL NZ,B4AF
1A4F 93           SUB E
1A50 91           SUB C
1A51 92           SUB D
1A52 95           SUB L
1A53 98           SBC A,B
1A54 98           SBC A,B
1A55 98           SBC A,B
1A56 98           SBC A,B
1A57 98           SBC A,B
1A58 98           SBC A,B
1A59 98           SBC A,B
1A5A 7F           LD A,A
1A5B 81           ADD A,C
1A5C 2E6C         LD L,6C
1A5E 6E           LD L,(HL)
1A5F 70           LD (HL),B
1A60 48           LD C,B
1A61 94           SUB H
1A62 56           LD D,(HL)
1A63 3F           CCF
1A64 41           LD B,C
1A65 2B           DEC HL
1A66 17           RLA
1A67 1F           RRA
1A68 37           SCF
1A69 77           LD (HL),A
1A6A 44           LD B,H
1A6B 0F           RRCA
1A6C 59           LD E,C
1A6D 2B           DEC HL
1A6E 43           LD B,E
1A6F 2D           DEC L
1A70 51           LD D,C
1A71 3A6D42       LD A,(426D)
1A74 0D           DEC C
1A75 49           LD C,C
1A76 5C           LD E,H
1A77 44           LD B,H
1A78 15           DEC D
1A79 5D           LD E,L
1A7A 013D02       LD BC,023D
1A7D 0600         LD B,00
1A7F 67           LD H,A
1A80 1E06         LD E,06
1A82 CB05         RLC L
1A84 F0           RET P
1A85 1C           INC E
1A86 0600         LD B,00
1A88 ED           NOP
1A89 1E           NOP
1A8A 00           NOP
1A8B EE1C         XOR 1C
1A8D 00           NOP
1A8E 23           INC HL
1A8F 1F           RRA
1A90 04           INC B
1A91 3D           DEC A
1A92 06CC         LD B,CC
1A94 0605         LD B,05
1A96 03           INC BC
1A97 1D           DEC E
1A98 04           INC B
1A99 00           NOP
1A9A AB           XOR E
1A9B 1D           DEC E
1A9C 05           DEC B
1A9D CD1F05       CALL 051F
1AA0 89           ADC A,C
1AA1 2005         JR NZ,1AA8,P-NEW
1AA3 02           LD (BC),A
1AA4 2C           INC L
1AA5 05           DEC B
1AA6 B2           OR D
1AA7 1B           DEC DE
1AA8 00           NOP
1AA9 B7           OR A
1AAA 1103A1       LD DE,A103
1AAD 1E05         LD E,05
1AAF F9           LD SP,HL
1AB0 17           RLA
1AB1 08           EX AF,AF'
1AB2 00           NOP
1AB3 80           ADD A,B
1AB4 1E03         LD E,03
1AB6 4F           LD C,A
1AB7 1E00         LD E,00
1AB9 5F           LD E,A
1ABA 1E03         LD E,03
1ABC AC           XOR H
1ABD 1E00         LD E,00
1ABF 6B           LD L,E
1AC0 0D           DEC C
1AC1 09           ADD HL,BC
1AC2 00           NOP
1AC3 DC2206       CALL C,0622
1AC6 00           NOP
1AC7 3A1F05       LD A,(051F)
1ACA ED           NOP
1ACB 1D           NOP
1ACC 05           DEC B
1ACD 27           DAA
1ACE 1E03         LD E,03
1AD0 42           LD B,D
1AD1 1E09         LD E,09
1AD3 05           DEC B
1AD4 82           ADD A,D
1AD5 23           INC HL
1AD6 00           NOP
1AD7 AC           XOR H
1AD8 0E05         LD C,05
1ADA C9           RET
1ADB 1F           RRA
1ADC 05           DEC B
1ADD F5           PUSH AF
1ADE 17           RLA
1ADF 0B           DEC BC
1AE0 0B           DEC BC
1AE1 0B           DEC BC
1AE2 0B           DEC BC
1AE3 08           EX AF,AF'
1AE4 00           NOP
1AE5 F8           RET M
1AE6 03           INC BC
1AE7 09           ADD HL,BC
1AE8 05           DEC B
1AE9 2023         JR NZ,1B0E
1AEB 07           RLCA
1AEC 07           RLCA
1AED 07           RLCA
1AEE 07           RLCA
1AEF 07           RLCA
1AF0 07           RLCA
1AF1 08           EX AF,AF'
1AF2 00           NOP
1AF3 7A           LD A,D
1AF4 1E06         LD E,06
1AF6 00           NOP
1AF7 94           SUB H
1AF8 220560       LD (6005),HL
1AFB 1F           RRA
1AFC 062C         LD B,2C
1AFE 0A           LD A,(BC)
1AFF 00           NOP
1B00 3617         LD (HL),17
1B02 0600         LD B,00
1B04 E5           PUSH HL
1B05 160A         LD D,0A
1B07 00           NOP
1B08 93           SUB E
1B09 17           RLA
1B0A 0A           LD A,(BC)
1B0B 2C           INC L
1B0C 0A           LD A,(BC)
1B0D 00           NOP
1B0E 93           SUB E
1B0F 17           RLA
1B10 0A           LD A,(BC)
1B11 00           NOP
1B12 93           SUB E
1B13 17           RLA
1B14 00           NOP
1B15 93           SUB E
1B16 17           RLA
1B17 FDCB01BE     RES 7,(IY+01)
1B1B CDFB19       CALL 19FB,E-LINE-NO
1B1E AF           XOR A
1B1F 32475C       LD (5C47),A  {SUBPPC}
1B22 3D           DEC A
1B23 323A5C       LD (5C3A),A  {ERR-NR}
1B26 1801         JR 1B29,STMT-L-1
1B28 E7           RST 20,NEXT-CHAR
1B29 CDBF16       CALL 16BF,SET-WORK
1B2C FD340D       INC (IY+0D)
1B2F FA8A1C       JP M,1C8A,REPORT-C
1B32 DF           RST 18,GET-CHAR
1B33 0600         LD B,00
1B35 FE0D         CP 0D
1B37 287A         JR Z,1BB3,LINE-END
1B39 FE3A         CP 3A
1B3B 28EB         JR Z,1B28,STMT-LOOP
1B3D 21761B       LD HL,1B76
1B40 E5           PUSH HL
1B41 4F           LD C,A
1B42 E7           RST 20,NEXT-CHAR
1B43 79           LD A,C
1B44 D6CE         SUB CE
1B46 DA8A1C       JP C,1C8A,REPORT-C
1B49 4F           LD C,A
1B4A 21481A       LD HL,1A48
1B4D 09           ADD HL,BC
1B4E 4E           LD C,(HL)
1B4F 09           ADD HL,BC
1B50 1803         JR 1B55,GET-PARAM
1B52 2A745C       LD HL,(5C74)  {T-ADDR}
1B55 7E           LD A,(HL)
1B56 23           INC HL
1B57 22745C       LD (5C74),HL  {T-ADDR}
1B5A 01521B       LD BC,1B52
1B5D C5           PUSH BC
1B5E 4F           LD C,A
1B5F FE20         CP 20
1B61 300C         JR NC,1B6F,SEPARATOR
1B63 21011C       LD HL,1C01
1B66 0600         LD B,00
1B68 09           ADD HL,BC
1B69 4E           LD C,(HL)
1B6A 09           ADD HL,BC
1B6B E5           PUSH HL
1B6C DF           RST 18,GET-CHAR
1B6D 05           DEC B
1B6E C9           RET
1B6F DF           RST 18,GET-CHAR
1B70 B9           CP C
1B71 C28A1C       JP NZ,1C8A,REPORT-C
1B74 E7           RST 20,NEXT-CHAR
1B75 C9           RET
1B76 CD541F       CALL 1F54,BREAK-KEY
1B79 3802         JR C,1B7D,STMT-R-1
1B7B CF           RST 08,ERROR-1
1B7C 14           DEFB 14        ;L BREAK into program
1B7D FDCB0A7E     BIT 7,(IY+0A)
1B81 2071         JR NZ,1BF4,STMT-NEXT
1B83 2A425C       LD HL,(5C42)  {NEWPPC}
1B86 CB7C         BIT 7,H
1B88 2814         JR Z,1B9E,LINE-NEW
1B8A 21FEFF       LD HL,FFFE
1B8D 22455C       LD (5C45),HL  {PPC}
1B90 2A615C       LD HL,(5C61)  {WORKSP}
1B93 2B           DEC HL
1B94 ED5B595C     LD DE,(5C59)  {E-LINE}
1B98 1B           DEC DE
1B99 3A445C       LD A,(5C44)  {NSPPC}
1B9C 1833         JR 1BD1,NEXT-LINE
1B9E CD6E19       CALL 196E,LINE-ADDR
1BA1 3A445C       LD A,(5C44)  {NSPPC}
1BA4 2819         JR Z,1BBF,LINE-USE
1BA6 A7           AND A
1BA7 2043         JR NZ,1BEC,REPORT-N
1BA9 47           LD B,A
1BAA 7E           LD A,(HL)
1BAB E6C0         AND C0
1BAD 78           LD A,B
1BAE 280F         JR Z,1BBF,LINE-USE
1BB0 CF           RST 08,ERROR-1
1BB1 FF           DEFB FF        ;unknown
1BB2 C1           POP BC
1BB3 CD3025       CALL 2530,SYNTAX-Z
1BB6 C8           RET Z
1BB7 2A555C       LD HL,(5C55)  {NXTLIN}
1BBA 3EC0         LD A,C0
1BBC A6           AND (HL)
1BBD C0           RET NZ
1BBE AF           XOR A
1BBF FE01         CP 01
1BC1 CE00         ADC A,00
1BC3 56           LD D,(HL)
1BC4 23           INC HL
1BC5 5E           LD E,(HL)
1BC6 ED53455C     LD (5C45),DE  {PPC}
1BCA 23           INC HL
1BCB 5E           LD E,(HL)
1BCC 23           INC HL
1BCD 56           LD D,(HL)
1BCE EB           EX DE,HL
1BCF 19           ADD HL,DE
1BD0 23           INC HL
1BD1 22555C       LD (5C55),HL  {NXTLIN}
1BD4 EB           EX DE,HL
1BD5 225D5C       LD (5C5D),HL  {CH-ADD}
1BD8 57           LD D,A
1BD9 1E00         LD E,00
1BDB FD360AFF     LD (IY+0A),FF
1BDF 15           DEC D
1BE0 FD720D       LD (IY+0D),D
1BE3 CA281B       JP Z,1B28,STMT-LOOP
1BE6 14           INC D
1BE7 CD8B19       CALL 198B,EACH-STMT
1BEA 2808         JR Z,1BF4,STMT-NEXT
1BEC CF           RST 08,ERROR-1
1BED 16           DEFB 16        ;N Statement lost
1BEE CD3025       CALL 2530,SYNTAX-Z
1BF1 C0           RET NZ
1BF2 C1           POP BC
1BF3 C1           POP BC
1BF4 DF           RST 18,GET-CHAR
1BF5 FE0D         CP 0D
1BF7 28BA         JR Z,1BB3,LINE-END
1BF9 FE3A         CP 3A
1BFB CA281B       JP Z,1B28,STMT-LOOP
1BFE C38A1C       JP 1C8A,REPORT-C
1C01 0F           RRCA
1C02 1D           DEC E
1C03 4B           LD C,E
1C04 09           ADD HL,BC
1C05 67           LD H,A
1C06 0B           DEC BC
1C07 7B           LD A,E
1C08 8E           ADC A,(HL)
1C09 71           LD (HL),C
1C0A B4           OR H
1C0B 81           ADD A,C
1C0C CF           RST 08,ERROR-1
1C0D CD           DEFB CD        ;unknown
1C0E DE1C         SBC A,1C
1C10 BF           CP A
1C11 C1           POP BC
1C12 CCEE1B       CALL Z,1BEE,CHECK-END
1C15 EB           EX DE,HL
1C16 2A745C       LD HL,(5C74)  {T-ADDR}
1C19 4E           LD C,(HL)
1C1A 23           INC HL
1C1B 46           LD B,(HL)
1C1C EB           EX DE,HL
1C1D C5           PUSH BC
1C1E C9           RET
1C1F CDB228       CALL 28B2,LOOK-VARS
1C22 FD363700     LD (IY+37),00
1C26 3008         JR NC,1C30,VAR-A-2
1C28 FDCB37CE     SET 1,(IY+37)
1C2C 2018         JR NZ,1C46,VAR-A-3
1C2E CF           RST 08,ERROR-1
1C2F 01           DEFB 01        ;2 Variable not found
1C30 CC9629       CALL Z,2996,STK-VAR
1C33 FDCB0176     BIT 6,(IY+01)
1C37 200D         JR NZ,1C46,VAR-A-3
1C39 AF           XOR A
1C3A CD3025       CALL 2530,SYNTAX-Z
1C3D C4F12B       CALL NZ,2BF1,STK-FETCH
1C40 21715C       LD HL,5C71
1C43 B6           OR (HL)
1C44 77           LD (HL),A
1C45 EB           EX DE,HL
1C46 ED43725C     LD (5C72),BC  {STRLEN}
1C4A 224D5C       LD (5C4D),HL  {DEST}
1C4D C9           RET
1C4E C1           POP BC
1C4F CD561C       CALL 1C56,VAL-FET-1
1C52 CDEE1B       CALL 1BEE,CHECK-END
1C55 C9           RET
1C56 3A3B5C       LD A,(5C3B)  {FLAGS}
1C59 F5           PUSH AF
1C5A CDFB24       CALL 24FB,SCANNING
1C5D F1           POP AF
1C5E FD5601       LD D,(IY+01)
1C61 AA           XOR D
1C62 E640         AND 40
1C64 2024         JR NZ,1C8A,REPORT-C
1C66 CB7A         BIT 7,D
1C68 C2FF2A       JP NZ,2AFF,LET
1C6B C9           RET
1C6C CDB228       CALL 28B2,LOOK-VARS
1C6F F5           PUSH AF
1C70 79           LD A,C
1C71 F69F         OR 9F
1C73 3C           INC A
1C74 2014         JR NZ,1C8A,REPORT-C
1C76 F1           POP AF
1C77 18A9         JR 1C22,VAR-A-1
1C79 E7           RST 20,NEXT-CHAR
1C7A CD821C       CALL 1C82,EXPT-1NUM
1C7D FE2C         CP 2C
1C7F 2009         JR NZ,1C8A,REPORT-C
1C81 E7           RST 20,NEXT-CHAR
1C82 CDFB24       CALL 24FB,SCANNING
1C85 FDCB0176     BIT 6,(IY+01)
1C89 C0           RET NZ
1C8A CF           RST 08,ERROR-1
1C8B 0B           DEFB 0B        ;C Nonsense in BASIC
1C8C CDFB24       CALL 24FB,SCANNING
1C8F FDCB0176     BIT 6,(IY+01)
1C93 C8           RET Z
1C94 18F4         JR 1C8A,REPORT-C
1C96 FDCB017E     BIT 7,(IY+01)
1C9A FDCB0286     RES 0,(IY+02)
1C9E C44D0D       CALL NZ,0D4D,TEMPS
1CA1 F1           POP AF
1CA2 3A745C       LD A,(5C74)  {T-ADDR}
1CA5 D613         SUB 13
1CA7 CDFC21       CALL 21FC,CO-TEMP-4
1CAA CDEE1B       CALL 1BEE,CHECK-END
1CAD 2A8F5C       LD HL,(5C8F)  {ATTR-T}
1CB0 228D5C       LD (5C8D),HL  {ATTR-P}
1CB3 21915C       LD HL,5C91
1CB6 7E           LD A,(HL)
1CB7 07           RLCA
1CB8 AE           XOR (HL)
1CB9 E6AA         AND AA
1CBB AE           XOR (HL)
1CBC 77           LD (HL),A
1CBD C9           RET
1CBE CD3025       CALL 2530,SYNTAX-Z
1CC1 2813         JR Z,1CD6,CL-09-1
1CC3 FDCB0286     RES 0,(IY+02)
1CC7 CD4D0D       CALL 0D4D,TEMPS
1CCA 21905C       LD HL,5C90
1CCD 7E           LD A,(HL)
1CCE F6F8         OR F8
1CD0 77           LD (HL),A
1CD1 FDCB57B6     RES 6,(IY+57)
1CD5 DF           RST 18,GET-CHAR
1CD6 CDE221       CALL 21E2,CO-TEMP-2
1CD9 189F         JR 1C7A,EXPT-2NUM
1CDB C30506       JP 0605,SAVE-ETC
1CDE FE0D         CP 0D
1CE0 2804         JR Z,1CE6,USE-ZERO
1CE2 FE3A         CP 3A
1CE4 209C         JR NZ,1C82,EXPT-1NUM
1CE6 CD3025       CALL 2530,SYNTAX-Z
1CE9 C8           RET Z
1CEA EF           RST 28,FP-CALC
1CEB A0           DEFB A0,stk-zero
1CEC 38           DEFB 38,end-calc
1CED C9           RET
1CEE CF           RST 08,ERROR-1
1CEF 08           DEFB 08        ;9 STOP statement
1CF0 C1           POP BC
1CF1 CD3025       CALL 2530,SYNTAX-Z
1CF4 280A         JR Z,1D00,IF-1
1CF6 EF           RST 28,FP-CALC
1CF7 02           DEFB 02,delete
1CF8 38           DEFB 38,end-calc
1CF9 EB           EX DE,HL
1CFA CDE934       CALL 34E9,TEST-ZERO
1CFD DAB31B       JP C,1BB3,LINE-END
1D00 C3291B       JP 1B29,STMT-L-1
1D03 FECD         CP CD
1D05 2009         JR NZ,1D10,F-USE-1
1D07 E7           RST 20,NEXT-CHAR
1D08 CD821C       CALL 1C82,EXPT-1NUM
1D0B CDEE1B       CALL 1BEE,CHECK-END
1D0E 1806         JR 1D16,F-REORDER
1D10 CDEE1B       CALL 1BEE,CHECK-END
1D13 EF           RST 28,FP-CALC
1D14 A1           DEFB A1,stk-one
1D15 38           DEFB 38,end-calc
1D16 EF           RST 28,FP-CALC
1D17 C0           DEFB C0,st-mem-0
1D18 02           DEFB 02,delete
1D19 01           DEFB 01,exchange
1D1A E0           DEFB E0,get-mem-0
1D1B 01           DEFB 01,exchange
1D1C 38           DEFB 38,end-calc
1D1D CDFF2A       CALL 2AFF,LET
1D20 22685C       LD (5C68),HL  {MEM}
1D23 2B           DEC HL
1D24 7E           LD A,(HL)
1D25 CBFE         SET 7,(HL)
1D27 010600       LD BC,0006
1D2A 09           ADD HL,BC
1D2B 07           RLCA
1D2C 3806         JR C,1D34,F-L&S
1D2E 0E0D         LD C,0D
1D30 CD5516       CALL 1655,MAKE-ROOM
1D33 23           INC HL
1D34 E5           PUSH HL
1D35 EF           RST 28,FP-CALC
1D36 02           DEFB 02,delete
1D37 02           DEFB 02,delete
1D38 38           DEFB 38,end-calc
1D39 E1           POP HL
1D3A EB           EX DE,HL
1D3B 0E0A         LD C,0A
1D3D EDB0         LDIR
1D3F 2A455C       LD HL,(5C45)  {PPC}
1D42 EB           EX DE,HL
1D43 73           LD (HL),E
1D44 23           INC HL
1D45 72           LD (HL),D
1D46 FD560D       LD D,(IY+0D)
1D49 14           INC D
1D4A 23           INC HL
1D4B 72           LD (HL),D
1D4C CDDA1D       CALL 1DDA,NEXT-LOOP
1D4F D0           RET NC
1D50 FD4638       LD B,(IY+38)
1D53 2A455C       LD HL,(5C45)  {PPC}
1D56 22425C       LD (5C42),HL  {NEWPPC}
1D59 3A475C       LD A,(5C47)  {SUBPPC}
1D5C ED44         NEG
1D5E 57           LD D,A
1D5F 2A5D5C       LD HL,(5C5D)  {CH-ADD}
1D62 1EF3         LD E,F3
1D64 C5           PUSH BC
1D65 ED4B555C     LD BC,(5C55)  {NXTLIN}
1D69 CD861D       CALL 1D86,LOOK-PROG
1D6C ED43555C     LD (5C55),BC  {NXTLIN}
1D70 C1           POP BC
1D71 3811         JR C,1D84,REPORT-I
1D73 E7           RST 20,NEXT-CHAR
1D74 F620         OR 20
1D76 B8           CP B
1D77 2803         JR Z,1D7C,F-FOUND
1D79 E7           RST 20,NEXT-CHAR
1D7A 18E8         JR 1D64,F-LOOP
1D7C E7           RST 20,NEXT-CHAR
1D7D 3E01         LD A,01
1D7F 92           SUB D
1D80 32445C       LD (5C44),A  {NSPPC}
1D83 C9           RET
1D84 CF           RST 08,ERROR-1
1D85 11           DEFB 11        ;I FOR without NEXT
1D86 7E           LD A,(HL)
1D87 FE3A         CP 3A
1D89 2818         JR Z,1DA3,LOOK-P-2
1D8B 23           INC HL
1D8C 7E           LD A,(HL)
1D8D E6C0         AND C0
1D8F 37           SCF
1D90 C0           RET NZ
1D91 46           LD B,(HL)
1D92 23           INC HL
1D93 4E           LD C,(HL)
1D94 ED43425C     LD (5C42),BC  {NEWPPC}
1D98 23           INC HL
1D99 4E           LD C,(HL)
1D9A 23           INC HL
1D9B 46           LD B,(HL)
1D9C E5           PUSH HL
1D9D 09           ADD HL,BC
1D9E 44           LD B,H
1D9F 4D           LD C,L
1DA0 E1           POP HL
1DA1 1600         LD D,00
1DA3 C5           PUSH BC
1DA4 CD8B19       CALL 198B,EACH-STMT
1DA7 C1           POP BC
1DA8 D0           RET NC
1DA9 18E0         JR 1D8B,LOOK-P-1
1DAB FDCB374E     BIT 1,(IY+37)
1DAF C22E1C       JP NZ,1C2E,REPORT-2
1DB2 2A4D5C       LD HL,(5C4D)  {DEST}
1DB5 CB7E         BIT 7,(HL)
1DB7 281F         JR Z,1DD8,REPORT-1
1DB9 23           INC HL
1DBA 22685C       LD (5C68),HL  {MEM}
1DBD EF           RST 28,FP-CALC
1DBE E0           DEFB E0,get-mem-0
1DBF E2           DEFB E2,get-mem-2
1DC0 0F           DEFB 0F,addition
1DC1 C0           DEFB C0,st-mem-0
1DC2 02           DEFB 02,delete
1DC3 38           DEFB 38,end-calc
1DC4 CDDA1D       CALL 1DDA,NEXT-LOOP
1DC7 D8           RET C
1DC8 2A685C       LD HL,(5C68)  {MEM}
1DCB 110F00       LD DE,000F
1DCE 19           ADD HL,DE
1DCF 5E           LD E,(HL)
1DD0 23           INC HL
1DD1 56           LD D,(HL)
1DD2 23           INC HL
1DD3 66           LD H,(HL)
1DD4 EB           EX DE,HL
1DD5 C3731E       JP 1E73
1DD8 CF           RST 08,ERROR-1
1DD9 00           DEFB 00        ;1 NEXT without FOR
1DDA EF           RST 28,FP-CALC
1DDB E1           DEFB E1,get-mem-1
1DDC E0           DEFB E0,get-mem-0
1DDD E2           DEFB E2,get-mem-2
1DDE 36           DEFB 36,less-0
1DDF 00           DEFB 00,jump-true
1DE0 02           DEFB 02,delete
1DE1 01           DEFB 01,exchange
1DE2 03           DEFB 03,subtract
1DE3 37           DEFB 37,greater-0
1DE4 00           DEFB 00,jump-true
1DE5 04           DEFB 04,multiply
1DE6 38           DEFB 38,end-calc
1DE7 A7           AND A
1DE8 C9           RET
1DE9 3837         JR C,1E22
1DEB C9           RET
1DEC E7           RST 20,NEXT-CHAR
1DED CD1F1C       CALL 1C1F,CLASS-01
1DF0 CD3025       CALL 2530,SYNTAX-Z
1DF3 2829         JR Z,1E1E,READ-2
1DF5 DF           RST 18,GET-CHAR
1DF6 225F5C       LD (5C5F),HL  {X-PTR}
1DF9 2A575C       LD HL,(5C57)  {DATADD}
1DFC 7E           LD A,(HL)
1DFD FE2C         CP 2C
1DFF 2809         JR Z,1E0A,READ-1
1E01 1EE4         LD E,E4
1E03 CD861D       CALL 1D86,LOOK-PROG
1E06 3002         JR NC,1E0A,READ-1
1E08 CF           RST 08,ERROR-1
1E09 0D           DEFB 0D        ;E Out of DATA
1E0A CD7700       CALL 0077,TEMP-PTR1
1E0D CD561C       CALL 1C56,VAL-FET-1
1E10 DF           RST 18,GET-CHAR
1E11 22575C       LD (5C57),HL  {DATADD}
1E14 2A5F5C       LD HL,(5C5F)  {X-PTR}
1E17 FD362600     LD (IY+26),00
1E1B CD7800       CALL 0078,TEMP-PTR2
1E1E DF           RST 18,GET-CHAR
1E1F FE2C         CP 2C
1E21 28C9         JR Z,1DEC,READ-3
1E23 CDEE1B       CALL 1BEE,CHECK-END
1E26 C9           RET
1E27 CD3025       CALL 2530,SYNTAX-Z
1E2A 200B         JR NZ,1E37,DATA-2
1E2C CDFB24       CALL 24FB,SCANNING
1E2F FE2C         CP 2C
1E31 C4EE1B       CALL NZ,1BEE,CHECK-END
1E34 E7           RST 20,NEXT-CHAR
1E35 18F5         JR 1E2C,DATA-1
1E37 3EE4         LD A,E4
1E39 47           LD B,A
1E3A EDB9         CPDR
1E3C 110002       LD DE,0200
1E3F C38B19       JP 198B,EACH-STMT
1E42 CD991E       CALL 1E99,FIND-INT2
1E45 60           LD H,B
1E46 69           LD L,C
1E47 CD6E19       CALL 196E,LINE-ADDR
1E4A 2B           DEC HL
1E4B 22575C       LD (5C57),HL  {DATADD}
1E4E C9           RET
1E4F CD991E       CALL 1E99,FIND-INT2
1E52 78           LD A,B
1E53 B1           OR C
1E54 2004         JR NZ,1E5A,RAND-1
1E56 ED4B785C     LD BC,(5C78)  {FRAMES}
1E5A ED43765C     LD (5C76),BC  {SEED}
1E5E C9           RET
1E5F 2A6E5C       LD HL,(5C6E)  {OLDPPC}
1E62 FD5636       LD D,(IY+36)
1E65 180C         JR 1E73
1E67 CD991E       CALL 1E99,FIND-INT2
1E6A 60           LD H,B
1E6B 69           LD L,C
1E6C 1600         LD D,00
1E6E 7C           LD A,H
1E6F FEF0         CP F0
1E71 302C         JR NC,1E9F,REPORT-B
1E73 22425C       LD (5C42),HL  {NEWPPC}
1E76 FD720A       LD (IY+0A),D
1E79 C9           RET
1E7A CD851E       CALL 1E85,TWO-PARAM
1E7D ED79         OUT (C),A
1E7F C9           RET
1E80 CD851E       CALL 1E85,TWO-PARAM
1E83 02           LD (BC),A
1E84 C9           RET
1E85 CDD52D       CALL 2DD5,FP-TO-A
1E88 3815         JR C,1E9F,REPORT-B
1E8A 2802         JR Z,1E8E,TWO-P-1
1E8C ED44         NEG
1E8E F5           PUSH AF
1E8F CD991E       CALL 1E99,FIND-INT2
1E92 F1           POP AF
1E93 C9           RET
1E94 CDD52D       CALL 2DD5,FP-TO-A
1E97 1803         JR 1E9C,FIND-I-1
1E99 CDA22D       CALL 2DA2,FP-TO-BC
1E9C 3801         JR C,1E9F,REPORT-B
1E9E C8           RET Z
1E9F CF           RST 08,ERROR-1
1EA0 0A           DEFB 0A        ;B Integer out of range
1EA1 CD671E       CALL 1E67,GO-TO-2
1EA4 010000       LD BC,0000
1EA7 CD451E       CALL 1E45,REST-RUN
1EAA 1803         JR 1EAF,CLEAR-RUN
1EAC CD991E       CALL 1E99,FIND-INT2
1EAF 78           LD A,B
1EB0 B1           OR C
1EB1 2004         JR NZ,1EB7,CLEAR-1
1EB3 ED4BB25C     LD BC,(5CB2)  {RAMTOP}
1EB7 C5           PUSH BC
1EB8 ED5B4B5C     LD DE,(5C4B)  {VARS}
1EBC 2A595C       LD HL,(5C59)  {E-LINE}
1EBF 2B           DEC HL
1EC0 CDE519       CALL 19E5,RECLAIM-1
1EC3 CD6B0D       CALL 0D6B,CLS
1EC6 2A655C       LD HL,(5C65)  {STKEND}
1EC9 113200       LD DE,0032
1ECC 19           ADD HL,DE
1ECD D1           POP DE
1ECE ED52         SBC HL,DE
1ED0 3008         JR NC,1EDA,REPORT-M
1ED2 2AB45C       LD HL,(5CB4)  {P-RAMT}
1ED5 A7           AND A
1ED6 ED52         SBC HL,DE
1ED8 3002         JR NC,1EDC,CLEAR-2
1EDA CF           RST 08,ERROR-1
1EDB 15           DEFB 15        ;M RAMTOP no good
1EDC EB           EX DE,HL
1EDD 22B25C       LD (5CB2),HL  {RAMTOP}
1EE0 D1           POP DE
1EE1 C1           POP BC
1EE2 363E         LD (HL),3E
1EE4 2B           DEC HL
1EE5 F9           LD SP,HL
1EE6 C5           PUSH BC
1EE7 ED733D5C     LD (5C3D),SP  {ERR-SP}
1EEB EB           EX DE,HL
1EEC E9           JP (HL)
1EED D1           POP DE
1EEE FD660D       LD H,(IY+0D)
1EF1 24           INC H
1EF2 E3           EX SP,(HL)
1EF3 33           INC SP
1EF4 ED4B455C     LD BC,(5C45)  {PPC}
1EF8 C5           PUSH BC
1EF9 E5           PUSH HL
1EFA ED733D5C     LD (5C3D),SP  {ERR-SP}
1EFE D5           PUSH DE
1EFF CD671E       CALL 1E67,GO-TO-2
1F02 011400       LD BC,0014
1F05 2A655C       LD HL,(5C65)  {STKEND}
1F08 09           ADD HL,BC
1F09 380A         JR C,1F15,REPORT-4
1F0B EB           EX DE,HL
1F0C 215000       LD HL,0050
1F0F 19           ADD HL,DE
1F10 3803         JR C,1F15,REPORT-4
1F12 ED72         SBC HL,SP
1F14 D8           RET C
1F15 2E03         LD L,03
1F17 C35500       JP 0055,ERROR-3
1F1A 010000       LD BC,0000
1F1D CD051F       CALL 1F05,TEST-ROOM
1F20 44           LD B,H
1F21 4D           LD C,L
1F22 C9           RET
1F23 C1           POP BC
1F24 E1           POP HL
1F25 D1           POP DE
1F26 7A           LD A,D
1F27 FE3E         CP 3E
1F29 280B         JR Z,1F36,REPORT-7
1F2B 3B           DEC SP
1F2C E3           EX SP,(HL)
1F2D EB           EX DE,HL
1F2E ED733D5C     LD (5C3D),SP  {ERR-SP}
1F32 C5           PUSH BC
1F33 C3731E       JP 1E73
1F36 D5           PUSH DE
1F37 E5           PUSH HL
1F38 CF           RST 08,ERROR-1
1F39 06           DEFB 06        ;7 RETURN without GOSUB
1F3A CD991E       CALL 1E99,FIND-INT2
1F3D 76           HALT
1F3E 0B           DEC BC
1F3F 78           LD A,B
1F40 B1           OR C
1F41 280C         JR Z,1F4F,PAUSE-END
1F43 78           LD A,B
1F44 A1           AND C
1F45 3C           INC A
1F46 2001         JR NZ,1F49,PAUSE-2
1F48 03           INC BC
1F49 FDCB016E     BIT 5,(IY+01)
1F4D 28EE         JR Z,1F3D,PAUSE-1
1F4F FDCB01AE     RES 5,(IY+01)
1F53 C9           RET
1F54 3E7F         LD A,7F
1F56 DBFE         IN A,(FE)
1F58 1F           RRA
1F59 D8           RET C
1F5A 3EFE         LD A,FE
1F5C DBFE         IN A,(FE)
1F5E 1F           RRA
1F5F C9           RET
1F60 CD3025       CALL 2530,SYNTAX-Z
1F63 2805         JR Z,1F6A,DEF-FN-1
1F65 3ECE         LD A,CE
1F67 C3391E       JP 1E39,PASS-BY
1F6A FDCB01F6     SET 6,(IY+01)
1F6E CD8D2C       CALL 2C8D,ALPHA
1F71 3016         JR NC,1F89,DEF-FN-4
1F73 E7           RST 20,NEXT-CHAR
1F74 FE24         CP 24
1F76 2005         JR NZ,1F7D,DEF-FN-2
1F78 FDCB01B6     RES 6,(IY+01)
1F7C E7           RST 20,NEXT-CHAR
1F7D FE28         CP 28
1F7F 203C         JR NZ,1FBD,DEF-FN-7
1F81 E7           RST 20,NEXT-CHAR
1F82 FE29         CP 29
1F84 2820         JR Z,1FA6,DEF-FN-6
1F86 CD8D2C       CALL 2C8D,ALPHA
1F89 D28A1C       JP NC,1C8A,REPORT-C
1F8C EB           EX DE,HL
1F8D E7           RST 20,NEXT-CHAR
1F8E FE24         CP 24
1F90 2002         JR NZ,1F94,DEF-FN-5
1F92 EB           EX DE,HL
1F93 E7           RST 20,NEXT-CHAR
1F94 EB           EX DE,HL
1F95 010600       LD BC,0006
1F98 CD5516       CALL 1655,MAKE-ROOM
1F9B 23           INC HL
1F9C 23           INC HL
1F9D 360E         LD (HL),0E
1F9F FE2C         CP 2C
1FA1 2003         JR NZ,1FA6,DEF-FN-6
1FA3 E7           RST 20,NEXT-CHAR
1FA4 18E0         JR 1F86,DEF-FN-3
1FA6 FE29         CP 29
1FA8 2013         JR NZ,1FBD,DEF-FN-7
1FAA E7           RST 20,NEXT-CHAR
1FAB FE3D         CP 3D
1FAD 200E         JR NZ,1FBD,DEF-FN-7
1FAF E7           RST 20,NEXT-CHAR
1FB0 3A3B5C       LD A,(5C3B)  {FLAGS}
1FB3 F5           PUSH AF
1FB4 CDFB24       CALL 24FB,SCANNING
1FB7 F1           POP AF
1FB8 FDAE01       XOR (IY+01)
1FBB E640         AND 40
1FBD C28A1C       JP NZ,1C8A,REPORT-C
1FC0 CDEE1B       CALL 1BEE,CHECK-END
1FC3 CD3025       CALL 2530,SYNTAX-Z
1FC6 E1           POP HL
1FC7 C8           RET Z
1FC8 E9           JP (HL)
1FC9 3E03         LD A,03
1FCB 1802         JR 1FCF,PRINT-1
1FCD 3E02         LD A,02
1FCF CD3025       CALL 2530,SYNTAX-Z
1FD2 C40116       CALL NZ,1601,CHAN-OPEN
1FD5 CD4D0D       CALL 0D4D,TEMPS
1FD8 CDDF1F       CALL 1FDF,PRINT-2
1FDB CDEE1B       CALL 1BEE,CHECK-END
1FDE C9           RET
1FDF DF           RST 18,GET-CHAR
1FE0 CD4520       CALL 2045,PR-END-Z
1FE3 280D         JR Z,1FF2,PRINT-4
1FE5 CD4E20       CALL 204E,PR-POSN-1
1FE8 28FB         JR Z,1FE5,PRINT-3
1FEA CDFC1F       CALL 1FFC,PR-ITEM-1
1FED CD4E20       CALL 204E,PR-POSN-1
1FF0 28F3         JR Z,1FE5,PRINT-3
1FF2 FE29         CP 29
1FF4 C8           RET Z
1FF5 CDC31F       CALL 1FC3,UNSTACK-Z
1FF8 3E0D         LD A,0D
1FFA D7           RST 10,PRINT-A-1
1FFB C9           RET
1FFC DF           RST 18,GET-CHAR
1FFD FEAC         CP AC
1FFF 200D         JR NZ,200E,PR-ITEM-2
2001 CD791C       CALL 1C79,NEXT-2NUM
2004 CDC31F       CALL 1FC3,UNSTACK-Z
2007 CD0723       CALL 2307,STK-TO-BC
200A 3E16         LD A,16
200C 1810         JR 201E,PR-AT-TAB
200E FEAD         CP AD
2010 2012         JR NZ,2024,PR-ITEM-3
2012 E7           RST 20,NEXT-CHAR
2013 CD821C       CALL 1C82,EXPT-1NUM
2016 CDC31F       CALL 1FC3,UNSTACK-Z
2019 CD991E       CALL 1E99,FIND-INT2
201C 3E17         LD A,17
201E D7           RST 10,PRINT-A-1
201F 79           LD A,C
2020 D7           RST 10,PRINT-A-1
2021 78           LD A,B
2022 D7           RST 10,PRINT-A-1
2023 C9           RET
2024 CDF221       CALL 21F2,CO-TEMP-3
2027 D0           RET NC
2028 CD7020       CALL 2070,STR-ALTER
202B D0           RET NC
202C CDFB24       CALL 24FB,SCANNING
202F CDC31F       CALL 1FC3,UNSTACK-Z
2032 FDCB0176     BIT 6,(IY+01)
2036 CCF12B       CALL Z,2BF1,STK-FETCH
2039 C2E32D       JP NZ,2DE3,PRINT-FP
203C 78           LD A,B
203D B1           OR C
203E 0B           DEC BC
203F C8           RET Z
2040 1A           LD A,(DE)
2041 13           INC DE
2042 D7           RST 10,PRINT-A-1
2043 18F7         JR 203C,PR-STRING
2045 FE29         CP 29
2047 C8           RET Z
2048 FE0D         CP 0D
204A C8           RET Z
204B FE3A         CP 3A
204D C9           RET
204E DF           RST 18,GET-CHAR
204F FE3B         CP 3B
2051 2814         JR Z,2067,PR-POSN-3
2053 FE2C         CP 2C
2055 200A         JR NZ,2061,PR-POSN-2
2057 CD3025       CALL 2530,SYNTAX-Z
205A 280B         JR Z,2067,PR-POSN-3
205C 3E06         LD A,06
205E D7           RST 10,PRINT-A-1
205F 1806         JR 2067,PR-POSN-3
2061 FE27         CP 27
2063 C0           RET NZ
2064 CDF51F       CALL 1FF5,PRINT-CR
2067 E7           RST 20,NEXT-CHAR
2068 CD4520       CALL 2045,PR-END-Z
206B 2001         JR NZ,206E,PR-POSN-4
206D C1           POP BC
206E BF           CP A
206F C9           RET
2070 FE23         CP 23
2072 37           SCF
2073 C0           RET NZ
2074 E7           RST 20,NEXT-CHAR
2075 CD821C       CALL 1C82,EXPT-1NUM
2078 A7           AND A
2079 CDC31F       CALL 1FC3,UNSTACK-Z
207C CD941E       CALL 1E94,FIND-INT1
207F FE10         CP 10
2081 D20E16       JP NC,160E,REPORT-O
2084 CD0116       CALL 1601,CHAN-OPEN
2087 A7           AND A
2088 C9           RET
2089 CD3025       CALL 2530,SYNTAX-Z
208C 2808         JR Z,2096,INPUT-1
208E 3E01         LD A,01
2090 CD0116       CALL 1601,CHAN-OPEN
2093 CD6E0D       CALL 0D6E,CLS-LOWER
2096 FD360201     LD (IY+02),01
209A CDC120       CALL 20C1,IN-ITEM-1
209D CDEE1B       CALL 1BEE,CHECK-END
20A0 ED4B885C     LD BC,(5C88)  {S-POSN}
20A4 3A6B5C       LD A,(5C6B)  {DF-SZ}
20A7 B8           CP B
20A8 3803         JR C,20AD,INPUT-2
20AA 0E21         LD C,21
20AC 47           LD B,A
20AD ED43885C     LD (5C88),BC  {S-POSN}
20B1 3E19         LD A,19
20B3 90           SUB B
20B4 328C5C       LD (5C8C),A  {SCR-CT}
20B7 FDCB0286     RES 0,(IY+02)
20BB CDD90D       CALL 0DD9,CL-SET
20BE C36E0D       JP 0D6E,CLS-LOWER
20C1 CD4E20       CALL 204E,PR-POSN-1
20C4 28FB         JR Z,20C1,IN-ITEM-1
20C6 FE28         CP 28
20C8 200E         JR NZ,20D8,IN-ITEM-2
20CA E7           RST 20,NEXT-CHAR
20CB CDDF1F       CALL 1FDF,PRINT-2
20CE DF           RST 18,GET-CHAR
20CF FE29         CP 29
20D1 C28A1C       JP NZ,1C8A,REPORT-C
20D4 E7           RST 20,NEXT-CHAR
20D5 C3B221       JP 21B2,IN-NEXT-2
20D8 FECA         CP CA
20DA 2011         JR NZ,20ED,IN-ITEM-3
20DC E7           RST 20,NEXT-CHAR
20DD CD1F1C       CALL 1C1F,CLASS-01
20E0 FDCB37FE     SET 7,(IY+37)
20E4 FDCB0176     BIT 6,(IY+01)
20E8 C28A1C       JP NZ,1C8A,REPORT-C
20EB 180D         JR 20FA,IN-PROMPT
20ED CD8D2C       CALL 2C8D,ALPHA
20F0 D2AF21       JP NC,21AF,IN-NEXT-1
20F3 CD1F1C       CALL 1C1F,CLASS-01
20F6 FDCB37BE     RES 7,(IY+37)
20FA CD3025       CALL 2530,SYNTAX-Z
20FD CAB221       JP Z,21B2,IN-NEXT-2
2100 CDBF16       CALL 16BF,SET-WORK
2103 21715C       LD HL,5C71
2106 CBB6         RES 6,(HL)
2108 CBEE         SET 5,(HL)
210A 010100       LD BC,0001
210D CB7E         BIT 7,(HL)
210F 200B         JR NZ,211C,IN-PR-2
2111 3A3B5C       LD A,(5C3B)  {FLAGS}
2114 E640         AND 40
2116 2002         JR NZ,211A,IN-PR-1
2118 0E03         LD C,03
211A B6           OR (HL)
211B 77           LD (HL),A
211C F7           RST 30,BC-SPACES
211D 360D         LD (HL),0D
211F 79           LD A,C
2120 0F           RRCA
2121 0F           RRCA
2122 3005         JR NC,2129,IN-PR-3
2124 3E22         LD A,22
2126 12           LD (DE),A
2127 2B           DEC HL
2128 77           LD (HL),A
2129 225B5C       LD (5C5B),HL  {K-CUR}
212C FDCB377E     BIT 7,(IY+37)
2130 202C         JR NZ,215E,IN-VAR-3
2132 2A5D5C       LD HL,(5C5D)  {CH-ADD}
2135 E5           PUSH HL
2136 2A3D5C       LD HL,(5C3D)  {ERR-SP}
2139 E5           PUSH HL
213A 213A21       LD HL,213A
213D E5           PUSH HL
213E FDCB3066     BIT 4,(IY+30)
2142 2804         JR Z,2148,IN-VAR-2
2144 ED733D5C     LD (5C3D),SP  {ERR-SP}
2148 2A615C       LD HL,(5C61)  {WORKSP}
214B CDA711       CALL 11A7,REMOVE-FP
214E FD3600FF     LD (IY+00),FF
2152 CD2C0F       CALL 0F2C,EDITOR
2155 FDCB01BE     RES 7,(IY+01)
2159 CDB921       CALL 21B9,IN-ASSIGN
215C 1803         JR 2161,IN-VAR-4
215E CD2C0F       CALL 0F2C,EDITOR
2161 FD362200     LD (IY+22),00
2165 CDD621       CALL 21D6,IN-CHAN-K
2168 200A         JR NZ,2174,IN-VAR-5
216A CD1D11       CALL 111D,ED-COPY
216D ED4B825C     LD BC,(5C82)  {ECHO-E}
2171 CDD90D       CALL 0DD9,CL-SET
2174 21715C       LD HL,5C71
2177 CBAE         RES 5,(HL)
2179 CB7E         BIT 7,(HL)
217B CBBE         RES 7,(HL)
217D 201C         JR NZ,219B,IN-VAR-6
217F E1           POP HL
2180 E1           POP HL
2181 223D5C       LD (5C3D),HL  {ERR-SP}
2184 E1           POP HL
2185 225F5C       LD (5C5F),HL  {X-PTR}
2188 FDCB01FE     SET 7,(IY+01)
218C CDB921       CALL 21B9,IN-ASSIGN
218F 2A5F5C       LD HL,(5C5F)  {X-PTR}
2192 FD362600     LD (IY+26),00
2196 225D5C       LD (5C5D),HL  {CH-ADD}
2199 1817         JR 21B2,IN-NEXT-2
219B 2A635C       LD HL,(5C63)  {STKBOT}
219E ED5B615C     LD DE,(5C61)  {WORKSP}
21A2 37           SCF
21A3 ED52         SBC HL,DE
21A5 44           LD B,H
21A6 4D           LD C,L
21A7 CDB22A       CALL 2AB2,STK-STO-$
21AA CDFF2A       CALL 2AFF,LET
21AD 1803         JR 21B2,IN-NEXT-2
21AF CDFC1F       CALL 1FFC,PR-ITEM-1
21B2 CD4E20       CALL 204E,PR-POSN-1
21B5 CAC120       JP Z,20C1,IN-ITEM-1
21B8 C9           RET
21B9 2A615C       LD HL,(5C61)  {WORKSP}
21BC 225D5C       LD (5C5D),HL  {CH-ADD}
21BF DF           RST 18,GET-CHAR
21C0 FEE2         CP E2
21C2 280C         JR Z,21D0,IN-STOP
21C4 3A715C       LD A,(5C71)  {FLAGX}
21C7 CD591C       CALL 1C59,VAL-FET-2
21CA DF           RST 18,GET-CHAR
21CB FE0D         CP 0D
21CD C8           RET Z
21CE CF           RST 08,ERROR-1
21CF 0B           DEFB 0B        ;C Nonsense in BASIC
21D0 CD3025       CALL 2530,SYNTAX-Z
21D3 C8           RET Z
21D4 CF           RST 08,ERROR-1
21D5 10           DEFB 10        ;H STOP in INPUT
21D6 2A515C       LD HL,(5C51)  {CURCHL}
21D9 23           INC HL
21DA 23           INC HL
21DB 23           INC HL
21DC 23           INC HL
21DD 7E           LD A,(HL)
21DE FE4B         CP 4B
21E0 C9           RET
21E1 E7           RST 20,NEXT-CHAR
21E2 CDF221       CALL 21F2,CO-TEMP-3
21E5 D8           RET C
21E6 DF           RST 18,GET-CHAR
21E7 FE2C         CP 2C
21E9 28F6         JR Z,21E1,CO-TEMP-1
21EB FE3B         CP 3B
21ED 28F2         JR Z,21E1,CO-TEMP-1
21EF C38A1C       JP 1C8A,REPORT-C
21F2 FED9         CP D9
21F4 D8           RET C
21F5 FEDF         CP DF
21F7 3F           CCF
21F8 D8           RET C
21F9 F5           PUSH AF
21FA E7           RST 20,NEXT-CHAR
21FB F1           POP AF
21FC D6C9         SUB C9
21FE F5           PUSH AF
21FF CD821C       CALL 1C82,EXPT-1NUM
2202 F1           POP AF
2203 A7           AND A
2204 CDC31F       CALL 1FC3,UNSTACK-Z
2207 F5           PUSH AF
2208 CD941E       CALL 1E94,FIND-INT1
220B 57           LD D,A
220C F1           POP AF
220D D7           RST 10,PRINT-A-1
220E 7A           LD A,D
220F D7           RST 10,PRINT-A-1
2210 C9           RET
2211 D611         SUB 11
2213 CE00         ADC A,00
2215 281D         JR Z,2234,CO-TEMP-7
2217 D602         SUB 02
2219 CE00         ADC A,00
221B 2856         JR Z,2273,CO-TEMP-C
221D FE01         CP 01
221F 7A           LD A,D
2220 0601         LD B,01
2222 2004         JR NZ,2228,CO-TEMP-6
2224 07           RLCA
2225 07           RLCA
2226 0604         LD B,04
2228 4F           LD C,A
2229 7A           LD A,D
222A FE02         CP 02
222C 3016         JR NC,2244,REPORT-K
222E 79           LD A,C
222F 21915C       LD HL,5C91
2232 1838         JR 226C,CO-CHANGE
2234 7A           LD A,D
2235 0607         LD B,07
2237 3805         JR C,223E,CO-TEMP-8
2239 07           RLCA
223A 07           RLCA
223B 07           RLCA
223C 0638         LD B,38
223E 4F           LD C,A
223F 7A           LD A,D
2240 FE0A         CP 0A
2242 3802         JR C,2246,CO-TEMP-9
2244 CF           RST 08,ERROR-1
2245 13           DEFB 13        ;K Invalid colour
2246 218F5C       LD HL,5C8F
2249 FE08         CP 08
224B 380B         JR C,2258,CO-TEMP-B
224D 7E           LD A,(HL)
224E 2807         JR Z,2257,CO-TEMP-A
2250 B0           OR B
2251 2F           CPL
2252 E624         AND 24
2254 2801         JR Z,2257,CO-TEMP-A
2256 78           LD A,B
2257 4F           LD C,A
2258 79           LD A,C
2259 CD6C22       CALL 226C,CO-CHANGE
225C 3E07         LD A,07
225E BA           CP D
225F 9F           SBC A,A
2260 CD6C22       CALL 226C,CO-CHANGE
2263 07           RLCA
2264 07           RLCA
2265 E650         AND 50
2267 47           LD B,A
2268 3E08         LD A,08
226A BA           CP D
226B 9F           SBC A,A
226C AE           XOR (HL)
226D A0           AND B
226E AE           XOR (HL)
226F 77           LD (HL),A
2270 23           INC HL
2271 78           LD A,B
2272 C9           RET
2273 9F           SBC A,A
2274 7A           LD A,D
2275 0F           RRCA
2276 0680         LD B,80
2278 2003         JR NZ,227D,CO-TEMP-D
227A 0F           RRCA
227B 0640         LD B,40
227D 4F           LD C,A
227E 7A           LD A,D
227F FE08         CP 08
2281 2804         JR Z,2287,CO-TEMP-E
2283 FE02         CP 02
2285 30BD         JR NC,2244,REPORT-K
2287 79           LD A,C
2288 218F5C       LD HL,5C8F
228B CD6C22       CALL 226C,CO-CHANGE
228E 79           LD A,C
228F 0F           RRCA
2290 0F           RRCA
2291 0F           RRCA
2292 18D8         JR 226C,CO-CHANGE
2294 CD941E       CALL 1E94,FIND-INT1
2297 FE08         CP 08
2299 30A9         JR NC,2244,REPORT-K
229B D3FE         OUT (FE),A
229D 07           RLCA
229E 07           RLCA
229F 07           RLCA
22A0 CB6F         BIT 5,A
22A2 2002         JR NZ,22A6,BORDER-1
22A4 EE07         XOR 07
22A6 32485C       LD (5C48),A  {BORDCR}
22A9 C9           RET
22AA 3EAF         LD A,AF
22AC 90           SUB B
22AD DAF924       JP C,24F9,REPORT-B
22B0 47           LD B,A
22B1 A7           AND A
22B2 1F           RRA
22B3 37           SCF
22B4 1F           RRA
22B5 A7           AND A
22B6 1F           RRA
22B7 A8           XOR B
22B8 E6F8         AND F8
22BA A8           XOR B
22BB 67           LD H,A
22BC 79           LD A,C
22BD 07           RLCA
22BE 07           RLCA
22BF 07           RLCA
22C0 A8           XOR B
22C1 E6C7         AND C7
22C3 A8           XOR B
22C4 07           RLCA
22C5 07           RLCA
22C6 6F           LD L,A
22C7 79           LD A,C
22C8 E607         AND 07
22CA C9           RET
22CB CD0723       CALL 2307,STK-TO-BC
22CE CDAA22       CALL 22AA,PIXEL-ADD
22D1 47           LD B,A
22D2 04           INC B
22D3 7E           LD A,(HL)
22D4 07           RLCA
22D5 10FD         DJNZ 22D4,POINT-LP
22D7 E601         AND 01
22D9 C3282D       JP 2D28,STACK-A
22DC CD0723       CALL 2307,STK-TO-BC
22DF CDE522       CALL 22E5,PLOT-SUB
22E2 C34D0D       JP 0D4D,TEMPS
22E5 ED437D5C     LD (5C7D),BC  {COORDS}
22E9 CDAA22       CALL 22AA,PIXEL-ADD
22EC 47           LD B,A
22ED 04           INC B
22EE 3EFE         LD A,FE
22F0 0F           RRCA
22F1 10FD         DJNZ 22F0,PLOT-LOOP
22F3 47           LD B,A
22F4 7E           LD A,(HL)
22F5 FD4E57       LD C,(IY+57)
22F8 CB41         BIT 0,C
22FA 2001         JR NZ,22FD,PL-TST-IN
22FC A0           AND B
22FD CB51         BIT 2,C
22FF 2002         JR NZ,2303,PLOT-END
2301 A8           XOR B
2302 2F           CPL
2303 77           LD (HL),A
2304 C3DB0B       JP 0BDB,PO-ATTR
2307 CD1423       CALL 2314,STK-TO-A
230A 47           LD B,A
230B C5           PUSH BC
230C CD1423       CALL 2314,STK-TO-A
230F 59           LD E,C
2310 C1           POP BC
2311 51           LD D,C
2312 4F           LD C,A
2313 C9           RET
2314 CDD52D       CALL 2DD5,FP-TO-A
2317 DAF924       JP C,24F9,REPORT-B
231A 0E01         LD C,01
231C C8           RET Z
231D 0EFF         LD C,FF
231F C9           RET
2320 DF           RST 18,GET-CHAR
2321 FE2C         CP 2C
2323 C28A1C       JP NZ,1C8A,REPORT-C
2326 E7           RST 20,NEXT-CHAR
2327 CD821C       CALL 1C82,EXPT-1NUM
232A CDEE1B       CALL 1BEE,CHECK-END
232D EF           RST 28,FP-CALC
232E 2A           DEFB 2A,abs
232F 3D           DEFB 3D,re-stack
2330 38           DEFB 38,end-calc
2331 7E           LD A,(HL)
2332 FE81         CP 81
2334 3005         JR NC,233B,C-R-GRE-1
2336 EF           RST 28,FP-CALC
2337 02           DEFB 02,delete
2338 38           DEFB 38,end-calc
2339 18A1         JR 22DC,PLOT
233B EF           RST 28,FP-CALC
233C A3           DEFB A3,stk-pi/2
233D 38           DEFB 38,end-calc
233E 3683         LD (HL),83
2340 EF           RST 28,FP-CALC
2341 C5           DEFB C5,st-mem-5
2342 02           DEFB 02,delete
2343 38           DEFB 38,end-calc
2344 CD7D24       CALL 247D,CD-PRMS1
2347 C5           PUSH BC
2348 EF           RST 28,FP-CALC
2349 31           DEFB 31,duplicate
234A E1           DEFB E1,get-mem-1
234B 04           DEFB 04,multiply
234C 38           DEFB 38,end-calc
234D 7E           LD A,(HL)
234E FE80         CP 80
2350 3008         JR NC,235A,C-ARC-GE1
2352 EF           RST 28,FP-CALC
2353 02           DEFB 02,delete
2354 02           DEFB 02,delete
2355 38           DEFB 38,end-calc
2356 C1           POP BC
2357 C3DC22       JP 22DC,PLOT
235A EF           RST 28,FP-CALC
235B C2           DEFB C2,st-mem-2
235C 01           DEFB 01,exchange
235D C0           DEFB C0,st-mem-0
235E 02           DEFB 02,delete
235F 03           DEFB 03,subtract
2360 01           DEFB 01,exchange
2361 E0           DEFB E0,get-mem-0
2362 0F           DEFB 0F,addition
2363 C0           DEFB C0,st-mem-0
2364 01           DEFB 01,exchange
2365 31           DEFB 31,duplicate
2366 E0           DEFB E0,get-mem-0
2367 01           DEFB 01,exchange
2368 31           DEFB 31,duplicate
2369 E0           DEFB E0,get-mem-0
236A A0           DEFB A0,stk-zero
236B C1           DEFB C1,st-mem-1
236C 02           DEFB 02,delete
236D 38           DEFB 38,end-calc
236E FD3462       INC (IY+62)
2371 CD941E       CALL 1E94,FIND-INT1
2374 6F           LD L,A
2375 E5           PUSH HL
2376 CD941E       CALL 1E94,FIND-INT1
2379 E1           POP HL
237A 67           LD H,A
237B 227D5C       LD (5C7D),HL  {COORDS}
237E C1           POP BC
237F C32024       JP 2420,DRW-STEPS
2382 DF           RST 18,GET-CHAR
2383 FE2C         CP 2C
2385 2806         JR Z,238D,DR-3-PRMS
2387 CDEE1B       CALL 1BEE,CHECK-END
238A C37724       JP 2477,LINE-DRAW
238D E7           RST 20,NEXT-CHAR
238E CD821C       CALL 1C82,EXPT-1NUM
2391 CDEE1B       CALL 1BEE,CHECK-END
2394 EF           RST 28,FP-CALC
2395 C5           DEFB C5,st-mem-5
2396 A2           DEFB A2,stk-half
2397 04           DEFB 04,multiply
2398 1F           DEFB 1F,sin
2399 31           DEFB 31,duplicate
239A 30           DEFB 30,not
239B 30           DEFB 30,not
239C 00           DEFB 00,jump-true
239D 06           DEFB 06,to-power
239E 02           DEFB 02,delete
239F 38           DEFB 38,end-calc
23A0 C37724       JP 2477,LINE-DRAW
23A3 C0           RET NZ
23A4 02           LD (BC),A
23A5 C1           POP BC
23A6 02           LD (BC),A
23A7 312AE1       LD SP,E12A
23AA 01E12A       LD BC,2AE1
23AD 0F           RRCA
23AE E0           RET PO
23AF 05           DEC B
23B0 2AE001       LD HL,(01E0)
23B3 3D           DEC A
23B4 387E         JR C,2434
23B6 FE81         CP 81
23B8 3007         JR NC,23C1,DR-PRMS
23BA EF           RST 28,FP-CALC
23BB 02           DEFB 02,delete
23BC 02           DEFB 02,delete
23BD 38           DEFB 38,end-calc
23BE C37724       JP 2477,LINE-DRAW
23C1 CD7D24       CALL 247D,CD-PRMS1
23C4 C5           PUSH BC
23C5 EF           RST 28,FP-CALC
23C6 02           DEFB 02,delete
23C7 E1           DEFB E1,get-mem-1
23C8 01           DEFB 01,exchange
23C9 05           DEFB 05,division
23CA C1           DEFB C1,st-mem-1
23CB 02           DEFB 02,delete
23CC 01           DEFB 01,exchange
23CD 31           DEFB 31,duplicate
23CE E1           DEFB E1,get-mem-1
23CF 04           DEFB 04,multiply
23D0 C2           DEFB C2,st-mem-2
23D1 02           DEFB 02,delete
23D2 01           DEFB 01,exchange
23D3 31           DEFB 31,duplicate
23D4 E1           DEFB E1,get-mem-1
23D5 04           DEFB 04,multiply
23D6 E2           DEFB E2,get-mem-2
23D7 E5           DEFB E5,get-mem-5
23D8 E0           DEFB E0,get-mem-0
23D9 03           DEFB 03,subtract
23DA A2           DEFB A2,stk-half
23DB 04           DEFB 04,multiply
23DC 31           DEFB 31,duplicate
23DD 1F           DEFB 1F,sin
23DE C5           DEFB C5,st-mem-5
23DF 02           DEFB 02,delete
23E0 20           DEFB 20,cos
23E1 C0           DEFB C0,st-mem-0
23E2 02           DEFB 02,delete
23E3 C2           DEFB C2,st-mem-2
23E4 02           DEFB 02,delete
23E5 C1           DEFB C1,st-mem-1
23E6 E5           DEFB E5,get-mem-5
23E7 04           DEFB 04,multiply
23E8 E0           DEFB E0,get-mem-0
23E9 E2           DEFB E2,get-mem-2
23EA 04           DEFB 04,multiply
23EB 0F           DEFB 0F,addition
23EC E1           DEFB E1,get-mem-1
23ED 01           DEFB 01,exchange
23EE C1           DEFB C1,st-mem-1
23EF 02           DEFB 02,delete
23F0 E0           DEFB E0,get-mem-0
23F1 04           DEFB 04,multiply
23F2 E2           DEFB E2,get-mem-2
23F3 E5           DEFB E5,get-mem-5
23F4 04           DEFB 04,multiply
23F5 03           DEFB 03,subtract
23F6 C2           DEFB C2,st-mem-2
23F7 2A           DEFB 2A,abs
23F8 E1           DEFB E1,get-mem-1
23F9 2A           DEFB 2A,abs
23FA 0F           DEFB 0F,addition
23FB 02           DEFB 02,delete
23FC 38           DEFB 38,end-calc
23FD 1A           LD A,(DE)
23FE FE81         CP 81
2400 C1           POP BC
2401 DA7724       JP C,2477,LINE-DRAW
2404 C5           PUSH BC
2405 EF           RST 28,FP-CALC
2406 01           DEFB 01,exchange
2407 38           DEFB 38,end-calc
2408 3A7D5C       LD A,(5C7D)  {COORDS}
240B CD282D       CALL 2D28,STACK-A
240E EF           RST 28,FP-CALC
240F C0           DEFB C0,st-mem-0
2410 0F           DEFB 0F,addition
2411 01           DEFB 01,exchange
2412 38           DEFB 38,end-calc
2413 3A7E5C       LD A,(5C7E)
2416 CD282D       CALL 2D28,STACK-A
2419 EF           RST 28,FP-CALC
241A C5           DEFB C5,st-mem-5
241B 0F           DEFB 0F,addition
241C E0           DEFB E0,get-mem-0
241D E5           DEFB E5,get-mem-5
241E 38           DEFB 38,end-calc
241F C1           POP BC
2420 05           DEC B
2421 283C         JR Z,245F,ARC-END
2423 1814         JR 2439,ARC-START
2425 EF           RST 28,FP-CALC
2426 E1           DEFB E1,get-mem-1
2427 31           DEFB 31,duplicate
2428 E3           DEFB E3,get-mem-3
2429 04           DEFB 04,multiply
242A E2           DEFB E2,get-mem-2
242B E4           DEFB E4,get-mem-4
242C 04           DEFB 04,multiply
242D 03           DEFB 03,subtract
242E C1           DEFB C1,st-mem-1
242F 02           DEFB 02,delete
2430 E4           DEFB E4,get-mem-4
2431 04           DEFB 04,multiply
2432 E2           DEFB E2,get-mem-2
2433 E3           DEFB E3,get-mem-3
2434 04           DEFB 04,multiply
2435 0F           DEFB 0F,addition
2436 C2           DEFB C2,st-mem-2
2437 02           DEFB 02,delete
2438 38           DEFB 38,end-calc
2439 C5           PUSH BC
243A EF           RST 28,FP-CALC
243B C0           DEFB C0,st-mem-0
243C 02           DEFB 02,delete
243D E1           DEFB E1,get-mem-1
243E 0F           DEFB 0F,addition
243F 31           DEFB 31,duplicate
2440 38           DEFB 38,end-calc
2441 3A7D5C       LD A,(5C7D)  {COORDS}
2444 CD282D       CALL 2D28,STACK-A
2447 EF           RST 28,FP-CALC
2448 03           DEFB 03,subtract
2449 E0           DEFB E0,get-mem-0
244A E2           DEFB E2,get-mem-2
244B 0F           DEFB 0F,addition
244C C0           DEFB C0,st-mem-0
244D 01           DEFB 01,exchange
244E E0           DEFB E0,get-mem-0
244F 38           DEFB 38,end-calc
2450 3A7E5C       LD A,(5C7E)
2453 CD282D       CALL 2D28,STACK-A
2456 EF           RST 28,FP-CALC
2457 03           DEFB 03,subtract
2458 38           DEFB 38,end-calc
2459 CDB724       CALL 24B7,DRAW-LINE
245C C1           POP BC
245D 10C6         DJNZ 2425,ARC-LOOP
245F EF           RST 28,FP-CALC
2460 02           DEFB 02,delete
2461 02           DEFB 02,delete
2462 01           DEFB 01,exchange
2463 38           DEFB 38,end-calc
2464 3A7D5C       LD A,(5C7D)  {COORDS}
2467 CD282D       CALL 2D28,STACK-A
246A EF           RST 28,FP-CALC
246B 03           DEFB 03,subtract
246C 01           DEFB 01,exchange
246D 38           DEFB 38,end-calc
246E 3A7E5C       LD A,(5C7E)
2471 CD282D       CALL 2D28,STACK-A
2474 EF           RST 28,FP-CALC
2475 03           DEFB 03,subtract
2476 38           DEFB 38,end-calc
2477 CDB724       CALL 24B7,DRAW-LINE
247A C34D0D       JP 0D4D,TEMPS
247D EF           RST 28,FP-CALC
247E 31           DEFB 31,duplicate
247F 28           DEFB 28,sqr
2480 34           DEFB 34,stk-data
2481 3200         DEFB 32,00        ;2
2483 01           DEFB 01,exchange
2484 05           DEFB 05,division
2485 E5           DEFB E5,get-mem-5
2486 01           DEFB 01,exchange
2487 05           DEFB 05,division
2488 2A           DEFB 2A,abs
2489 38           DEFB 38,end-calc
248A CDD52D       CALL 2DD5,FP-TO-A
248D 3806         JR C,2495,USE-252
248F E6FC         AND FC
2491 C604         ADD A,04
2493 3002         JR NC,2497,DRAW-SAVE
2495 3EFC         LD A,FC
2497 F5           PUSH AF
2498 CD282D       CALL 2D28,STACK-A
249B EF           RST 28,FP-CALC
249C E5           DEFB E5,get-mem-5
249D 01           DEFB 01,exchange
249E 05           DEFB 05,division
249F 31           DEFB 31,duplicate
24A0 1F           DEFB 1F,sin
24A1 C4           DEFB C4,st-mem-4
24A2 02           DEFB 02,delete
24A3 31           DEFB 31,duplicate
24A4 A2           DEFB A2,stk-half
24A5 04           DEFB 04,multiply
24A6 1F           DEFB 1F,sin
24A7 C1           DEFB C1,st-mem-1
24A8 01           DEFB 01,exchange
24A9 C0           DEFB C0,st-mem-0
24AA 02           DEFB 02,delete
24AB 31           DEFB 31,duplicate
24AC 04           DEFB 04,multiply
24AD 31           DEFB 31,duplicate
24AE 0F           DEFB 0F,addition
24AF A1           DEFB A1,stk-one
24B0 03           DEFB 03,subtract
24B1 1B           DEFB 1B,negate
24B2 C3           DEFB C3,st-mem-3
24B3 02           DEFB 02,delete
24B4 38           DEFB 38,end-calc
24B5 C1           POP BC
24B6 C9           RET
24B7 CD0723       CALL 2307,STK-TO-BC
24BA 79           LD A,C
24BB B8           CP B
24BC 3006         JR NC,24C4
24BE 69           LD L,C
24BF D5           PUSH DE
24C0 AF           XOR A
24C1 5F           LD E,A
24C2 1807         JR 24CB,DL-LARGER
24C4 B1           OR C
24C5 C8           RET Z
24C6 68           LD L,B
24C7 41           LD B,C
24C8 D5           PUSH DE
24C9 1600         LD D,00
24CB 60           LD H,B
24CC 78           LD A,B
24CD 1F           RRA
24CE 85           ADD A,L
24CF 3803         JR C,24D4,D-L-DIAG
24D1 BC           CP H
24D2 3807         JR C,24DB,D-L-HR-VT
24D4 94           SUB H
24D5 4F           LD C,A
24D6 D9           EXX
24D7 C1           POP BC
24D8 C5           PUSH BC
24D9 1804         JR 24DF,D-L-STEP
24DB 4F           LD C,A
24DC D5           PUSH DE
24DD D9           EXX
24DE C1           POP BC
24DF 2A7D5C       LD HL,(5C7D)  {COORDS}
24E2 78           LD A,B
24E3 84           ADD A,H
24E4 47           LD B,A
24E5 79           LD A,C
24E6 3C           INC A
24E7 85           ADD A,L
24E8 380D         JR C,24F7,D-L-RANGE
24EA 280D         JR Z,24F9,REPORT-B
24EC 3D           DEC A
24ED 4F           LD C,A
24EE CDE522       CALL 22E5,PLOT-SUB
24F1 D9           EXX
24F2 79           LD A,C
24F3 10D9         DJNZ 24CE,D-L-LOOP
24F5 D1           POP DE
24F6 C9           RET
24F7 28F3         JR Z,24EC,D-L-PLOT
24F9 CF           RST 08,ERROR-1
24FA 0A           DEFB 0A        ;B Integer out of range
24FB DF           RST 18,GET-CHAR
24FC 0600         LD B,00
24FE C5           PUSH BC
24FF 4F           LD C,A
2500 219625       LD HL,2596
2503 CDDC16       CALL 16DC,INDEXER
2506 79           LD A,C
2507 D28426       JP NC,2684,S-ALPHANUM
250A 0600         LD B,00
250C 4E           LD C,(HL)
250D 09           ADD HL,BC
250E E9           JP (HL)
250F CD7400       CALL 0074,CH-ADD+1
2512 03           INC BC
2513 FE0D         CP 0D
2515 CA8A1C       JP Z,1C8A,REPORT-C
2518 FE22         CP 22
251A 20F3         JR NZ,250F,S-QUOTE-S
251C CD7400       CALL 0074,CH-ADD+1
251F FE22         CP 22
2521 C9           RET
2522 E7           RST 20,NEXT-CHAR
2523 FE28         CP 28
2525 2006         JR NZ,252D,S-RPORT-C
2527 CD791C       CALL 1C79,NEXT-2NUM
252A DF           RST 18,GET-CHAR
252B FE29         CP 29
252D C28A1C       JP NZ,1C8A,REPORT-C
2530 FDCB017E     BIT 7,(IY+01)
2534 C9           RET
2535 CD0723       CALL 2307,STK-TO-BC
2538 2A365C       LD HL,(5C36)  {CHARS}
253B 110001       LD DE,0100
253E 19           ADD HL,DE
253F 79           LD A,C
2540 0F           RRCA
2541 0F           RRCA
2542 0F           RRCA
2543 E6E0         AND E0
2545 A8           XOR B
2546 5F           LD E,A
2547 79           LD A,C
2548 E618         AND 18
254A EE40         XOR 40
254C 57           LD D,A
254D 0660         LD B,60
254F C5           PUSH BC
2550 D5           PUSH DE
2551 E5           PUSH HL
2552 1A           LD A,(DE)
2553 AE           XOR (HL)
2554 2804         JR Z,255A,S-SC-MTCH
2556 3C           INC A
2557 201A         JR NZ,2573,S-SCR-NXT
2559 3D           DEC A
255A 4F           LD C,A
255B 0607         LD B,07
255D 14           INC D
255E 23           INC HL
255F 1A           LD A,(DE)
2560 AE           XOR (HL)
2561 A9           XOR C
2562 200F         JR NZ,2573,S-SCR-NXT
2564 10F7         DJNZ 255D,S-SC-ROWS
2566 C1           POP BC
2567 C1           POP BC
2568 C1           POP BC
2569 3E80         LD A,80
256B 90           SUB B
256C 010100       LD BC,0001
256F F7           RST 30,BC-SPACES
2570 12           LD (DE),A
2571 180A         JR 257D,S-SCR-STO
2573 E1           POP HL
2574 110800       LD DE,0008
2577 19           ADD HL,DE
2578 D1           POP DE
2579 C1           POP BC
257A 10D3         DJNZ 254F,S-SCRN-LP
257C 48           LD C,B
257D C3B22A       JP 2AB2,STK-STO-$
2580 CD0723       CALL 2307,STK-TO-BC
2583 79           LD A,C
2584 0F           RRCA
2585 0F           RRCA
2586 0F           RRCA
2587 4F           LD C,A
2588 E6E0         AND E0
258A A8           XOR B
258B 6F           LD L,A
258C 79           LD A,C
258D E603         AND 03
258F EE58         XOR 58
2591 67           LD H,A
2592 7E           LD A,(HL)
2593 C3282D       JP 2D28,STACK-A
2596 221C28       LD (281C),HL
2599 4F           LD C,A
259A 2EF2         LD L,F2
259C 2B           DEC HL
259D 12           LD (DE),A
259E A8           XOR B
259F 56           LD D,(HL)
25A0 A5           AND L
25A1 57           LD D,A
25A2 A7           AND A
25A3 84           ADD A,H
25A4 A6           AND (HL)
25A5 8F           ADC A,A
25A6 C4E6AA       CALL NZ,AAE6
25A9 BF           CP A
25AA AB           XOR E
25AB C7           RST 00,START
25AC A9           XOR C
25AD CE00         ADC A,00
25AF E7           RST 20,NEXT-CHAR
25B0 C3FF24       JP 24FF,S-LOOP-1
25B3 DF           RST 18,GET-CHAR
25B4 23           INC HL
25B5 E5           PUSH HL
25B6 010000       LD BC,0000
25B9 CD0F25       CALL 250F,S-QUOTE-S
25BC 201B         JR NZ,25D9,S-Q-PRMS
25BE CD0F25       CALL 250F,S-QUOTE-S
25C1 28FB         JR Z,25BE,S-Q-AGAIN
25C3 CD3025       CALL 2530,SYNTAX-Z
25C6 2811         JR Z,25D9,S-Q-PRMS
25C8 F7           RST 30,BC-SPACES
25C9 E1           POP HL
25CA D5           PUSH DE
25CB 7E           LD A,(HL)
25CC 23           INC HL
25CD 12           LD (DE),A
25CE 13           INC DE
25CF FE22         CP 22
25D1 20F8         JR NZ,25CB,S-Q-COPY
25D3 7E           LD A,(HL)
25D4 23           INC HL
25D5 FE22         CP 22
25D7 28F2         JR Z,25CB,S-Q-COPY
25D9 0B           DEC BC
25DA D1           POP DE
25DB 213B5C       LD HL,5C3B
25DE CBB6         RES 6,(HL)
25E0 CB7E         BIT 7,(HL)
25E2 C4B22A       CALL NZ,2AB2,STK-STO-$
25E5 C31227       JP 2712,S-CONT-2
25E8 E7           RST 20,NEXT-CHAR
25E9 CDFB24       CALL 24FB,SCANNING
25EC FE29         CP 29
25EE C28A1C       JP NZ,1C8A,REPORT-C
25F1 E7           RST 20,NEXT-CHAR
25F2 C31227       JP 2712,S-CONT-2
25F5 C3BD27       JP 27BD,S-FN-SBRN
25F8 CD3025       CALL 2530,SYNTAX-Z
25FB 2828         JR Z,2625,S-RND-END
25FD ED4B765C     LD BC,(5C76)  {SEED}
2601 CD2B2D       CALL 2D2B,STACK-BC
2604 EF           RST 28,FP-CALC
2605 A1           DEFB A1,stk-one
2606 0F           DEFB 0F,addition
2607 34           DEFB 34,stk-data
2608 3716         DEFB 37,16        ;4B
260A 04           DEFB 04,multiply
260B 34           DEFB 34,stk-data
260C 8041000080   DEFB 80,41,00,00,80        ;10001
2611 32           DEFB 32,n-mod-m
2612 02           DEFB 02,delete
2613 A1           DEFB A1,stk-one
2614 03           DEFB 03,subtract
2615 31           DEFB 31,duplicate
2616 38           DEFB 38,end-calc
2617 CDA22D       CALL 2DA2,FP-TO-BC
261A ED43765C     LD (5C76),BC  {SEED}
261E 7E           LD A,(HL)
261F A7           AND A
2620 2803         JR Z,2625,S-RND-END
2622 D610         SUB 10
2624 77           LD (HL),A
2625 1809         JR 2630,S-PI-END
2627 CD3025       CALL 2530,SYNTAX-Z
262A 2804         JR Z,2630,S-PI-END
262C EF           RST 28,FP-CALC
262D A3           DEFB A3,stk-pi/2
262E 38           DEFB 38,end-calc
262F 34           INC (HL)
2630 E7           RST 20,NEXT-CHAR
2631 C3C326       JP 26C3,S-NUMERIC
2634 015A10       LD BC,105A
2637 E7           RST 20,NEXT-CHAR
2638 FE23         CP 23
263A CA0D27       JP Z,270D,S-PUSH-PO
263D 213B5C       LD HL,5C3B
2640 CBB6         RES 6,(HL)
2642 CB7E         BIT 7,(HL)
2644 281F         JR Z,2665,S-INK$-EN
2646 CD8E02       CALL 028E,KEY-SCAN
2649 0E00         LD C,00
264B 2013         JR NZ,2660,S-IK$-STK
264D CD1E03       CALL 031E,K-TEST
2650 300E         JR NC,2660,S-IK$-STK
2652 15           DEC D
2653 5F           LD E,A
2654 CD3303       CALL 0333,K-DECODE
2657 F5           PUSH AF
2658 010100       LD BC,0001
265B F7           RST 30,BC-SPACES
265C F1           POP AF
265D 12           LD (DE),A
265E 0E01         LD C,01
2660 0600         LD B,00
2662 CDB22A       CALL 2AB2,STK-STO-$
2665 C31227       JP 2712,S-CONT-2
2668 CD2225       CALL 2522,S-2-COORD
266B C43525       CALL NZ,2535,S-SCRN$-S
266E E7           RST 20,NEXT-CHAR
266F C3DB25       JP 25DB,S-STRING
2672 CD2225       CALL 2522,S-2-COORD
2675 C48025       CALL NZ,2580,S-ATTR-S
2678 E7           RST 20,NEXT-CHAR
2679 1848         JR 26C3,S-NUMERIC
267B CD2225       CALL 2522,S-2-COORD
267E C4CB22       CALL NZ,22CB,POINT-SUB
2681 E7           RST 20,NEXT-CHAR
2682 183F         JR 26C3,S-NUMERIC
2684 CD882C       CALL 2C88,ALPHANUM
2687 3056         JR NC,26DF,S-NEGATE
2689 FE41         CP 41
268B 303C         JR NC,26C9,S-LETTER
268D CD3025       CALL 2530,SYNTAX-Z
2690 2023         JR NZ,26B5,S-STK-DEC
2692 CD9B2C       CALL 2C9B,DEC-TO-FP
2695 DF           RST 18,GET-CHAR
2696 010600       LD BC,0006
2699 CD5516       CALL 1655,MAKE-ROOM
269C 23           INC HL
269D 360E         LD (HL),0E
269F 23           INC HL
26A0 EB           EX DE,HL
26A1 2A655C       LD HL,(5C65)  {STKEND}
26A4 0E05         LD C,05
26A6 A7           AND A
26A7 ED42         SBC HL,BC
26A9 22655C       LD (5C65),HL  {STKEND}
26AC EDB0         LDIR
26AE EB           EX DE,HL
26AF 2B           DEC HL
26B0 CD7700       CALL 0077,TEMP-PTR1
26B3 180E         JR 26C3,S-NUMERIC
26B5 DF           RST 18,GET-CHAR
26B6 23           INC HL
26B7 7E           LD A,(HL)
26B8 FE0E         CP 0E
26BA 20FA         JR NZ,26B6,S-SD-SKIP
26BC 23           INC HL
26BD CDB433       CALL 33B4,STACK-NUM
26C0 225D5C       LD (5C5D),HL  {CH-ADD}
26C3 FDCB01F6     SET 6,(IY+01)
26C7 1814         JR 26DD,S-CONT-1
26C9 CDB228       CALL 28B2,LOOK-VARS
26CC DA2E1C       JP C,1C2E,REPORT-2
26CF CC9629       CALL Z,2996,STK-VAR
26D2 3A3B5C       LD A,(5C3B)  {FLAGS}
26D5 FEC0         CP C0
26D7 3804         JR C,26DD,S-CONT-1
26D9 23           INC HL
26DA CDB433       CALL 33B4,STACK-NUM
26DD 1833         JR 2712,S-CONT-2
26DF 01DB09       LD BC,09DB
26E2 FE2D         CP 2D
26E4 2827         JR Z,270D,S-PUSH-PO
26E6 011810       LD BC,1018
26E9 FEAE         CP AE
26EB 2820         JR Z,270D,S-PUSH-PO
26ED D6AF         SUB AF
26EF DA8A1C       JP C,1C8A,REPORT-C
26F2 01F004       LD BC,04F0
26F5 FE14         CP 14
26F7 2814         JR Z,270D,S-PUSH-PO
26F9 D28A1C       JP NC,1C8A,REPORT-C
26FC 0610         LD B,10
26FE C6DC         ADD A,DC
2700 4F           LD C,A
2701 FEDF         CP DF
2703 3002         JR NC,2707,S-NO-TO-$
2705 CBB1         RES 6,C
2707 FEEE         CP EE
2709 3802         JR C,270D,S-PUSH-PO
270B CBB9         RES 7,C
270D C5           PUSH BC
270E E7           RST 20,NEXT-CHAR
270F C3FF24       JP 24FF,S-LOOP-1
2712 DF           RST 18,GET-CHAR
2713 FE28         CP 28
2715 200C         JR NZ,2723,S-OPERTR
2717 FDCB0176     BIT 6,(IY+01)
271B 2017         JR NZ,2734,S-LOOP
271D CD522A       CALL 2A52,SLICING
2720 E7           RST 20,NEXT-CHAR
2721 18F0         JR 2713,S-CONT-3
2723 0600         LD B,00
2725 4F           LD C,A
2726 219527       LD HL,2795
2729 CDDC16       CALL 16DC,INDEXER
272C 3006         JR NC,2734,S-LOOP
272E 4E           LD C,(HL)
272F 21ED26       LD HL,26ED
2732 09           ADD HL,BC
2733 46           LD B,(HL)
2734 D1           POP DE
2735 7A           LD A,D
2736 B8           CP B
2737 383A         JR C,2773,S-TIGHTER
2739 A7           AND A
273A CA1800       JP Z,0018,GET-CHAR
273D C5           PUSH BC
273E 213B5C       LD HL,5C3B
2741 7B           LD A,E
2742 FEED         CP ED
2744 2006         JR NZ,274C,S-STK-LST
2746 CB76         BIT 6,(HL)
2748 2002         JR NZ,274C,S-STK-LST
274A 1E99         LD E,99
274C D5           PUSH DE
274D CD3025       CALL 2530,SYNTAX-Z
2750 2809         JR Z,275B,S-SYNTEST
2752 7B           LD A,E
2753 E63F         AND 3F
2755 47           LD B,A
2756 EF           RST 28,FP-CALC
2757 3B           DEFB 3B,fp-calc-2
2758 38           DEFB 38,end-calc
2759 1809         JR 2764,S-RUNTEST
275B 7B           LD A,E
275C FDAE01       XOR (IY+01)
275F E640         AND 40
2761 C28A1C       JP NZ,1C8A,REPORT-C
2764 D1           POP DE
2765 213B5C       LD HL,5C3B
2768 CBF6         SET 6,(HL)
276A CB7B         BIT 7,E
276C 2002         JR NZ,2770,S-LOOPEND
276E CBB6         RES 6,(HL)
2770 C1           POP BC
2771 18C1         JR 2734,S-LOOP
2773 D5           PUSH DE
2774 79           LD A,C
2775 FDCB0176     BIT 6,(IY+01)
2779 2015         JR NZ,2790,S-NEXT
277B E63F         AND 3F
277D C608         ADD A,08
277F 4F           LD C,A
2780 FE10         CP 10
2782 2004         JR NZ,2788,S-NOT-AND
2784 CBF1         SET 6,C
2786 1808         JR 2790,S-NEXT
2788 38D7         JR C,2761,S-RPORT-C
278A FE17         CP 17
278C 2802         JR Z,2790,S-NEXT
278E CBF9         SET 7,C
2790 C5           PUSH BC
2791 E7           RST 20,NEXT-CHAR
2792 C3FF24       JP 24FF,S-LOOP-1
2795 2B           DEC HL
2796 CF           RST 08,ERROR-1
2797 2D           DEFB 2D        ;unknown
2798 C32AC4       JP C42A
279B 2F           CPL
279C C5           PUSH BC
279D 5E           LD E,(HL)
279E C63D         ADD A,3D
27A0 CE3E         ADC A,3E
27A2 CC3CCD       CALL Z,CD3C
27A5 C7           RST 00,START
27A6 C9           RET
27A7 C8           RET Z
27A8 CAC9CB       JP Z,CBC9
27AB C5           PUSH BC
27AC C7           RST 00,START
27AD C6C8         ADD A,C8
27AF 00           NOP
27B0 0608         LD B,08
27B2 08           EX AF,AF'
27B3 0A           LD A,(BC)
27B4 02           LD (BC),A
27B5 03           INC BC
27B6 05           DEC B
27B7 05           DEC B
27B8 05           DEC B
27B9 05           DEC B
27BA 05           DEC B
27BB 05           DEC B
27BC 06CD         LD B,CD
27BE 3025         JR NC,27E5
27C0 2035         JR NZ,27F7,SF-RUN
27C2 E7           RST 20,NEXT-CHAR
27C3 CD8D2C       CALL 2C8D,ALPHA
27C6 D28A1C       JP NC,1C8A,REPORT-C
27C9 E7           RST 20,NEXT-CHAR
27CA FE24         CP 24
27CC F5           PUSH AF
27CD 2001         JR NZ,27D0,SF-BRKT-1
27CF E7           RST 20,NEXT-CHAR
27D0 FE28         CP 28
27D2 2012         JR NZ,27E6,SF-RPRT-C
27D4 E7           RST 20,NEXT-CHAR
27D5 FE29         CP 29
27D7 2810         JR Z,27E9,SF-FLAG-6
27D9 CDFB24       CALL 24FB,SCANNING
27DC DF           RST 18,GET-CHAR
27DD FE2C         CP 2C
27DF 2003         JR NZ,27E4,SF-BRKT-2
27E1 E7           RST 20,NEXT-CHAR
27E2 18F5         JR 27D9,SF-ARGMTS
27E4 FE29         CP 29
27E6 C28A1C       JP NZ,1C8A,REPORT-C
27E9 E7           RST 20,NEXT-CHAR
27EA 213B5C       LD HL,5C3B
27ED CBB6         RES 6,(HL)
27EF F1           POP AF
27F0 2802         JR Z,27F4,SF-SYN-EN
27F2 CBF6         SET 6,(HL)
27F4 C31227       JP 2712,S-CONT-2
27F7 E7           RST 20,NEXT-CHAR
27F8 E6DF         AND DF
27FA 47           LD B,A
27FB E7           RST 20,NEXT-CHAR
27FC D624         SUB 24
27FE 4F           LD C,A
27FF 2001         JR NZ,2802,SF-ARGMT1
2801 E7           RST 20,NEXT-CHAR
2802 E7           RST 20,NEXT-CHAR
2803 E5           PUSH HL
2804 2A535C       LD HL,(5C53)  {PROG}
2807 2B           DEC HL
2808 11CE00       LD DE,00CE
280B C5           PUSH BC
280C CD861D       CALL 1D86,LOOK-PROG
280F C1           POP BC
2810 3002         JR NC,2814,SF-CP-DEF
2812 CF           RST 08,ERROR-1
2813 18           DEFB 18        ;P FN without DEF
2814 E5           PUSH HL
2815 CDAB28       CALL 28AB,FN-SKPOVR
2818 E6DF         AND DF
281A B8           CP B
281B 2008         JR NZ,2825,SF-NOT-FD
281D CDAB28       CALL 28AB,FN-SKPOVR
2820 D624         SUB 24
2822 B9           CP C
2823 280C         JR Z,2831,SF-VALUES
2825 E1           POP HL
2826 2B           DEC HL
2827 110002       LD DE,0200
282A C5           PUSH BC
282B CD8B19       CALL 198B,EACH-STMT
282E C1           POP BC
282F 18D7         JR 2808,SF-FND-DF
2831 A7           AND A
2832 CCAB28       CALL Z,28AB,FN-SKPOVR
2835 D1           POP DE
2836 D1           POP DE
2837 ED535D5C     LD (5C5D),DE  {CH-ADD}
283B CDAB28       CALL 28AB,FN-SKPOVR
283E E5           PUSH HL
283F FE29         CP 29
2841 2842         JR Z,2885,SF-R-BR-2
2843 23           INC HL
2844 7E           LD A,(HL)
2845 FE0E         CP 0E
2847 1640         LD D,40
2849 2807         JR Z,2852,SF-ARG-VL
284B 2B           DEC HL
284C CDAB28       CALL 28AB,FN-SKPOVR
284F 23           INC HL
2850 1600         LD D,00
2852 23           INC HL
2853 E5           PUSH HL
2854 D5           PUSH DE
2855 CDFB24       CALL 24FB,SCANNING
2858 F1           POP AF
2859 FDAE01       XOR (IY+01)
285C E640         AND 40
285E 202B         JR NZ,288B,REPORT-Q
2860 E1           POP HL
2861 EB           EX DE,HL
2862 2A655C       LD HL,(5C65)  {STKEND}
2865 010500       LD BC,0005
2868 ED42         SBC HL,BC
286A 22655C       LD (5C65),HL  {STKEND}
286D EDB0         LDIR
286F EB           EX DE,HL
2870 2B           DEC HL
2871 CDAB28       CALL 28AB,FN-SKPOVR
2874 FE29         CP 29
2876 280D         JR Z,2885,SF-R-BR-2
2878 E5           PUSH HL
2879 DF           RST 18,GET-CHAR
287A FE2C         CP 2C
287C 200D         JR NZ,288B,REPORT-Q
287E E7           RST 20,NEXT-CHAR
287F E1           POP HL
2880 CDAB28       CALL 28AB,FN-SKPOVR
2883 18BE         JR 2843,SF-ARG-LP
2885 E5           PUSH HL
2886 DF           RST 18,GET-CHAR
2887 FE29         CP 29
2889 2802         JR Z,288D,SF-VALUE
288B CF           RST 08,ERROR-1
288C 19           DEFB 19        ;Q Parameter error
288D D1           POP DE
288E EB           EX DE,HL
288F 225D5C       LD (5C5D),HL  {CH-ADD}
2892 2A0B5C       LD HL,(5C0B)  {DEFADD}
2895 E3           EX SP,(HL)
2896 220B5C       LD (5C0B),HL  {DEFADD}
2899 D5           PUSH DE
289A E7           RST 20,NEXT-CHAR
289B E7           RST 20,NEXT-CHAR
289C CDFB24       CALL 24FB,SCANNING
289F E1           POP HL
28A0 225D5C       LD (5C5D),HL  {CH-ADD}
28A3 E1           POP HL
28A4 220B5C       LD (5C0B),HL  {DEFADD}
28A7 E7           RST 20,NEXT-CHAR
28A8 C31227       JP 2712,S-CONT-2
28AB 23           INC HL
28AC 7E           LD A,(HL)
28AD FE21         CP 21
28AF 38FA         JR C,28AB,FN-SKPOVR
28B1 C9           RET
28B2 FDCB01F6     SET 6,(IY+01)
28B6 DF           RST 18,GET-CHAR
28B7 CD8D2C       CALL 2C8D,ALPHA
28BA D28A1C       JP NC,1C8A,REPORT-C
28BD E5           PUSH HL
28BE E61F         AND 1F
28C0 4F           LD C,A
28C1 E7           RST 20,NEXT-CHAR
28C2 E5           PUSH HL
28C3 FE28         CP 28
28C5 2828         JR Z,28EF,V-RUN/SYN
28C7 CBF1         SET 6,C
28C9 FE24         CP 24
28CB 2811         JR Z,28DE,V-STR-VAR
28CD CBE9         SET 5,C
28CF CD882C       CALL 2C88,ALPHANUM
28D2 300F         JR NC,28E3,V-TEST-FN
28D4 CD882C       CALL 2C88,ALPHANUM
28D7 3016         JR NC,28EF,V-RUN/SYN
28D9 CBB1         RES 6,C
28DB E7           RST 20,NEXT-CHAR
28DC 18F6         JR 28D4,V-CHAR
28DE E7           RST 20,NEXT-CHAR
28DF FDCB01B6     RES 6,(IY+01)
28E3 3A0C5C       LD A,(5C0C)
28E6 A7           AND A
28E7 2806         JR Z,28EF,V-RUN/SYN
28E9 CD3025       CALL 2530,SYNTAX-Z
28EC C25129       JP NZ,2951,STK-F-ARG
28EF 41           LD B,C
28F0 CD3025       CALL 2530,SYNTAX-Z
28F3 2008         JR NZ,28FD,V-RUN
28F5 79           LD A,C
28F6 E6E0         AND E0
28F8 CBFF         SET 7,A
28FA 4F           LD C,A
28FB 1837         JR 2934,V-SYNTAX
28FD 2A4B5C       LD HL,(5C4B)  {VARS}
2900 7E           LD A,(HL)
2901 E67F         AND 7F
2903 282D         JR Z,2932,V-80-BYTE
2905 B9           CP C
2906 2022         JR NZ,292A,V-NEXT
2908 17           RLA
2909 87           ADD A,A
290A F23F29       JP P,293F,V-FOUND-2
290D 3830         JR C,293F,V-FOUND-2
290F D1           POP DE
2910 D5           PUSH DE
2911 E5           PUSH HL
2912 23           INC HL
2913 1A           LD A,(DE)
2914 13           INC DE
2915 FE20         CP 20
2917 28FA         JR Z,2913,V-SPACES
2919 F620         OR 20
291B BE           CP (HL)
291C 28F4         JR Z,2912,V-MATCHES
291E F680         OR 80
2920 BE           CP (HL)
2921 2006         JR NZ,2929,V-GET-PTR
2923 1A           LD A,(DE)
2924 CD882C       CALL 2C88,ALPHANUM
2927 3015         JR NC,293E,V-FOUND-1
2929 E1           POP HL
292A C5           PUSH BC
292B CDB819       CALL 19B8,NEXT-ONE
292E EB           EX DE,HL
292F C1           POP BC
2930 18CE         JR 2900,V-EACH
2932 CBF8         SET 7,B
2934 D1           POP DE
2935 DF           RST 18,GET-CHAR
2936 FE28         CP 28
2938 2809         JR Z,2943,V-PASS
293A CBE8         SET 5,B
293C 180D         JR 294B
293E D1           POP DE
293F D1           POP DE
2940 D1           POP DE
2941 E5           PUSH HL
2942 DF           RST 18,GET-CHAR
2943 CD882C       CALL 2C88,ALPHANUM
2946 3003         JR NC,294B
2948 E7           RST 20,NEXT-CHAR
2949 18F8         JR 2943,V-PASS
294B E1           POP HL
294C CB10         RL B
294E CB70         BIT 6,B
2950 C9           RET
2951 2A0B5C       LD HL,(5C0B)  {DEFADD}
2954 7E           LD A,(HL)
2955 FE29         CP 29
2957 CAEF28       JP Z,28EF,V-RUN/SYN
295A 7E           LD A,(HL)
295B F660         OR 60
295D 47           LD B,A
295E 23           INC HL
295F 7E           LD A,(HL)
2960 FE0E         CP 0E
2962 2807         JR Z,296B,SFA-CP-VR
2964 2B           DEC HL
2965 CDAB28       CALL 28AB,FN-SKPOVR
2968 23           INC HL
2969 CBA8         RES 5,B
296B 78           LD A,B
296C B9           CP C
296D 2812         JR Z,2981,SFA-MATCH
296F 23           INC HL
2970 23           INC HL
2971 23           INC HL
2972 23           INC HL
2973 23           INC HL
2974 CDAB28       CALL 28AB,FN-SKPOVR
2977 FE29         CP 29
2979 CAEF28       JP Z,28EF,V-RUN/SYN
297C CDAB28       CALL 28AB,FN-SKPOVR
297F 18D9         JR 295A,SFA-LOOP
2981 CB69         BIT 5,C
2983 200C         JR NZ,2991,SFA-END
2985 23           INC HL
2986 ED5B655C     LD DE,(5C65)  {STKEND}
298A CDC033       CALL 33C0,MOVE-FP
298D EB           EX DE,HL
298E 22655C       LD (5C65),HL  {STKEND}
2991 D1           POP DE
2992 D1           POP DE
2993 AF           XOR A
2994 3C           INC A
2995 C9           RET
2996 AF           XOR A
2997 47           LD B,A
2998 CB79         BIT 7,C
299A 204B         JR NZ,29E7,SV-COUNT
299C CB7E         BIT 7,(HL)
299E 200E         JR NZ,29AE,SV-ARRAYS
29A0 3C           INC A
29A1 23           INC HL
29A2 4E           LD C,(HL)
29A3 23           INC HL
29A4 46           LD B,(HL)
29A5 23           INC HL
29A6 EB           EX DE,HL
29A7 CDB22A       CALL 2AB2,STK-STO-$
29AA DF           RST 18,GET-CHAR
29AB C3492A       JP 2A49,SV-SLICE?
29AE 23           INC HL
29AF 23           INC HL
29B0 23           INC HL
29B1 46           LD B,(HL)
29B2 CB71         BIT 6,C
29B4 280A         JR Z,29C0,SV-PTR
29B6 05           DEC B
29B7 28E8         JR Z,29A1,SV-SIMPLE$
29B9 EB           EX DE,HL
29BA DF           RST 18,GET-CHAR
29BB FE28         CP 28
29BD 2061         JR NZ,2A20,REPORT-3
29BF EB           EX DE,HL
29C0 EB           EX DE,HL
29C1 1824         JR 29E7,SV-COUNT
29C3 E5           PUSH HL
29C4 DF           RST 18,GET-CHAR
29C5 E1           POP HL
29C6 FE2C         CP 2C
29C8 2820         JR Z,29EA,SV-LOOP
29CA CB79         BIT 7,C
29CC 2852         JR Z,2A20,REPORT-3
29CE CB71         BIT 6,C
29D0 2006         JR NZ,29D8,SV-CLOSE
29D2 FE29         CP 29
29D4 203C         JR NZ,2A12,SV-RPT-C
29D6 E7           RST 20,NEXT-CHAR
29D7 C9           RET
29D8 FE29         CP 29
29DA 286C         JR Z,2A48,SV-DIM
29DC FECC         CP CC
29DE 2032         JR NZ,2A12,SV-RPT-C
29E0 DF           RST 18,GET-CHAR
29E1 2B           DEC HL
29E2 225D5C       LD (5C5D),HL  {CH-ADD}
29E5 185E         JR 2A45,SV-SLICE
29E7 210000       LD HL,0000
29EA E5           PUSH HL
29EB E7           RST 20,NEXT-CHAR
29EC E1           POP HL
29ED 79           LD A,C
29EE FEC0         CP C0
29F0 2009         JR NZ,29FB
29F2 DF           RST 18,GET-CHAR
29F3 FE29         CP 29
29F5 2851         JR Z,2A48,SV-DIM
29F7 FECC         CP CC
29F9 28E5         JR Z,29E0,SV-CH-ADD
29FB C5           PUSH BC
29FC E5           PUSH HL
29FD CDEE2A       CALL 2AEE,DE,(DE+1)
2A00 E3           EX SP,(HL)
2A01 EB           EX DE,HL
2A02 CDCC2A       CALL 2ACC,INT-EXP1
2A05 3819         JR C,2A20,REPORT-3
2A07 0B           DEC BC
2A08 CDF42A       CALL 2AF4,GET-HL*DE
2A0B 09           ADD HL,BC
2A0C D1           POP DE
2A0D C1           POP BC
2A0E 10B3         DJNZ 29C3,SV-COMMA
2A10 CB79         BIT 7,C
2A12 2066         JR NZ,2A7A,SL-RPT-C
2A14 E5           PUSH HL
2A15 CB71         BIT 6,C
2A17 2013         JR NZ,2A2C,SV-ELEM$
2A19 42           LD B,D
2A1A 4B           LD C,E
2A1B DF           RST 18,GET-CHAR
2A1C FE29         CP 29
2A1E 2802         JR Z,2A22,SV-NUMBER
2A20 CF           RST 08,ERROR-1
2A21 02           DEFB 02        ;3 Subscript wrong
2A22 E7           RST 20,NEXT-CHAR
2A23 E1           POP HL
2A24 110500       LD DE,0005
2A27 CDF42A       CALL 2AF4,GET-HL*DE
2A2A 09           ADD HL,BC
2A2B C9           RET
2A2C CDEE2A       CALL 2AEE,DE,(DE+1)
2A2F E3           EX SP,(HL)
2A30 CDF42A       CALL 2AF4,GET-HL*DE
2A33 C1           POP BC
2A34 09           ADD HL,BC
2A35 23           INC HL
2A36 42           LD B,D
2A37 4B           LD C,E
2A38 EB           EX DE,HL
2A39 CDB12A       CALL 2AB1,STK-ST-0
2A3C DF           RST 18,GET-CHAR
2A3D FE29         CP 29
2A3F 2807         JR Z,2A48,SV-DIM
2A41 FE2C         CP 2C
2A43 20DB         JR NZ,2A20,REPORT-3
2A45 CD522A       CALL 2A52,SLICING
2A48 E7           RST 20,NEXT-CHAR
2A49 FE28         CP 28
2A4B 28F8         JR Z,2A45,SV-SLICE
2A4D FDCB01B6     RES 6,(IY+01)
2A51 C9           RET
2A52 CD3025       CALL 2530,SYNTAX-Z
2A55 C4F12B       CALL NZ,2BF1,STK-FETCH
2A58 E7           RST 20,NEXT-CHAR
2A59 FE29         CP 29
2A5B 2850         JR Z,2AAD,SL-STORE
2A5D D5           PUSH DE
2A5E AF           XOR A
2A5F F5           PUSH AF
2A60 C5           PUSH BC
2A61 110100       LD DE,0001
2A64 DF           RST 18,GET-CHAR
2A65 E1           POP HL
2A66 FECC         CP CC
2A68 2817         JR Z,2A81,SL-SECOND
2A6A F1           POP AF
2A6B CDCD2A       CALL 2ACD,INT-EXP2
2A6E F5           PUSH AF
2A6F 50           LD D,B
2A70 59           LD E,C
2A71 E5           PUSH HL
2A72 DF           RST 18,GET-CHAR
2A73 E1           POP HL
2A74 FECC         CP CC
2A76 2809         JR Z,2A81,SL-SECOND
2A78 FE29         CP 29
2A7A C28A1C       JP NZ,1C8A,REPORT-C
2A7D 62           LD H,D
2A7E 6B           LD L,E
2A7F 1813         JR 2A94,SL-DEFINE
2A81 E5           PUSH HL
2A82 E7           RST 20,NEXT-CHAR
2A83 E1           POP HL
2A84 FE29         CP 29
2A86 280C         JR Z,2A94,SL-DEFINE
2A88 F1           POP AF
2A89 CDCD2A       CALL 2ACD,INT-EXP2
2A8C F5           PUSH AF
2A8D DF           RST 18,GET-CHAR
2A8E 60           LD H,B
2A8F 69           LD L,C
2A90 FE29         CP 29
2A92 20E6         JR NZ,2A7A,SL-RPT-C
2A94 F1           POP AF
2A95 E3           EX SP,(HL)
2A96 19           ADD HL,DE
2A97 2B           DEC HL
2A98 E3           EX SP,(HL)
2A99 A7           AND A
2A9A ED52         SBC HL,DE
2A9C 010000       LD BC,0000
2A9F 3807         JR C,2AA8,SL-OVER
2AA1 23           INC HL
2AA2 A7           AND A
2AA3 FA202A       JP M,2A20,REPORT-3
2AA6 44           LD B,H
2AA7 4D           LD C,L
2AA8 D1           POP DE
2AA9 FDCB01B6     RES 6,(IY+01)
2AAD CD3025       CALL 2530,SYNTAX-Z
2AB0 C8           RET Z
2AB1 AF           XOR A
2AB2 FDCB01B6     RES 6,(IY+01)
2AB6 C5           PUSH BC
2AB7 CDA933       CALL 33A9,TEST-5-SP
2ABA C1           POP BC
2ABB 2A655C       LD HL,(5C65)  {STKEND}
2ABE 77           LD (HL),A
2ABF 23           INC HL
2AC0 73           LD (HL),E
2AC1 23           INC HL
2AC2 72           LD (HL),D
2AC3 23           INC HL
2AC4 71           LD (HL),C
2AC5 23           INC HL
2AC6 70           LD (HL),B
2AC7 23           INC HL
2AC8 22655C       LD (5C65),HL  {STKEND}
2ACB C9           RET
2ACC AF           XOR A
2ACD D5           PUSH DE
2ACE E5           PUSH HL
2ACF F5           PUSH AF
2AD0 CD821C       CALL 1C82,EXPT-1NUM
2AD3 F1           POP AF
2AD4 CD3025       CALL 2530,SYNTAX-Z
2AD7 2812         JR Z,2AEB,I-RESTORE
2AD9 F5           PUSH AF
2ADA CD991E       CALL 1E99,FIND-INT2
2ADD D1           POP DE
2ADE 78           LD A,B
2ADF B1           OR C
2AE0 37           SCF
2AE1 2805         JR Z,2AE8,I-CARRY
2AE3 E1           POP HL
2AE4 E5           PUSH HL
2AE5 A7           AND A
2AE6 ED42         SBC HL,BC
2AE8 7A           LD A,D
2AE9 DE00         SBC A,00
2AEB E1           POP HL
2AEC D1           POP DE
2AED C9           RET
2AEE EB           EX DE,HL
2AEF 23           INC HL
2AF0 5E           LD E,(HL)
2AF1 23           INC HL
2AF2 56           LD D,(HL)
2AF3 C9           RET
2AF4 CD3025       CALL 2530,SYNTAX-Z
2AF7 C8           RET Z
2AF8 CDA930       CALL 30A9,HL=HL*DE
2AFB DA151F       JP C,1F15,REPORT-4
2AFE C9           RET
2AFF 2A4D5C       LD HL,(5C4D)  {DEST}
2B02 FDCB374E     BIT 1,(IY+37)
2B06 285E         JR Z,2B66,L-EXISTS
2B08 010500       LD BC,0005
2B0B 03           INC BC
2B0C 23           INC HL
2B0D 7E           LD A,(HL)
2B0E FE20         CP 20
2B10 28FA         JR Z,2B0C,L-NO-SP
2B12 300B         JR NC,2B1F,L-TEST-CH
2B14 FE10         CP 10
2B16 3811         JR C,2B29,L-SPACES
2B18 FE16         CP 16
2B1A 300D         JR NC,2B29,L-SPACES
2B1C 23           INC HL
2B1D 18ED         JR 2B0C,L-NO-SP
2B1F CD882C       CALL 2C88,ALPHANUM
2B22 38E7         JR C,2B0B,L-EACH-CH
2B24 FE24         CP 24
2B26 CAC02B       JP Z,2BC0,L-NEW$
2B29 79           LD A,C
2B2A 2A595C       LD HL,(5C59)  {E-LINE}
2B2D 2B           DEC HL
2B2E CD5516       CALL 1655,MAKE-ROOM
2B31 23           INC HL
2B32 23           INC HL
2B33 EB           EX DE,HL
2B34 D5           PUSH DE
2B35 2A4D5C       LD HL,(5C4D)  {DEST}
2B38 1B           DEC DE
2B39 D606         SUB 06
2B3B 47           LD B,A
2B3C 2811         JR Z,2B4F,L-SINGLE
2B3E 23           INC HL
2B3F 7E           LD A,(HL)
2B40 FE21         CP 21
2B42 38FA         JR C,2B3E,L-CHAR
2B44 F620         OR 20
2B46 13           INC DE
2B47 12           LD (DE),A
2B48 10F4         DJNZ 2B3E,L-CHAR
2B4A F680         OR 80
2B4C 12           LD (DE),A
2B4D 3EC0         LD A,C0
2B4F 2A4D5C       LD HL,(5C4D)  {DEST}
2B52 AE           XOR (HL)
2B53 F620         OR 20
2B55 E1           POP HL
2B56 CDEA2B       CALL 2BEA,L-FIRST
2B59 E5           PUSH HL
2B5A EF           RST 28,FP-CALC
2B5B 02           DEFB 02,delete
2B5C 38           DEFB 38,end-calc
2B5D E1           POP HL
2B5E 010500       LD BC,0005
2B61 A7           AND A
2B62 ED42         SBC HL,BC
2B64 1840         JR 2BA6,L-ENTER
2B66 FDCB0176     BIT 6,(IY+01)
2B6A 2806         JR Z,2B72,L-DELETE$
2B6C 110600       LD DE,0006
2B6F 19           ADD HL,DE
2B70 18E7         JR 2B59,L-NUMERIC
2B72 2A4D5C       LD HL,(5C4D)  {DEST}
2B75 ED4B725C     LD BC,(5C72)  {STRLEN}
2B79 FDCB3746     BIT 0,(IY+37)
2B7D 2030         JR NZ,2BAF,L-ADD$
2B7F 78           LD A,B
2B80 B1           OR C
2B81 C8           RET Z
2B82 E5           PUSH HL
2B83 F7           RST 30,BC-SPACES
2B84 D5           PUSH DE
2B85 C5           PUSH BC
2B86 54           LD D,H
2B87 5D           LD E,L
2B88 23           INC HL
2B89 3620         LD (HL),20
2B8B EDB8         LDDR
2B8D E5           PUSH HL
2B8E CDF12B       CALL 2BF1,STK-FETCH
2B91 E1           POP HL
2B92 E3           EX SP,(HL)
2B93 A7           AND A
2B94 ED42         SBC HL,BC
2B96 09           ADD HL,BC
2B97 3002         JR NC,2B9B,L-LENGTH
2B99 44           LD B,H
2B9A 4D           LD C,L
2B9B E3           EX SP,(HL)
2B9C EB           EX DE,HL
2B9D 78           LD A,B
2B9E B1           OR C
2B9F 2802         JR Z,2BA3,L-IN-W/S
2BA1 EDB0         LDIR
2BA3 C1           POP BC
2BA4 D1           POP DE
2BA5 E1           POP HL
2BA6 EB           EX DE,HL
2BA7 78           LD A,B
2BA8 B1           OR C
2BA9 C8           RET Z
2BAA D5           PUSH DE
2BAB EDB0         LDIR
2BAD E1           POP HL
2BAE C9           RET
2BAF 2B           DEC HL
2BB0 2B           DEC HL
2BB1 2B           DEC HL
2BB2 7E           LD A,(HL)
2BB3 E5           PUSH HL
2BB4 C5           PUSH BC
2BB5 CDC62B       CALL 2BC6,L-STRING
2BB8 C1           POP BC
2BB9 E1           POP HL
2BBA 03           INC BC
2BBB 03           INC BC
2BBC 03           INC BC
2BBD C3E819       JP 19E8,RECLAIM-2
2BC0 3EDF         LD A,DF
2BC2 2A4D5C       LD HL,(5C4D)  {DEST}
2BC5 A6           AND (HL)
2BC6 F5           PUSH AF
2BC7 CDF12B       CALL 2BF1,STK-FETCH
2BCA EB           EX DE,HL
2BCB 09           ADD HL,BC
2BCC C5           PUSH BC
2BCD 2B           DEC HL
2BCE 224D5C       LD (5C4D),HL  {DEST}
2BD1 03           INC BC
2BD2 03           INC BC
2BD3 03           INC BC
2BD4 2A595C       LD HL,(5C59)  {E-LINE}
2BD7 2B           DEC HL
2BD8 CD5516       CALL 1655,MAKE-ROOM
2BDB 2A4D5C       LD HL,(5C4D)  {DEST}
2BDE C1           POP BC
2BDF C5           PUSH BC
2BE0 03           INC BC
2BE1 EDB8         LDDR
2BE3 EB           EX DE,HL
2BE4 23           INC HL
2BE5 C1           POP BC
2BE6 70           LD (HL),B
2BE7 2B           DEC HL
2BE8 71           LD (HL),C
2BE9 F1           POP AF
2BEA 2B           DEC HL
2BEB 77           LD (HL),A
2BEC 2A595C       LD HL,(5C59)  {E-LINE}
2BEF 2B           DEC HL
2BF0 C9           RET
2BF1 2A655C       LD HL,(5C65)  {STKEND}
2BF4 2B           DEC HL
2BF5 46           LD B,(HL)
2BF6 2B           DEC HL
2BF7 4E           LD C,(HL)
2BF8 2B           DEC HL
2BF9 56           LD D,(HL)
2BFA 2B           DEC HL
2BFB 5E           LD E,(HL)
2BFC 2B           DEC HL
2BFD 7E           LD A,(HL)
2BFE 22655C       LD (5C65),HL  {STKEND}
2C01 C9           RET
2C02 CDB228       CALL 28B2,LOOK-VARS
2C05 C28A1C       JP NZ,1C8A,REPORT-C
2C08 CD3025       CALL 2530,SYNTAX-Z
2C0B 2008         JR NZ,2C15,D-RUN
2C0D CBB1         RES 6,C
2C0F CD9629       CALL 2996,STK-VAR
2C12 CDEE1B       CALL 1BEE,CHECK-END
2C15 3808         JR C,2C1F,D-LETTER
2C17 C5           PUSH BC
2C18 CDB819       CALL 19B8,NEXT-ONE
2C1B CDE819       CALL 19E8,RECLAIM-2
2C1E C1           POP BC
2C1F CBF9         SET 7,C
2C21 0600         LD B,00
2C23 C5           PUSH BC
2C24 210100       LD HL,0001
2C27 CB71         BIT 6,C
2C29 2002         JR NZ,2C2D,D-SIZE
2C2B 2E05         LD L,05
2C2D EB           EX DE,HL
2C2E E7           RST 20,NEXT-CHAR
2C2F 26FF         LD H,FF
2C31 CDCC2A       CALL 2ACC,INT-EXP1
2C34 DA202A       JP C,2A20,REPORT-3
2C37 E1           POP HL
2C38 C5           PUSH BC
2C39 24           INC H
2C3A E5           PUSH HL
2C3B 60           LD H,B
2C3C 69           LD L,C
2C3D CDF42A       CALL 2AF4,GET-HL*DE
2C40 EB           EX DE,HL
2C41 DF           RST 18,GET-CHAR
2C42 FE2C         CP 2C
2C44 28E8         JR Z,2C2E,D-NO-LOOP
2C46 FE29         CP 29
2C48 20BB         JR NZ,2C05,D-RPORT-C
2C4A E7           RST 20,NEXT-CHAR
2C4B C1           POP BC
2C4C 79           LD A,C
2C4D 68           LD L,B
2C4E 2600         LD H,00
2C50 23           INC HL
2C51 23           INC HL
2C52 29           ADD HL,HL
2C53 19           ADD HL,DE
2C54 DA151F       JP C,1F15,REPORT-4
2C57 D5           PUSH DE
2C58 C5           PUSH BC
2C59 E5           PUSH HL
2C5A 44           LD B,H
2C5B 4D           LD C,L
2C5C 2A595C       LD HL,(5C59)  {E-LINE}
2C5F 2B           DEC HL
2C60 CD5516       CALL 1655,MAKE-ROOM
2C63 23           INC HL
2C64 77           LD (HL),A
2C65 C1           POP BC
2C66 0B           DEC BC
2C67 0B           DEC BC
2C68 0B           DEC BC
2C69 23           INC HL
2C6A 71           LD (HL),C
2C6B 23           INC HL
2C6C 70           LD (HL),B
2C6D C1           POP BC
2C6E 78           LD A,B
2C6F 23           INC HL
2C70 77           LD (HL),A
2C71 62           LD H,D
2C72 6B           LD L,E
2C73 1B           DEC DE
2C74 3600         LD (HL),00
2C76 CB71         BIT 6,C
2C78 2802         JR Z,2C7C,DIM-CLEAR
2C7A 3620         LD (HL),20
2C7C C1           POP BC
2C7D EDB8         LDDR
2C7F C1           POP BC
2C80 70           LD (HL),B
2C81 2B           DEC HL
2C82 71           LD (HL),C
2C83 2B           DEC HL
2C84 3D           DEC A
2C85 20F8         JR NZ,2C7F,DIM-SIZES
2C87 C9           RET
2C88 CD1B2D       CALL 2D1B,NUMERIC
2C8B 3F           CCF
2C8C D8           RET C
2C8D FE41         CP 41
2C8F 3F           CCF
2C90 D0           RET NC
2C91 FE5B         CP 5B
2C93 D8           RET C
2C94 FE61         CP 61
2C96 3F           CCF
2C97 D0           RET NC
2C98 FE7B         CP 7B
2C9A C9           RET
2C9B FEC4         CP C4
2C9D 2019         JR NZ,2CB8,NOT-BIN
2C9F 110000       LD DE,0000
2CA2 E7           RST 20,NEXT-CHAR
2CA3 D631         SUB 31
2CA5 CE00         ADC A,00
2CA7 200A         JR NZ,2CB3,BIN-END
2CA9 EB           EX DE,HL
2CAA 3F           CCF
2CAB ED6A         ADC HL,HL
2CAD DAAD31       JP C,31AD,REPORT-6
2CB0 EB           EX DE,HL
2CB1 18EF         JR 2CA2,BIN-DIGIT
2CB3 42           LD B,D
2CB4 4B           LD C,E
2CB5 C32B2D       JP 2D2B,STACK-BC
2CB8 FE2E         CP 2E
2CBA 280F         JR Z,2CCB,DECIMAL
2CBC CD3B2D       CALL 2D3B,INT-TO-FP
2CBF FE2E         CP 2E
2CC1 2028         JR NZ,2CEB,E-FORMAT
2CC3 E7           RST 20,NEXT-CHAR
2CC4 CD1B2D       CALL 2D1B,NUMERIC
2CC7 3822         JR C,2CEB,E-FORMAT
2CC9 180A         JR 2CD5
2CCB E7           RST 20,NEXT-CHAR
2CCC CD1B2D       CALL 2D1B,NUMERIC
2CCF DA8A1C       JP C,1C8A,REPORT-C
2CD2 EF           RST 28,FP-CALC
2CD3 A0           DEFB A0,stk-zero
2CD4 38           DEFB 38,end-calc
2CD5 EF           RST 28,FP-CALC
2CD6 A1           DEFB A1,stk-one
2CD7 C0           DEFB C0,st-mem-0
2CD8 02           DEFB 02,delete
2CD9 38           DEFB 38,end-calc
2CDA DF           RST 18,GET-CHAR
2CDB CD222D       CALL 2D22,STK-DIGIT
2CDE 380B         JR C,2CEB,E-FORMAT
2CE0 EF           RST 28,FP-CALC
2CE1 E0           DEFB E0,get-mem-0
2CE2 A4           DEFB A4,stk-ten
2CE3 05           DEFB 05,division
2CE4 C0           DEFB C0,st-mem-0
2CE5 04           DEFB 04,multiply
2CE6 0F           DEFB 0F,addition
2CE7 38           DEFB 38,end-calc
2CE8 E7           RST 20,NEXT-CHAR
2CE9 18EF         JR 2CDA,NXT-DGT-1
2CEB FE45         CP 45
2CED 2803         JR Z,2CF2,SIGN-FLAG
2CEF FE65         CP 65
2CF1 C0           RET NZ
2CF2 06FF         LD B,FF
2CF4 E7           RST 20,NEXT-CHAR
2CF5 FE2B         CP 2B
2CF7 2805         JR Z,2CFE,SIGN-DONE
2CF9 FE2D         CP 2D
2CFB 2002         JR NZ,2CFF,ST-E-PART
2CFD 04           INC B
2CFE E7           RST 20,NEXT-CHAR
2CFF CD1B2D       CALL 2D1B,NUMERIC
2D02 38CB         JR C,2CCF,DEC-RPT-C
2D04 C5           PUSH BC
2D05 CD3B2D       CALL 2D3B,INT-TO-FP
2D08 CDD52D       CALL 2DD5,FP-TO-A
2D0B C1           POP BC
2D0C DAAD31       JP C,31AD,REPORT-6
2D0F A7           AND A
2D10 FAAD31       JP M,31AD,REPORT-6
2D13 04           INC B
2D14 2802         JR Z,2D18,E-FP-JUMP
2D16 ED44         NEG
2D18 C34F2D       JP 2D4F,E-TO-FP
2D1B FE30         CP 30
2D1D D8           RET C
2D1E FE3A         CP 3A
2D20 3F           CCF
2D21 C9           RET
2D22 CD1B2D       CALL 2D1B,NUMERIC
2D25 D8           RET C
2D26 D630         SUB 30
2D28 4F           LD C,A
2D29 0600         LD B,00
2D2B FD213A5C     LD IY,5C3A
2D2F AF           XOR A
2D30 5F           LD E,A
2D31 51           LD D,C
2D32 48           LD C,B
2D33 47           LD B,A
2D34 CDB62A       CALL 2AB6,STK-STORE
2D37 EF           RST 28,FP-CALC
2D38 38           DEFB 38,end-calc
2D39 A7           AND A
2D3A C9           RET
2D3B F5           PUSH AF
2D3C EF           RST 28,FP-CALC
2D3D A0           DEFB A0,stk-zero
2D3E 38           DEFB 38,end-calc
2D3F F1           POP AF
2D40 CD222D       CALL 2D22,STK-DIGIT
2D43 D8           RET C
2D44 EF           RST 28,FP-CALC
2D45 01           DEFB 01,exchange
2D46 A4           DEFB A4,stk-ten
2D47 04           DEFB 04,multiply
2D48 0F           DEFB 0F,addition
2D49 38           DEFB 38,end-calc
2D4A CD7400       CALL 0074,CH-ADD+1
2D4D 18F1         JR 2D40,NXT-DGT-2
2D4F 07           RLCA
2D50 0F           RRCA
2D51 3002         JR NC,2D55,E-SAVE
2D53 2F           CPL
2D54 3C           INC A
2D55 F5           PUSH AF
2D56 21925C       LD HL,5C92
2D59 CD0B35       CALL 350B,FP-0/1
2D5C EF           RST 28,FP-CALC
2D5D A4           DEFB A4,stk-ten
2D5E 38           DEFB 38,end-calc
2D5F F1           POP AF
2D60 CB3F         SRL A
2D62 300D         JR NC,2D71,E-TST-END
2D64 F5           PUSH AF
2D65 EF           RST 28,FP-CALC
2D66 C1           DEFB C1,st-mem-1
2D67 E0           DEFB E0,get-mem-0
2D68 00           DEFB 00,jump-true
2D69 04           DEFB 04,multiply
2D6A 04           DEFB 04,multiply
2D6B 3302         DEFB 33,02,jump 2D6F
2D6D 05           DEFB 05,division
2D6E E1           DEFB E1,get-mem-1
2D6F 38           DEFB 38,end-calc
2D70 F1           POP AF
2D71 2808         JR Z,2D7B,E-END
2D73 F5           PUSH AF
2D74 EF           RST 28,FP-CALC
2D75 31           DEFB 31,duplicate
2D76 04           DEFB 04,multiply
2D77 38           DEFB 38,end-calc
2D78 F1           POP AF
2D79 18E5         JR 2D60,E-LOOP
2D7B EF           RST 28,FP-CALC
2D7C 02           DEFB 02,delete
2D7D 38           DEFB 38,end-calc
2D7E C9           RET
2D7F 23           INC HL
2D80 4E           LD C,(HL)
2D81 23           INC HL
2D82 7E           LD A,(HL)
2D83 A9           XOR C
2D84 91           SUB C
2D85 5F           LD E,A
2D86 23           INC HL
2D87 7E           LD A,(HL)
2D88 89           ADC A,C
2D89 A9           XOR C
2D8A 57           LD D,A
2D8B C9           RET
2D8C 0E00         LD C,00
2D8E E5           PUSH HL
2D8F 3600         LD (HL),00
2D91 23           INC HL
2D92 71           LD (HL),C
2D93 23           INC HL
2D94 7B           LD A,E
2D95 A9           XOR C
2D96 91           SUB C
2D97 77           LD (HL),A
2D98 23           INC HL
2D99 7A           LD A,D
2D9A 89           ADC A,C
2D9B A9           XOR C
2D9C 77           LD (HL),A
2D9D 23           INC HL
2D9E 3600         LD (HL),00
2DA0 E1           POP HL
2DA1 C9           RET
2DA2 EF           RST 28,FP-CALC
2DA3 38           DEFB 38,end-calc
2DA4 7E           LD A,(HL)
2DA5 A7           AND A
2DA6 2805         JR Z,2DAD,FP-DELETE
2DA8 EF           RST 28,FP-CALC
2DA9 A2           DEFB A2,stk-half
2DAA 0F           DEFB 0F,addition
2DAB 27           DEFB 27,int
2DAC 38           DEFB 38,end-calc
2DAD EF           RST 28,FP-CALC
2DAE 02           DEFB 02,delete
2DAF 38           DEFB 38,end-calc
2DB0 E5           PUSH HL
2DB1 D5           PUSH DE
2DB2 EB           EX DE,HL
2DB3 46           LD B,(HL)
2DB4 CD7F2D       CALL 2D7F,INT-FETCH
2DB7 AF           XOR A
2DB8 90           SUB B
2DB9 CB79         BIT 7,C
2DBB 42           LD B,D
2DBC 4B           LD C,E
2DBD 7B           LD A,E
2DBE D1           POP DE
2DBF E1           POP HL
2DC0 C9           RET
2DC1 57           LD D,A
2DC2 17           RLA
2DC3 9F           SBC A,A
2DC4 5F           LD E,A
2DC5 4F           LD C,A
2DC6 AF           XOR A
2DC7 47           LD B,A
2DC8 CDB62A       CALL 2AB6,STK-STORE
2DCB EF           RST 28,FP-CALC
2DCC 34           DEFB 34,stk-data
2DCD EF1A209A85   DEFB EF,1A,20,9A,85        ;0.4D104D428
2DD2 04           DEFB 04,multiply
2DD3 27           DEFB 27,int
2DD4 38           DEFB 38,end-calc
2DD5 CDA22D       CALL 2DA2,FP-TO-BC
2DD8 D8           RET C
2DD9 F5           PUSH AF
2DDA 05           DEC B
2DDB 04           INC B
2DDC 2803         JR Z,2DE1,FP-A-END
2DDE F1           POP AF
2DDF 37           SCF
2DE0 C9           RET
2DE1 F1           POP AF
2DE2 C9           RET
2DE3 EF           RST 28,FP-CALC
2DE4 31           DEFB 31,duplicate
2DE5 36           DEFB 36,less-0
2DE6 00           DEFB 00,jump-true
2DE7 0B           DEFB 0B,nos-neql
2DE8 31           DEFB 31,duplicate
2DE9 37           DEFB 37,greater-0
2DEA 00           DEFB 00,jump-true
2DEB 0D           DEFB 0D,no-less
2DEC 02           DEFB 02,delete
2DED 38           DEFB 38,end-calc
2DEE 3E30         LD A,30
2DF0 D7           RST 10,PRINT-A-1
2DF1 C9           RET
2DF2 2A383E       LD HL,(3E38)
2DF5 2D           DEC L
2DF6 D7           RST 10,PRINT-A-1
2DF7 EF           RST 28,FP-CALC
2DF8 A0           DEFB A0,stk-zero
2DF9 C3           DEFB C3,st-mem-3
2DFA C4           DEFB C4,st-mem-4
2DFB C5           DEFB C5,st-mem-5
2DFC 02           DEFB 02,delete
2DFD 38           DEFB 38,end-calc
2DFE D9           EXX
2DFF E5           PUSH HL
2E00 D9           EXX
2E01 EF           RST 28,FP-CALC
2E02 31           DEFB 31,duplicate
2E03 27           DEFB 27,int
2E04 C2           DEFB C2,st-mem-2
2E05 03           DEFB 03,subtract
2E06 E2           DEFB E2,get-mem-2
2E07 01           DEFB 01,exchange
2E08 C2           DEFB C2,st-mem-2
2E09 02           DEFB 02,delete
2E0A 38           DEFB 38,end-calc
2E0B 7E           LD A,(HL)
2E0C A7           AND A
2E0D 2047         JR NZ,2E56,PF-LARGE
2E0F CD7F2D       CALL 2D7F,INT-FETCH
2E12 0610         LD B,10
2E14 7A           LD A,D
2E15 A7           AND A
2E16 2006         JR NZ,2E1E,PF-SAVE
2E18 B3           OR E
2E19 2809         JR Z,2E24,PF-SMALL
2E1B 53           LD D,E
2E1C 0608         LD B,08
2E1E D5           PUSH DE
2E1F D9           EXX
2E20 D1           POP DE
2E21 D9           EXX
2E22 1857         JR 2E7B,PF-BITS
2E24 EF           RST 28,FP-CALC
2E25 E2           DEFB E2,get-mem-2
2E26 38           DEFB 38,end-calc
2E27 7E           LD A,(HL)
2E28 D67E         SUB 7E
2E2A CDC12D       CALL 2DC1,LOG-(2^A)
2E2D 57           LD D,A
2E2E 3AAC5C       LD A,(5CAC)
2E31 92           SUB D
2E32 32AC5C       LD (5CAC),A
2E35 7A           LD A,D
2E36 CD4F2D       CALL 2D4F,E-TO-FP
2E39 EF           RST 28,FP-CALC
2E3A 31           DEFB 31,duplicate
2E3B 27           DEFB 27,int
2E3C C1           DEFB C1,st-mem-1
2E3D 03           DEFB 03,subtract
2E3E E1           DEFB E1,get-mem-1
2E3F 38           DEFB 38,end-calc
2E40 CDD52D       CALL 2DD5,FP-TO-A
2E43 E5           PUSH HL
2E44 32A15C       LD (5CA1),A
2E47 3D           DEC A
2E48 17           RLA
2E49 9F           SBC A,A
2E4A 3C           INC A
2E4B 21AB5C       LD HL,5CAB
2E4E 77           LD (HL),A
2E4F 23           INC HL
2E50 86           ADD A,(HL)
2E51 77           LD (HL),A
2E52 E1           POP HL
2E53 C3CF2E       JP 2ECF,PF-FRACTN
2E56 D680         SUB 80
2E58 FE1C         CP 1C
2E5A 3813         JR C,2E6F,PF-MEDIUM
2E5C CDC12D       CALL 2DC1,LOG-(2^A)
2E5F D607         SUB 07
2E61 47           LD B,A
2E62 21AC5C       LD HL,5CAC
2E65 86           ADD A,(HL)
2E66 77           LD (HL),A
2E67 78           LD A,B
2E68 ED44         NEG
2E6A CD4F2D       CALL 2D4F,E-TO-FP
2E6D 1892         JR 2E01,PF-LOOP
2E6F EB           EX DE,HL
2E70 CDBA2F       CALL 2FBA,FETCH-TWO
2E73 D9           EXX
2E74 CBFA         SET 7,D
2E76 7D           LD A,L
2E77 D9           EXX
2E78 D680         SUB 80
2E7A 47           LD B,A
2E7B CB23         SLA E
2E7D CB12         RL D
2E7F D9           EXX
2E80 CB13         RL E
2E82 CB12         RL D
2E84 D9           EXX
2E85 21AA5C       LD HL,5CAA
2E88 0E05         LD C,05
2E8A 7E           LD A,(HL)
2E8B 8F           ADC A,A
2E8C 27           DAA
2E8D 77           LD (HL),A
2E8E 2B           DEC HL
2E8F 0D           DEC C
2E90 20F8         JR NZ,2E8A,PF-BYTES
2E92 10E7         DJNZ 2E7B,PF-BITS
2E94 AF           XOR A
2E95 21A65C       LD HL,5CA6
2E98 11A15C       LD DE,5CA1
2E9B 0609         LD B,09
2E9D ED6F         RLD
2E9F 0EFF         LD C,FF
2EA1 ED6F         RLD
2EA3 2004         JR NZ,2EA9,PF-INSERT
2EA5 0D           DEC C
2EA6 0C           INC C
2EA7 200A         JR NZ,2EB3,PF-TEST-2
2EA9 12           LD (DE),A
2EAA 13           INC DE
2EAB FD3471       INC (IY+71)
2EAE FD3472       INC (IY+72)
2EB1 0E00         LD C,00
2EB3 CB40         BIT 0,B
2EB5 2801         JR Z,2EB8,PF-ALL-9
2EB7 23           INC HL
2EB8 10E7         DJNZ 2EA1,PF-DIGITS
2EBA 3AAB5C       LD A,(5CAB)
2EBD D609         SUB 09
2EBF 380A         JR C,2ECB,PF-MORE
2EC1 FD3571       DEC (IY+71)
2EC4 3E04         LD A,04
2EC6 FDBE6F       CP (IY+6F)
2EC9 1841         JR 2F0C,PF-ROUND
2ECB EF           RST 28,FP-CALC
2ECC 02           DEFB 02,delete
2ECD E2           DEFB E2,get-mem-2
2ECE 38           DEFB 38,end-calc
2ECF EB           EX DE,HL
2ED0 CDBA2F       CALL 2FBA,FETCH-TWO
2ED3 D9           EXX
2ED4 3E80         LD A,80
2ED6 95           SUB L
2ED7 2E00         LD L,00
2ED9 CBFA         SET 7,D
2EDB D9           EXX
2EDC CDDD2F       CALL 2FDD,SHIFT-FP
2EDF FD7E71       LD A,(IY+71)
2EE2 FE08         CP 08
2EE4 3806         JR C,2EEC,PF-FR-DGT
2EE6 D9           EXX
2EE7 CB12         RL D
2EE9 D9           EXX
2EEA 1820         JR 2F0C,PF-ROUND
2EEC 010002       LD BC,0200
2EEF 7B           LD A,E
2EF0 CD8B2F       CALL 2F8B,CA=10*A+C
2EF3 5F           LD E,A
2EF4 7A           LD A,D
2EF5 CD8B2F       CALL 2F8B,CA=10*A+C
2EF8 57           LD D,A
2EF9 C5           PUSH BC
2EFA D9           EXX
2EFB C1           POP BC
2EFC 10F1         DJNZ 2EEF,PF-FR-EXX
2EFE 21A15C       LD HL,5CA1
2F01 79           LD A,C
2F02 FD4E71       LD C,(IY+71)
2F05 09           ADD HL,BC
2F06 77           LD (HL),A
2F07 FD3471       INC (IY+71)
2F0A 18D3         JR 2EDF,PF-FRN-LP
2F0C F5           PUSH AF
2F0D 21A15C       LD HL,5CA1
2F10 FD4E71       LD C,(IY+71)
2F13 0600         LD B,00
2F15 09           ADD HL,BC
2F16 41           LD B,C
2F17 F1           POP AF
2F18 2B           DEC HL
2F19 7E           LD A,(HL)
2F1A CE00         ADC A,00
2F1C 77           LD (HL),A
2F1D A7           AND A
2F1E 2805         JR Z,2F25,PF-R-BACK
2F20 FE0A         CP 0A
2F22 3F           CCF
2F23 3008         JR NC,2F2D,PF-COUNT
2F25 10F1         DJNZ 2F18,PF-RND-LP
2F27 3601         LD (HL),01
2F29 04           INC B
2F2A FD3472       INC (IY+72)
2F2D FD7071       LD (IY+71),B
2F30 EF           RST 28,FP-CALC
2F31 02           DEFB 02,delete
2F32 38           DEFB 38,end-calc
2F33 D9           EXX
2F34 E1           POP HL
2F35 D9           EXX
2F36 ED4BAB5C     LD BC,(5CAB)
2F3A 21A15C       LD HL,5CA1
2F3D 78           LD A,B
2F3E FE09         CP 09
2F40 3804         JR C,2F46,PF-NOT-E
2F42 FEFC         CP FC
2F44 3826         JR C,2F6C,PF-E-FRMT
2F46 A7           AND A
2F47 CCEF15       CALL Z,15EF,OUT-CODE
2F4A AF           XOR A
2F4B 90           SUB B
2F4C FA522F       JP M,2F52,PF-OUT-LP
2F4F 47           LD B,A
2F50 180C         JR 2F5E,PF-DC-OUT
2F52 79           LD A,C
2F53 A7           AND A
2F54 2803         JR Z,2F59,PF-OUT-DT
2F56 7E           LD A,(HL)
2F57 23           INC HL
2F58 0D           DEC C
2F59 CDEF15       CALL 15EF,OUT-CODE
2F5C 10F4         DJNZ 2F52,PF-OUT-LP
2F5E 79           LD A,C
2F5F A7           AND A
2F60 C8           RET Z
2F61 04           INC B
2F62 3E2E         LD A,2E
2F64 D7           RST 10,PRINT-A-1
2F65 3E30         LD A,30
2F67 10FB         DJNZ 2F64,PF-DEC-0S
2F69 41           LD B,C
2F6A 18E6         JR 2F52,PF-OUT-LP
2F6C 50           LD D,B
2F6D 15           DEC D
2F6E 0601         LD B,01
2F70 CD4A2F       CALL 2F4A,PF-E-SBRN
2F73 3E45         LD A,45
2F75 D7           RST 10,PRINT-A-1
2F76 4A           LD C,D
2F77 79           LD A,C
2F78 A7           AND A
2F79 F2832F       JP P,2F83,PF-E-POS
2F7C ED44         NEG
2F7E 4F           LD C,A
2F7F 3E2D         LD A,2D
2F81 1802         JR 2F85,PF-E-SIGN
2F83 3E2B         LD A,2B
2F85 D7           RST 10,PRINT-A-1
2F86 0600         LD B,00
2F88 C31B1A       JP 1A1B,OUT-NUM-1
2F8B D5           PUSH DE
2F8C 6F           LD L,A
2F8D 2600         LD H,00
2F8F 5D           LD E,L
2F90 54           LD D,H
2F91 29           ADD HL,HL
2F92 29           ADD HL,HL
2F93 19           ADD HL,DE
2F94 29           ADD HL,HL
2F95 59           LD E,C
2F96 19           ADD HL,DE
2F97 4C           LD C,H
2F98 7D           LD A,L
2F99 D1           POP DE
2F9A C9           RET
2F9B 7E           LD A,(HL)
2F9C 3600         LD (HL),00
2F9E A7           AND A
2F9F C8           RET Z
2FA0 23           INC HL
2FA1 CB7E         BIT 7,(HL)
2FA3 CBFE         SET 7,(HL)
2FA5 2B           DEC HL
2FA6 C8           RET Z
2FA7 C5           PUSH BC
2FA8 010500       LD BC,0005
2FAB 09           ADD HL,BC
2FAC 41           LD B,C
2FAD 4F           LD C,A
2FAE 37           SCF
2FAF 2B           DEC HL
2FB0 7E           LD A,(HL)
2FB1 2F           CPL
2FB2 CE00         ADC A,00
2FB4 77           LD (HL),A
2FB5 10F8         DJNZ 2FAF,NEG-BYTE
2FB7 79           LD A,C
2FB8 C1           POP BC
2FB9 C9           RET
2FBA E5           PUSH HL
2FBB F5           PUSH AF
2FBC 4E           LD C,(HL)
2FBD 23           INC HL
2FBE 46           LD B,(HL)
2FBF 77           LD (HL),A
2FC0 23           INC HL
2FC1 79           LD A,C
2FC2 4E           LD C,(HL)
2FC3 C5           PUSH BC
2FC4 23           INC HL
2FC5 4E           LD C,(HL)
2FC6 23           INC HL
2FC7 46           LD B,(HL)
2FC8 EB           EX DE,HL
2FC9 57           LD D,A
2FCA 5E           LD E,(HL)
2FCB D5           PUSH DE
2FCC 23           INC HL
2FCD 56           LD D,(HL)
2FCE 23           INC HL
2FCF 5E           LD E,(HL)
2FD0 D5           PUSH DE
2FD1 D9           EXX
2FD2 D1           POP DE
2FD3 E1           POP HL
2FD4 C1           POP BC
2FD5 D9           EXX
2FD6 23           INC HL
2FD7 56           LD D,(HL)
2FD8 23           INC HL
2FD9 5E           LD E,(HL)
2FDA F1           POP AF
2FDB E1           POP HL
2FDC C9           RET
2FDD A7           AND A
2FDE C8           RET Z
2FDF FE21         CP 21
2FE1 3016         JR NC,2FF9,APPEND-0
2FE3 C5           PUSH BC
2FE4 47           LD B,A
2FE5 D9           EXX
2FE6 CB2D         SRA L
2FE8 CB1A         RR D
2FEA CB1B         RR E
2FEC D9           EXX
2FED CB1A         RR D
2FEF CB1B         RR E
2FF1 10F2         DJNZ 2FE5,ONE-SHIFT
2FF3 C1           POP BC
2FF4 D0           RET NC
2FF5 CD0430       CALL 3004,ADD-BACK
2FF8 C0           RET NZ
2FF9 D9           EXX
2FFA AF           XOR A
2FFB 2E00         LD L,00
2FFD 57           LD D,A
2FFE 5D           LD E,L
2FFF D9           EXX
3000 110000       LD DE,0000
3003 C9           RET
3004 1C           INC E
3005 C0           RET NZ
3006 14           INC D
3007 C0           RET NZ
3008 D9           EXX
3009 1C           INC E
300A 2001         JR NZ,300D,ALL-ADDED
300C 14           INC D
300D D9           EXX
300E C9           RET
300F EB           EX DE,HL
3010 CD6E34       CALL 346E,NEGATE
3013 EB           EX DE,HL
3014 1A           LD A,(DE)
3015 B6           OR (HL)
3016 2026         JR NZ,303E,FULL-ADDN
3018 D5           PUSH DE
3019 23           INC HL
301A E5           PUSH HL
301B 23           INC HL
301C 5E           LD E,(HL)
301D 23           INC HL
301E 56           LD D,(HL)
301F 23           INC HL
3020 23           INC HL
3021 23           INC HL
3022 7E           LD A,(HL)
3023 23           INC HL
3024 4E           LD C,(HL)
3025 23           INC HL
3026 46           LD B,(HL)
3027 E1           POP HL
3028 EB           EX DE,HL
3029 09           ADD HL,BC
302A EB           EX DE,HL
302B 8E           ADC A,(HL)
302C 0F           RRCA
302D CE00         ADC A,00
302F 200B         JR NZ,303C,ADDN-OFLW
3031 9F           SBC A,A
3032 77           LD (HL),A
3033 23           INC HL
3034 73           LD (HL),E
3035 23           INC HL
3036 72           LD (HL),D
3037 2B           DEC HL
3038 2B           DEC HL
3039 2B           DEC HL
303A D1           POP DE
303B C9           RET
303C 2B           DEC HL
303D D1           POP DE
303E CD9332       CALL 3293,RE-ST-TWO
3041 D9           EXX
3042 E5           PUSH HL
3043 D9           EXX
3044 D5           PUSH DE
3045 E5           PUSH HL
3046 CD9B2F       CALL 2F9B,PREP-ADD
3049 47           LD B,A
304A EB           EX DE,HL
304B CD9B2F       CALL 2F9B,PREP-ADD
304E 4F           LD C,A
304F B8           CP B
3050 3003         JR NC,3055,SHIFT-LEN
3052 78           LD A,B
3053 41           LD B,C
3054 EB           EX DE,HL
3055 F5           PUSH AF
3056 90           SUB B
3057 CDBA2F       CALL 2FBA,FETCH-TWO
305A CDDD2F       CALL 2FDD,SHIFT-FP
305D F1           POP AF
305E E1           POP HL
305F 77           LD (HL),A
3060 E5           PUSH HL
3061 68           LD L,B
3062 61           LD H,C
3063 19           ADD HL,DE
3064 D9           EXX
3065 EB           EX DE,HL
3066 ED4A         ADC HL,BC
3068 EB           EX DE,HL
3069 7C           LD A,H
306A 8D           ADC A,L
306B 6F           LD L,A
306C 1F           RRA
306D AD           XOR L
306E D9           EXX
306F EB           EX DE,HL
3070 E1           POP HL
3071 1F           RRA
3072 3008         JR NC,307C,TEST-NEG
3074 3E01         LD A,01
3076 CDDD2F       CALL 2FDD,SHIFT-FP
3079 34           INC (HL)
307A 2823         JR Z,309F,ADD-REP-6
307C D9           EXX
307D 7D           LD A,L
307E E680         AND 80
3080 D9           EXX
3081 23           INC HL
3082 77           LD (HL),A
3083 2B           DEC HL
3084 281F         JR Z,30A5,GO-NC-MLT
3086 7B           LD A,E
3087 ED44         NEG
3089 3F           CCF
308A 5F           LD E,A
308B 7A           LD A,D
308C 2F           CPL
308D CE00         ADC A,00
308F 57           LD D,A
3090 D9           EXX
3091 7B           LD A,E
3092 2F           CPL
3093 CE00         ADC A,00
3095 5F           LD E,A
3096 7A           LD A,D
3097 2F           CPL
3098 CE00         ADC A,00
309A 3007         JR NC,30A3,END-COMPL
309C 1F           RRA
309D D9           EXX
309E 34           INC (HL)
309F CAAD31       JP Z,31AD,REPORT-6
30A2 D9           EXX
30A3 57           LD D,A
30A4 D9           EXX
30A5 AF           XOR A
30A6 C35531       JP 3155,TEST-NORM
30A9 C5           PUSH BC
30AA 0610         LD B,10
30AC 7C           LD A,H
30AD 4D           LD C,L
30AE 210000       LD HL,0000
30B1 29           ADD HL,HL
30B2 380A         JR C,30BE,HL-END
30B4 CB11         RL C
30B6 17           RLA
30B7 3003         JR NC,30BC,HL-AGAIN
30B9 19           ADD HL,DE
30BA 3802         JR C,30BE,HL-END
30BC 10F3         DJNZ 30B1,HL-LOOP
30BE C1           POP BC
30BF C9           RET
30C0 CDE934       CALL 34E9,TEST-ZERO
30C3 D8           RET C
30C4 23           INC HL
30C5 AE           XOR (HL)
30C6 CBFE         SET 7,(HL)
30C8 2B           DEC HL
30C9 C9           RET
30CA 1A           LD A,(DE)
30CB B6           OR (HL)
30CC 2022         JR NZ,30F0,MULT-LONG
30CE D5           PUSH DE
30CF E5           PUSH HL
30D0 D5           PUSH DE
30D1 CD7F2D       CALL 2D7F,INT-FETCH
30D4 EB           EX DE,HL
30D5 E3           EX SP,(HL)
30D6 41           LD B,C
30D7 CD7F2D       CALL 2D7F,INT-FETCH
30DA 78           LD A,B
30DB A9           XOR C
30DC 4F           LD C,A
30DD E1           POP HL
30DE CDA930       CALL 30A9,HL=HL*DE
30E1 EB           EX DE,HL
30E2 E1           POP HL
30E3 380A         JR C,30EF,MULT-OFLW
30E5 7A           LD A,D
30E6 B3           OR E
30E7 2001         JR NZ,30EA,MULT-RSLT
30E9 4F           LD C,A
30EA CD8E2D       CALL 2D8E,INT-STORE
30ED D1           POP DE
30EE C9           RET
30EF D1           POP DE
30F0 CD9332       CALL 3293,RE-ST-TWO
30F3 AF           XOR A
30F4 CDC030       CALL 30C0,PREP-M/D
30F7 D8           RET C
30F8 D9           EXX
30F9 E5           PUSH HL
30FA D9           EXX
30FB D5           PUSH DE
30FC EB           EX DE,HL
30FD CDC030       CALL 30C0,PREP-M/D
3100 EB           EX DE,HL
3101 385A         JR C,315D,ZERO-RSLT
3103 E5           PUSH HL
3104 CDBA2F       CALL 2FBA,FETCH-TWO
3107 78           LD A,B
3108 A7           AND A
3109 ED62         SBC HL,HL
310B D9           EXX
310C E5           PUSH HL
310D ED62         SBC HL,HL
310F D9           EXX
3110 0621         LD B,21
3112 1811         JR 3125,STRT-MLT
3114 3005         JR NC,311B
3116 19           ADD HL,DE
3117 D9           EXX
3118 ED5A         ADC HL,DE
311A D9           EXX
311B D9           EXX
311C CB1C         RR H
311E CB1D         RR L
3120 D9           EXX
3121 CB1C         RR H
3123 CB1D         RR L
3125 D9           EXX
3126 CB18         RR B
3128 CB19         RR C
312A D9           EXX
312B CB19         RR C
312D 1F           RRA
312E 10E4         DJNZ 3114,MLT-LOOP
3130 EB           EX DE,HL
3131 D9           EXX
3132 EB           EX DE,HL
3133 D9           EXX
3134 C1           POP BC
3135 E1           POP HL
3136 78           LD A,B
3137 81           ADD A,C
3138 2001         JR NZ,313B,MAKE-EXPT
313A A7           AND A
313B 3D           DEC A
313C 3F           CCF
313D 17           RLA
313E 3F           CCF
313F 1F           RRA
3140 F24631       JP P,3146,OFLW1-CLR
3143 3068         JR NC,31AD,REPORT-6
3145 A7           AND A
3146 3C           INC A
3147 2008         JR NZ,3151,OFLW2-CLR
3149 3806         JR C,3151,OFLW2-CLR
314B D9           EXX
314C CB7A         BIT 7,D
314E D9           EXX
314F 205C         JR NZ,31AD,REPORT-6
3151 77           LD (HL),A
3152 D9           EXX
3153 78           LD A,B
3154 D9           EXX
3155 3015         JR NC,316C,NORMALISE
3157 7E           LD A,(HL)
3158 A7           AND A
3159 3E80         LD A,80
315B 2801         JR Z,315E,SKIP-ZERO
315D AF           XOR A
315E D9           EXX
315F A2           AND D
3160 CDFB2F       CALL 2FFB,ZEROS-4/5
3163 07           RLCA
3164 77           LD (HL),A
3165 382E         JR C,3195,OFLOW-CLR
3167 23           INC HL
3168 77           LD (HL),A
3169 2B           DEC HL
316A 1829         JR 3195,OFLOW-CLR
316C 0620         LD B,20
316E D9           EXX
316F CB7A         BIT 7,D
3171 D9           EXX
3172 2012         JR NZ,3186,NORML-NOW
3174 07           RLCA
3175 CB13         RL E
3177 CB12         RL D
3179 D9           EXX
317A CB13         RL E
317C CB12         RL D
317E D9           EXX
317F 35           DEC (HL)
3180 28D7         JR Z,3159,NEAR-ZERO
3182 10EA         DJNZ 316E,SHIFT-ONE
3184 18D7         JR 315D,ZERO-RSLT
3186 17           RLA
3187 300C         JR NC,3195,OFLOW-CLR
3189 CD0430       CALL 3004,ADD-BACK
318C 2007         JR NZ,3195,OFLOW-CLR
318E D9           EXX
318F 1680         LD D,80
3191 D9           EXX
3192 34           INC (HL)
3193 2818         JR Z,31AD,REPORT-6
3195 E5           PUSH HL
3196 23           INC HL
3197 D9           EXX
3198 D5           PUSH DE
3199 D9           EXX
319A C1           POP BC
319B 78           LD A,B
319C 17           RLA
319D CB16         RL (HL)
319F 1F           RRA
31A0 77           LD (HL),A
31A1 23           INC HL
31A2 71           LD (HL),C
31A3 23           INC HL
31A4 72           LD (HL),D
31A5 23           INC HL
31A6 73           LD (HL),E
31A7 E1           POP HL
31A8 D1           POP DE
31A9 D9           EXX
31AA E1           POP HL
31AB D9           EXX
31AC C9           RET
31AD CF           RST 08,ERROR-1
31AE 05           DEFB 05        ;6 Number too big
31AF CD9332       CALL 3293,RE-ST-TWO
31B2 EB           EX DE,HL
31B3 AF           XOR A
31B4 CDC030       CALL 30C0,PREP-M/D
31B7 38F4         JR C,31AD,REPORT-6
31B9 EB           EX DE,HL
31BA CDC030       CALL 30C0,PREP-M/D
31BD D8           RET C
31BE D9           EXX
31BF E5           PUSH HL
31C0 D9           EXX
31C1 D5           PUSH DE
31C2 E5           PUSH HL
31C3 CDBA2F       CALL 2FBA,FETCH-TWO
31C6 D9           EXX
31C7 E5           PUSH HL
31C8 60           LD H,B
31C9 69           LD L,C
31CA D9           EXX
31CB 61           LD H,C
31CC 68           LD L,B
31CD AF           XOR A
31CE 06DF         LD B,DF
31D0 1810         JR 31E2,DIV-START
31D2 17           RLA
31D3 CB11         RL C
31D5 D9           EXX
31D6 CB11         RL C
31D8 CB10         RL B
31DA D9           EXX
31DB 29           ADD HL,HL
31DC D9           EXX
31DD ED6A         ADC HL,HL
31DF D9           EXX
31E0 3810         JR C,31F2,SUBN-ONLY
31E2 ED52         SBC HL,DE
31E4 D9           EXX
31E5 ED52         SBC HL,DE
31E7 D9           EXX
31E8 300F         JR NC,31F9,NO-RSTORE
31EA 19           ADD HL,DE
31EB D9           EXX
31EC ED5A         ADC HL,DE
31EE D9           EXX
31EF A7           AND A
31F0 1808         JR 31FA,COUNT-ONE
31F2 A7           AND A
31F3 ED52         SBC HL,DE
31F5 D9           EXX
31F6 ED52         SBC HL,DE
31F8 D9           EXX
31F9 37           SCF
31FA 04           INC B
31FB FAD231       JP M,31D2,DIV-LOOP
31FE F5           PUSH AF
31FF 28E1         JR Z,31E2,DIV-START
3201 5F           LD E,A
3202 51           LD D,C
3203 D9           EXX
3204 59           LD E,C
3205 50           LD D,B
3206 F1           POP AF
3207 CB18         RR B
3209 F1           POP AF
320A CB18         RR B
320C D9           EXX
320D C1           POP BC
320E E1           POP HL
320F 78           LD A,B
3210 91           SUB C
3211 C33D31       JP 313D,DIVN-EXPT
3214 7E           LD A,(HL)
3215 A7           AND A
3216 C8           RET Z
3217 FE81         CP 81
3219 3006         JR NC,3221,T-GR-ZERO
321B 3600         LD (HL),00
321D 3E20         LD A,20
321F 1851         JR 3272,NIL-BYTES
3221 FE91         CP 91
3223 201A         JR NZ,323F,T-SMALL
3225 23           INC HL
3226 23           INC HL
3227 23           INC HL
3228 3E80         LD A,80
322A A6           AND (HL)
322B 2B           DEC HL
322C B6           OR (HL)
322D 2B           DEC HL
322E 2003         JR NZ,3233,T-FIRST
3230 3E80         LD A,80
3232 AE           XOR (HL)
3233 2B           DEC HL
3234 2036         JR NZ,326C,T-EXPNENT
3236 77           LD (HL),A
3237 23           INC HL
3238 36FF         LD (HL),FF
323A 2B           DEC HL
323B 3E18         LD A,18
323D 1833         JR 3272,NIL-BYTES
323F 302C         JR NC,326D,X-LARGE
3241 D5           PUSH DE
3242 2F           CPL
3243 C691         ADD A,91
3245 23           INC HL
3246 56           LD D,(HL)
3247 23           INC HL
3248 5E           LD E,(HL)
3249 2B           DEC HL
324A 2B           DEC HL
324B 0E00         LD C,00
324D CB7A         BIT 7,D
324F 2801         JR Z,3252,T-NUMERIC
3251 0D           DEC C
3252 CBFA         SET 7,D
3254 0608         LD B,08
3256 90           SUB B
3257 80           ADD A,B
3258 3804         JR C,325E,T-TEST
325A 5A           LD E,D
325B 1600         LD D,00
325D 90           SUB B
325E 2807         JR Z,3267,T-STORE
3260 47           LD B,A
3261 CB3A         SRL D
3263 CB1B         RR E
3265 10FA         DJNZ 3261,T-SHIFT
3267 CD8E2D       CALL 2D8E,INT-STORE
326A D1           POP DE
326B C9           RET
326C 7E           LD A,(HL)
326D D6A0         SUB A0
326F F0           RET P
3270 ED44         NEG
3272 D5           PUSH DE
3273 EB           EX DE,HL
3274 2B           DEC HL
3275 47           LD B,A
3276 CB38         SRL B
3278 CB38         SRL B
327A CB38         SRL B
327C 2805         JR Z,3283,BITS-ZERO
327E 3600         LD (HL),00
3280 2B           DEC HL
3281 10FB         DJNZ 327E,BYTE-ZERO
3283 E607         AND 07
3285 2809         JR Z,3290,IX-END
3287 47           LD B,A
3288 3EFF         LD A,FF
328A CB27         SLA A
328C 10FC         DJNZ 328A,LESS-MASK
328E A6           AND (HL)
328F 77           LD (HL),A
3290 EB           EX DE,HL
3291 D1           POP DE
3292 C9           RET
3293 CD9632       CALL 3296,RESTK-SUB
3296 EB           EX DE,HL
3297 7E           LD A,(HL)
3298 A7           AND A
3299 C0           RET NZ
329A D5           PUSH DE
329B CD7F2D       CALL 2D7F,INT-FETCH
329E AF           XOR A
329F 23           INC HL
32A0 77           LD (HL),A
32A1 2B           DEC HL
32A2 77           LD (HL),A
32A3 0691         LD B,91
32A5 7A           LD A,D
32A6 A7           AND A
32A7 2008         JR NZ,32B1,RS-NRMLSE
32A9 B3           OR E
32AA 42           LD B,D
32AB 2810         JR Z,32BD,RS-STORE
32AD 53           LD D,E
32AE 58           LD E,B
32AF 0689         LD B,89
32B1 EB           EX DE,HL
32B2 05           DEC B
32B3 29           ADD HL,HL
32B4 30FC         JR NC,32B2,RSTK-LOOP
32B6 CB09         RRC C
32B8 CB1C         RR H
32BA CB1D         RR L
32BC EB           EX DE,HL
32BD 2B           DEC HL
32BE 73           LD (HL),E
32BF 2B           DEC HL
32C0 72           LD (HL),D
32C1 2B           DEC HL
32C2 70           LD (HL),B
32C3 D1           POP DE
32C4 C9           RET
32C5 00           NOP
32C6 B0           OR B
32C7 00           NOP
32C8 40           LD B,B
32C9 B0           OR B
32CA 00           NOP
32CB 013000       LD BC,0030
32CE F1           POP AF
32CF 49           LD C,C
32D0 0F           RRCA
32D1 DAA240       JP C,40A2
32D4 B0           OR B
32D5 00           NOP
32D6 0A           LD A,(BC)
32D7 8F           ADC A,A
32D8 363C         LD (HL),3C
32DA 34           INC (HL)
32DB A1           AND C
32DC 33           INC SP
32DD 0F           RRCA
32DE 30CA         JR NC,32AA
32E0 30AF         JR NC,3291
32E2 315138       LD SP,3851
32E5 1B           DEC DE
32E6 35           DEC (HL)
32E7 24           INC H
32E8 35           DEC (HL)
32E9 3B           DEC SP
32EA 35           DEC (HL)
32EB 3B           DEC SP
32EC 35           DEC (HL)
32ED 3B           DEC SP
32EE 35           DEC (HL)
32EF 3B           DEC SP
32F0 35           DEC (HL)
32F1 3B           DEC SP
32F2 35           DEC (HL)
32F3 3B           DEC SP
32F4 35           DEC (HL)
32F5 14           INC D
32F6 302D         JR NC,3325
32F8 35           DEC (HL)
32F9 3B           DEC SP
32FA 35           DEC (HL)
32FB 3B           DEC SP
32FC 35           DEC (HL)
32FD 3B           DEC SP
32FE 35           DEC (HL)
32FF 3B           DEC SP
3300 35           DEC (HL)
3301 3B           DEC SP
3302 35           DEC (HL)
3303 3B           DEC SP
3304 35           DEC (HL)
3305 9C           SBC A,H
3306 35           DEC (HL)
3307 DE35         SBC A,35
3309 BC           CP H
330A 34           INC (HL)
330B 45           LD B,L
330C 366E         LD (HL),6E
330E 34           INC (HL)
330F 69           LD L,C
3310 36DE         LD (HL),DE
3312 35           DEC (HL)
3313 74           LD (HL),H
3314 36B5         LD (HL),B5
3316 37           SCF
3317 AA           XOR D
3318 37           SCF
3319 DA3733       JP C,3337
331C 3843         JR C,3361
331E 38E2         JR C,3302
3320 37           SCF
3321 13           INC DE
3322 37           SCF
3323 C436AF       CALL NZ,AF36
3326 364A         LD (HL),4A
3328 3892         JR C,32BC
332A 34           INC (HL)
332B 6A           LD L,D
332C 34           INC (HL)
332D AC           XOR H
332E 34           INC (HL)
332F A5           AND L
3330 34           INC (HL)
3331 B3           OR E
3332 34           INC (HL)
3333 1F           RRA
3334 36C9         LD (HL),C9
3336 35           DEC (HL)
3337 0135C0       LD BC,C035
333A 33           INC SP
333B A0           AND B
333C 3686         LD (HL),86
333E 36C6         LD (HL),C6
3340 33           INC SP
3341 7A           LD A,D
3342 3606         LD (HL),06
3344 35           DEC (HL)
3345 F9           LD SP,HL
3346 34           INC (HL)
3347 9B           SBC A,E
3348 3683         LD (HL),83
334A 37           SCF
334B 14           INC D
334C 32A233       LD (33A2),A  {fp-calc-2}
334F 4F           LD C,A
3350 2D           DEC L
3351 97           SUB A
3352 324934       LD (3449),A  {series-06}
3355 1B           DEC DE
3356 34           INC (HL)
3357 2D           DEC L
3358 34           INC (HL)
3359 0F           RRCA
335A 34           INC (HL)
335B CDBF35       CALL 35BF,STK-PNTRS
335E 78           LD A,B
335F 32675C       LD (5C67),A  {BREG}
3362 D9           EXX
3363 E3           EX SP,(HL)
3364 D9           EXX
3365 ED53655C     LD (5C65),DE  {STKEND}
3369 D9           EXX
336A 7E           LD A,(HL)
336B 23           INC HL
336C E5           PUSH HL
336D A7           AND A
336E F28033       JP P,3380,FIRST-3D
3371 57           LD D,A
3372 E660         AND 60
3374 0F           RRCA
3375 0F           RRCA
3376 0F           RRCA
3377 0F           RRCA
3378 C67C         ADD A,7C
337A 6F           LD L,A
337B 7A           LD A,D
337C E61F         AND 1F
337E 180E         JR 338E,ENT-TABLE
3380 FE18         CP 18
3382 3008         JR NC,338C,DOUBLE-A
3384 D9           EXX
3385 01FBFF       LD BC,FFFB
3388 54           LD D,H
3389 5D           LD E,L
338A 09           ADD HL,BC
338B D9           EXX
338C 07           RLCA
338D 6F           LD L,A
338E 11D732       LD DE,32D7
3391 2600         LD H,00
3393 19           ADD HL,DE
3394 5E           LD E,(HL)
3395 23           INC HL
3396 56           LD D,(HL)
3397 216533       LD HL,3365
339A E3           EX SP,(HL)
339B D5           PUSH DE
339C D9           EXX
339D ED4B665C     LD BC,(5C66)
33A1 C9           RET
33A2 F1           POP AF
33A3 3A675C       LD A,(5C67)  {BREG}
33A6 D9           EXX
33A7 18C3         JR 336C,SCAN-ENT
33A9 D5           PUSH DE
33AA E5           PUSH HL
33AB 010500       LD BC,0005
33AE CD051F       CALL 1F05,TEST-ROOM
33B1 E1           POP HL
33B2 D1           POP DE
33B3 C9           RET
33B4 ED5B655C     LD DE,(5C65)  {STKEND}
33B8 CDC033       CALL 33C0,MOVE-FP
33BB ED53655C     LD (5C65),DE  {STKEND}
33BF C9           RET
33C0 CDA933       CALL 33A9,TEST-5-SP
33C3 EDB0         LDIR
33C5 C9           RET
33C6 62           LD H,D
33C7 6B           LD L,E
33C8 CDA933       CALL 33A9,TEST-5-SP
33CB D9           EXX
33CC E5           PUSH HL
33CD D9           EXX
33CE E3           EX SP,(HL)
33CF C5           PUSH BC
33D0 7E           LD A,(HL)
33D1 E6C0         AND C0
33D3 07           RLCA
33D4 07           RLCA
33D5 4F           LD C,A
33D6 0C           INC C
33D7 7E           LD A,(HL)
33D8 E63F         AND 3F
33DA 2002         JR NZ,33DE,FORM-EXP
33DC 23           INC HL
33DD 7E           LD A,(HL)
33DE C650         ADD A,50
33E0 12           LD (DE),A
33E1 3E05         LD A,05
33E3 91           SUB C
33E4 23           INC HL
33E5 13           INC DE
33E6 0600         LD B,00
33E8 EDB0         LDIR
33EA C1           POP BC
33EB E3           EX SP,(HL)
33EC D9           EXX
33ED E1           POP HL
33EE D9           EXX
33EF 47           LD B,A
33F0 AF           XOR A
33F1 05           DEC B
33F2 C8           RET Z
33F3 12           LD (DE),A
33F4 13           INC DE
33F5 18FA         JR 33F1,STK-ZEROS
33F7 A7           AND A
33F8 C8           RET Z
33F9 F5           PUSH AF
33FA D5           PUSH DE
33FB 110000       LD DE,0000
33FE CDC833       CALL 33C8,STK-CONST
3401 D1           POP DE
3402 F1           POP AF
3403 3D           DEC A
3404 18F2         JR 33F8,SKIP-NEXT
3406 4F           LD C,A
3407 07           RLCA
3408 07           RLCA
3409 81           ADD A,C
340A 4F           LD C,A
340B 0600         LD B,00
340D 09           ADD HL,BC
340E C9           RET
340F D5           PUSH DE
3410 2A685C       LD HL,(5C68)  {MEM}
3413 CD0634       CALL 3406,LOC-MEM
3416 CDC033       CALL 33C0,MOVE-FP
3419 E1           POP HL
341A C9           RET
341B 62           LD H,D
341C 6B           LD L,E
341D D9           EXX
341E E5           PUSH HL
341F 21C532       LD HL,32C5
3422 D9           EXX
3423 CDF733       CALL 33F7,SKIP-CONS
3426 CDC833       CALL 33C8,STK-CONST
3429 D9           EXX
342A E1           POP HL
342B D9           EXX
342C C9           RET
342D E5           PUSH HL
342E EB           EX DE,HL
342F 2A685C       LD HL,(5C68)  {MEM}
3432 CD0634       CALL 3406,LOC-MEM
3435 EB           EX DE,HL
3436 CDC033       CALL 33C0,MOVE-FP
3439 EB           EX DE,HL
343A E1           POP HL
343B C9           RET
343C 0605         LD B,05
343E 1A           LD A,(DE)
343F 4E           LD C,(HL)
3440 EB           EX DE,HL
3441 12           LD (DE),A
3442 71           LD (HL),C
3443 23           INC HL
3444 13           INC DE
3445 10F7         DJNZ 343E,SWAP-BYTE
3447 EB           EX DE,HL
3448 C9           RET
3449 47           LD B,A
344A CD5E33       CALL 335E,GEN-ENT-1
344D 310FC0       LD SP,C00F
3450 02           LD (BC),A
3451 A0           AND B
3452 C231E0       JP NZ,E031
3455 04           INC B
3456 E2C103       JP PO,03C1
3459 38CD         JR C,3428
345B C633         ADD A,33
345D CD6233       CALL 3362,GEN-ENT-2
3460 0F           RRCA
3461 01C202       LD BC,02C2
3464 35           DEC (HL)
3465 EEE1         XOR E1
3467 03           INC BC
3468 38C9         JR C,3433
346A 06FF         LD B,FF
346C 1806         JR 3474,NEG-TEST
346E CDE934       CALL 34E9,TEST-ZERO
3471 D8           RET C
3472 0600         LD B,00
3474 7E           LD A,(HL)
3475 A7           AND A
3476 280B         JR Z,3483,INT-CASE
3478 23           INC HL
3479 78           LD A,B
347A E680         AND 80
347C B6           OR (HL)
347D 17           RLA
347E 3F           CCF
347F 1F           RRA
3480 77           LD (HL),A
3481 2B           DEC HL
3482 C9           RET
3483 D5           PUSH DE
3484 E5           PUSH HL
3485 CD7F2D       CALL 2D7F,INT-FETCH
3488 E1           POP HL
3489 78           LD A,B
348A B1           OR C
348B 2F           CPL
348C 4F           LD C,A
348D CD8E2D       CALL 2D8E,INT-STORE
3490 D1           POP DE
3491 C9           RET
3492 CDE934       CALL 34E9,TEST-ZERO
3495 D8           RET C
3496 D5           PUSH DE
3497 110100       LD DE,0001
349A 23           INC HL
349B CB16         RL (HL)
349D 2B           DEC HL
349E 9F           SBC A,A
349F 4F           LD C,A
34A0 CD8E2D       CALL 2D8E,INT-STORE
34A3 D1           POP DE
34A4 C9           RET
34A5 CD991E       CALL 1E99,FIND-INT2
34A8 ED78         IN A,(C)
34AA 1804         JR 34B0,IN-PK-STK
34AC CD991E       CALL 1E99,FIND-INT2
34AF 0A           LD A,(BC)
34B0 C3282D       JP 2D28,STACK-A
34B3 CD991E       CALL 1E99,FIND-INT2
34B6 212B2D       LD HL,2D2B
34B9 E5           PUSH HL
34BA C5           PUSH BC
34BB C9           RET
34BC CDF12B       CALL 2BF1,STK-FETCH
34BF 0B           DEC BC
34C0 78           LD A,B
34C1 B1           OR C
34C2 2023         JR NZ,34E7,REPORT-A
34C4 1A           LD A,(DE)
34C5 CD8D2C       CALL 2C8D,ALPHA
34C8 3809         JR C,34D3,USR-RANGE
34CA D690         SUB 90
34CC 3819         JR C,34E7,REPORT-A
34CE FE15         CP 15
34D0 3015         JR NC,34E7,REPORT-A
34D2 3C           INC A
34D3 3D           DEC A
34D4 87           ADD A,A
34D5 87           ADD A,A
34D6 87           ADD A,A
34D7 FEA8         CP A8
34D9 300C         JR NC,34E7,REPORT-A
34DB ED4B7B5C     LD BC,(5C7B)  {UDG}
34DF 81           ADD A,C
34E0 4F           LD C,A
34E1 3001         JR NC,34E4,USR-STACK
34E3 04           INC B
34E4 C32B2D       JP 2D2B,STACK-BC
34E7 CF           RST 08,ERROR-1
34E8 09           DEFB 09        ;A Invalid argument
34E9 E5           PUSH HL
34EA C5           PUSH BC
34EB 47           LD B,A
34EC 7E           LD A,(HL)
34ED 23           INC HL
34EE B6           OR (HL)
34EF 23           INC HL
34F0 B6           OR (HL)
34F1 23           INC HL
34F2 B6           OR (HL)
34F3 78           LD A,B
34F4 C1           POP BC
34F5 E1           POP HL
34F6 C0           RET NZ
34F7 37           SCF
34F8 C9           RET
34F9 CDE934       CALL 34E9,TEST-ZERO
34FC D8           RET C
34FD 3EFF         LD A,FF
34FF 1806         JR 3507,SIGN-TO-C
3501 CDE934       CALL 34E9,TEST-ZERO
3504 1805         JR 350B,FP-0/1
3506 AF           XOR A
3507 23           INC HL
3508 AE           XOR (HL)
3509 2B           DEC HL
350A 07           RLCA
350B E5           PUSH HL
350C 3E00         LD A,00
350E 77           LD (HL),A
350F 23           INC HL
3510 77           LD (HL),A
3511 23           INC HL
3512 17           RLA
3513 77           LD (HL),A
3514 1F           RRA
3515 23           INC HL
3516 77           LD (HL),A
3517 23           INC HL
3518 77           LD (HL),A
3519 E1           POP HL
351A C9           RET
351B EB           EX DE,HL
351C CDE934       CALL 34E9,TEST-ZERO
351F EB           EX DE,HL
3520 D8           RET C
3521 37           SCF
3522 18E7         JR 350B,FP-0/1
3524 EB           EX DE,HL
3525 CDE934       CALL 34E9,TEST-ZERO
3528 EB           EX DE,HL
3529 D0           RET NC
352A A7           AND A
352B 18DE         JR 350B,FP-0/1
352D EB           EX DE,HL
352E CDE934       CALL 34E9,TEST-ZERO
3531 EB           EX DE,HL
3532 D0           RET NC
3533 D5           PUSH DE
3534 1B           DEC DE
3535 AF           XOR A
3536 12           LD (DE),A
3537 1B           DEC DE
3538 12           LD (DE),A
3539 D1           POP DE
353A C9           RET
353B 78           LD A,B
353C D608         SUB 08
353E CB57         BIT 2,A
3540 2001         JR NZ,3543,EX-OR-NOT
3542 3D           DEC A
3543 0F           RRCA
3544 3008         JR NC,354E,NU-OR-STR
3546 F5           PUSH AF
3547 E5           PUSH HL
3548 CD3C34       CALL 343C,EXCHANGE
354B D1           POP DE
354C EB           EX DE,HL
354D F1           POP AF
354E CB57         BIT 2,A
3550 2007         JR NZ,3559,STRINGS
3552 0F           RRCA
3553 F5           PUSH AF
3554 CD0F30       CALL 300F,SUBTRACT
3557 1833         JR 358C,END-TESTS
3559 0F           RRCA
355A F5           PUSH AF
355B CDF12B       CALL 2BF1,STK-FETCH
355E D5           PUSH DE
355F C5           PUSH BC
3560 CDF12B       CALL 2BF1,STK-FETCH
3563 E1           POP HL
3564 7C           LD A,H
3565 B5           OR L
3566 E3           EX SP,(HL)
3567 78           LD A,B
3568 200B         JR NZ,3575,SEC-PLUS
356A B1           OR C
356B C1           POP BC
356C 2804         JR Z,3572,BOTH-NULL
356E F1           POP AF
356F 3F           CCF
3570 1816         JR 3588,STR-TEST
3572 F1           POP AF
3573 1813         JR 3588,STR-TEST
3575 B1           OR C
3576 280D         JR Z,3585,FRST-LESS
3578 1A           LD A,(DE)
3579 96           SUB (HL)
357A 3809         JR C,3585,FRST-LESS
357C 20ED         JR NZ,356B,SECND-LOW
357E 0B           DEC BC
357F 13           INC DE
3580 23           INC HL
3581 E3           EX SP,(HL)
3582 2B           DEC HL
3583 18DF         JR 3564,BYTE-COMP
3585 C1           POP BC
3586 F1           POP AF
3587 A7           AND A
3588 F5           PUSH AF
3589 EF           RST 28,FP-CALC
358A A0           DEFB A0,stk-zero
358B 38           DEFB 38,end-calc
358C F1           POP AF
358D F5           PUSH AF
358E DC0135       CALL C,3501,NOT
3591 F1           POP AF
3592 F5           PUSH AF
3593 D4F934       CALL NC,34F9,GREATER-0
3596 F1           POP AF
3597 0F           RRCA
3598 D40135       CALL NC,3501,NOT
359B C9           RET
359C CDF12B       CALL 2BF1,STK-FETCH
359F D5           PUSH DE
35A0 C5           PUSH BC
35A1 CDF12B       CALL 2BF1,STK-FETCH
35A4 E1           POP HL
35A5 E5           PUSH HL
35A6 D5           PUSH DE
35A7 C5           PUSH BC
35A8 09           ADD HL,BC
35A9 44           LD B,H
35AA 4D           LD C,L
35AB F7           RST 30,BC-SPACES
35AC CDB22A       CALL 2AB2,STK-STO-$
35AF C1           POP BC
35B0 E1           POP HL
35B1 78           LD A,B
35B2 B1           OR C
35B3 2802         JR Z,35B7,OTHER-STR
35B5 EDB0         LDIR
35B7 C1           POP BC
35B8 E1           POP HL
35B9 78           LD A,B
35BA B1           OR C
35BB 2802         JR Z,35BF,STK-PNTRS
35BD EDB0         LDIR
35BF 2A655C       LD HL,(5C65)  {STKEND}
35C2 11FBFF       LD DE,FFFB
35C5 E5           PUSH HL
35C6 19           ADD HL,DE
35C7 D1           POP DE
35C8 C9           RET
35C9 CDD52D       CALL 2DD5,FP-TO-A
35CC 380E         JR C,35DC,REPORT-B
35CE 200C         JR NZ,35DC,REPORT-B
35D0 F5           PUSH AF
35D1 010100       LD BC,0001
35D4 F7           RST 30,BC-SPACES
35D5 F1           POP AF
35D6 12           LD (DE),A
35D7 CDB22A       CALL 2AB2,STK-STO-$
35DA EB           EX DE,HL
35DB C9           RET
35DC CF           RST 08,ERROR-1
35DD 0A           DEFB 0A        ;B Integer out of range
35DE 2A5D5C       LD HL,(5C5D)  {CH-ADD}
35E1 E5           PUSH HL
35E2 78           LD A,B
35E3 C6E3         ADD A,E3
35E5 9F           SBC A,A
35E6 F5           PUSH AF
35E7 CDF12B       CALL 2BF1,STK-FETCH
35EA D5           PUSH DE
35EB 03           INC BC
35EC F7           RST 30,BC-SPACES
35ED E1           POP HL
35EE ED535D5C     LD (5C5D),DE  {CH-ADD}
35F2 D5           PUSH DE
35F3 EDB0         LDIR
35F5 EB           EX DE,HL
35F6 2B           DEC HL
35F7 360D         LD (HL),0D
35F9 FDCB01BE     RES 7,(IY+01)
35FD CDFB24       CALL 24FB,SCANNING
3600 DF           RST 18,GET-CHAR
3601 FE0D         CP 0D
3603 2007         JR NZ,360C,V-RPORT-C
3605 E1           POP HL
3606 F1           POP AF
3607 FDAE01       XOR (IY+01)
360A E640         AND 40
360C C28A1C       JP NZ,1C8A,REPORT-C
360F 225D5C       LD (5C5D),HL  {CH-ADD}
3612 FDCB01FE     SET 7,(IY+01)
3616 CDFB24       CALL 24FB,SCANNING
3619 E1           POP HL
361A 225D5C       LD (5C5D),HL  {CH-ADD}
361D 18A0         JR 35BF,STK-PNTRS
361F 010100       LD BC,0001
3622 F7           RST 30,BC-SPACES
3623 225B5C       LD (5C5B),HL  {K-CUR}
3626 E5           PUSH HL
3627 2A515C       LD HL,(5C51)  {CURCHL}
362A E5           PUSH HL
362B 3EFF         LD A,FF
362D CD0116       CALL 1601,CHAN-OPEN
3630 CDE32D       CALL 2DE3,PRINT-FP
3633 E1           POP HL
3634 CD1516       CALL 1615,CHAN-FLAG
3637 D1           POP DE
3638 2A5B5C       LD HL,(5C5B)  {K-CUR}
363B A7           AND A
363C ED52         SBC HL,DE
363E 44           LD B,H
363F 4D           LD C,L
3640 CDB22A       CALL 2AB2,STK-STO-$
3643 EB           EX DE,HL
3644 C9           RET
3645 CD941E       CALL 1E94,FIND-INT1
3648 FE10         CP 10
364A D29F1E       JP NC,1E9F,REPORT-B
364D 2A515C       LD HL,(5C51)  {CURCHL}
3650 E5           PUSH HL
3651 CD0116       CALL 1601,CHAN-OPEN
3654 CDE615       CALL 15E6,INPUT-AD
3657 010000       LD BC,0000
365A 3003         JR NC,365F,R-I-STORE
365C 0C           INC C
365D F7           RST 30,BC-SPACES
365E 12           LD (DE),A
365F CDB22A       CALL 2AB2,STK-STO-$
3662 E1           POP HL
3663 CD1516       CALL 1615,CHAN-FLAG
3666 C3BF35       JP 35BF,STK-PNTRS
3669 CDF12B       CALL 2BF1,STK-FETCH
366C 78           LD A,B
366D B1           OR C
366E 2801         JR Z,3671,STK-CODE
3670 1A           LD A,(DE)
3671 C3282D       JP 2D28,STACK-A
3674 CDF12B       CALL 2BF1,STK-FETCH
3677 C32B2D       JP 2D2B,STACK-BC
367A D9           EXX
367B E5           PUSH HL
367C 21675C       LD HL,5C67
367F 35           DEC (HL)
3680 E1           POP HL
3681 2004         JR NZ,3687,JUMP-2
3683 23           INC HL
3684 D9           EXX
3685 C9           RET
3686 D9           EXX
3687 5E           LD E,(HL)
3688 7B           LD A,E
3689 17           RLA
368A 9F           SBC A,A
368B 57           LD D,A
368C 19           ADD HL,DE
368D D9           EXX
368E C9           RET
368F 13           INC DE
3690 13           INC DE
3691 1A           LD A,(DE)
3692 1B           DEC DE
3693 1B           DEC DE
3694 A7           AND A
3695 20EF         JR NZ,3686,JUMP
3697 D9           EXX
3698 23           INC HL
3699 D9           EXX
369A C9           RET
369B F1           POP AF
369C D9           EXX
369D E3           EX SP,(HL)
369E D9           EXX
369F C9           RET
36A0 EF           RST 28,FP-CALC
36A1 C0           DEFB C0,st-mem-0
36A2 02           DEFB 02,delete
36A3 31           DEFB 31,duplicate
36A4 E0           DEFB E0,get-mem-0
36A5 05           DEFB 05,division
36A6 27           DEFB 27,int
36A7 E0           DEFB E0,get-mem-0
36A8 01           DEFB 01,exchange
36A9 C0           DEFB C0,st-mem-0
36AA 04           DEFB 04,multiply
36AB 03           DEFB 03,subtract
36AC E0           DEFB E0,get-mem-0
36AD 38           DEFB 38,end-calc
36AE C9           RET
36AF EF           RST 28,FP-CALC
36B0 31           DEFB 31,duplicate
36B1 36           DEFB 36,less-0
36B2 00           DEFB 00,jump-true
36B3 04           DEFB 04,multiply
36B4 3A           DEFB 3A,truncate
36B5 38           DEFB 38,end-calc
36B6 C9           RET
36B7 313AC0       LD SP,C03A
36BA 03           INC BC
36BB E0           RET PO
36BC 013000       LD BC,0030
36BF 03           INC BC
36C0 A1           AND C
36C1 03           INC BC
36C2 38C9         JR C,368D
36C4 EF           RST 28,FP-CALC
36C5 3D           DEFB 3D,re-stack
36C6 34           DEFB 34,stk-data
36C7 F138AA3B29   DEFB F1,38,AA,3B,29        ;1.71547652
36CC 04           DEFB 04,multiply
36CD 31           DEFB 31,duplicate
36CE 27           DEFB 27,int
36CF C3           DEFB C3,st-mem-3
36D0 03           DEFB 03,subtract
36D1 31           DEFB 31,duplicate
36D2 0F           DEFB 0F,addition
36D3 A1           DEFB A1,stk-one
36D4 03           DEFB 03,subtract
36D5 88           DEFB 88,series-08
36D6 1336         DEFB 13,36        ;5.Bexp-8
36D8 586566       DEFB 58,65,66        ;0.E566exp-6
36DB 9D786540     DEFB 9D,78,65,40        ;0.00001F0CA8
36DF A26032C9     DEFB A2,60,32,C9        ;0.000380CB24
36E3 E721F7AF24   DEFB E7,21,F7,AF,24        ;0.0050FBD792
36E8 EB2FB0B014   DEFB EB,2F,B0,B0,14        ;0.057D8580A
36ED EE7EBB9458   DEFB EE,7E,BB,94,58        ;0.3FAEE516
36F2 F13A7EF8CF   DEFB F1,3A,7E,F8,CF        ;1.74FDF19E
36F7 E3           DEFB E3,get-mem-3
36F8 38           DEFB 38,end-calc
36F9 CDD52D       CALL 2DD5,FP-TO-A
36FC 2007         JR NZ,3705,N-NEGTV
36FE 3803         JR C,3703,REPORT-6
3700 86           ADD A,(HL)
3701 3009         JR NC,370C,RESULT-OK
3703 CF           RST 08,ERROR-1
3704 05           DEFB 05        ;6 Number too big
3705 3807         JR C,370E,RSLT-ZERO
3707 96           SUB (HL)
3708 3004         JR NC,370E,RSLT-ZERO
370A ED44         NEG
370C 77           LD (HL),A
370D C9           RET
370E EF           RST 28,FP-CALC
370F 02           DEFB 02,delete
3710 A0           DEFB A0,stk-zero
3711 38           DEFB 38,end-calc
3712 C9           RET
3713 EF           RST 28,FP-CALC
3714 3D           DEFB 3D,re-stack
3715 31           DEFB 31,duplicate
3716 37           DEFB 37,greater-0
3717 00           DEFB 00,jump-true
3718 04           DEFB 04,multiply
3719 38           DEFB 38,end-calc
371A CF           RST 08,ERROR-1
371B 09           DEFB 09        ;A Invalid argument
371C A0           AND B
371D 02           LD (BC),A
371E 387E         JR C,379E
3720 3680         LD (HL),80
3722 CD282D       CALL 2D28,STACK-A
3725 EF           RST 28,FP-CALC
3726 34           DEFB 34,stk-data
3727 3800         DEFB 38,00        ;080
3729 03           DEFB 03,subtract
372A 01           DEFB 01,exchange
372B 31           DEFB 31,duplicate
372C 34           DEFB 34,stk-data
372D F04CCCCCCD   DEFB F0,4C,CC,CC,CD        ;0.CCCCCCCD
3732 03           DEFB 03,subtract
3733 37           DEFB 37,greater-0
3734 00           DEFB 00,jump-true
3735 08           DEFB 08,no-&-no
3736 01           DEFB 01,exchange
3737 A1           DEFB A1,stk-one
3738 03           DEFB 03,subtract
3739 01           DEFB 01,exchange
373A 38           DEFB 38,end-calc
373B 34           INC (HL)
373C EF           RST 28,FP-CALC
373D 01           DEFB 01,exchange
373E 34           DEFB 34,stk-data
373F F0317217F8   DEFB F0,31,72,17,F8        ;0.B17217F8
3744 04           DEFB 04,multiply
3745 01           DEFB 01,exchange
3746 A2           DEFB A2,stk-half
3747 03           DEFB 03,subtract
3748 A2           DEFB A2,stk-half
3749 03           DEFB 03,subtract
374A 31           DEFB 31,duplicate
374B 34           DEFB 34,stk-data
374C 3220         DEFB 32,20        ;2.8
374E 04           DEFB 04,multiply
374F A2           DEFB A2,stk-half
3750 03           DEFB 03,subtract
3751 8C           DEFB 8C,series-0C
3752 11AC         DEFB 11,AC        ;-1.58exp-8
3754 1409         DEFB 14,09        ;0.89exp-7
3756 56DAA5       DEFB 56,DA,A5        ;-3.6A94exp-7
3759 5930C5       DEFB 59,30,C5        ;1.618Aexp-6
375C 5C90AA       DEFB 5C,90,AA        ;-0.0000090AA
375F 9E706F61     DEFB 9E,70,6F,61        ;0.00003C1BD84
3763 A1CBDA96     DEFB A1,CB,DA,96        ;-0.000197B52C
3767 A4319FB4     DEFB A4,31,9F,B4        ;0.000B19FB4
376B E7A0FE5CFC   DEFB E7,A0,FE,5C,FC        ;-0.00507F2E7E
3770 EA1B43CA36   DEFB EA,1B,43,CA,36        ;0.026D0F28D8
3775 EDA79C7E5E   DEFB ED,A7,9C,7E,5E        ;-0.14F38FCBC
377A F06E238093   DEFB F0,6E,23,80,93        ;0.EE238093
377F 04           DEFB 04,multiply
3780 0F           DEFB 0F,addition
3781 38           DEFB 38,end-calc
3782 C9           RET
3783 EF           RST 28,FP-CALC
3784 3D           DEFB 3D,re-stack
3785 34           DEFB 34,stk-data
3786 EE22F9836E   DEFB EE,22,F9,83,6E        ;0.28BE60DB8
378B 04           DEFB 04,multiply
378C 31           DEFB 31,duplicate
378D A2           DEFB A2,stk-half
378E 0F           DEFB 0F,addition
378F 27           DEFB 27,int
3790 03           DEFB 03,subtract
3791 31           DEFB 31,duplicate
3792 0F           DEFB 0F,addition
3793 31           DEFB 31,duplicate
3794 0F           DEFB 0F,addition
3795 31           DEFB 31,duplicate
3796 2A           DEFB 2A,abs
3797 A1           DEFB A1,stk-one
3798 03           DEFB 03,subtract
3799 31           DEFB 31,duplicate
379A 37           DEFB 37,greater-0
379B C0           DEFB C0,st-mem-0
379C 00           DEFB 00,jump-true
379D 04           DEFB 04,multiply
379E 02           DEFB 02,delete
379F 38           DEFB 38,end-calc
37A0 C9           RET
37A1 A1           AND C
37A2 03           INC BC
37A3 013600       LD BC,0036
37A6 02           LD (BC),A
37A7 1B           DEC DE
37A8 38C9         JR C,3773
37AA EF           RST 28,FP-CALC
37AB 39           DEFB 39,get-argt
37AC 2A           DEFB 2A,abs
37AD A1           DEFB A1,stk-one
37AE 03           DEFB 03,subtract
37AF E0           DEFB E0,get-mem-0
37B0 00           DEFB 00,jump-true
37B1 06           DEFB 06,to-power
37B2 1B           DEFB 1B,negate
37B3 3303         DEFB 33,03,jump 37B8
37B5 EF           DEFB EF,get-mem-F
37B6 39           DEFB 39,get-argt
37B7 31           DEFB 31,duplicate
37B8 31           DEFB 31,duplicate
37B9 04           DEFB 04,multiply
37BA 31           DEFB 31,duplicate
37BB 0F           DEFB 0F,addition
37BC A1           DEFB A1,stk-one
37BD 03           DEFB 03,subtract
37BE 86           DEFB 86,series-06
37BF 14E6         DEFB 14,E6        ;-0.E6exp-7
37C1 5C1F0B       DEFB 5C,1F,0B        ;0.000009F0B
37C4 A38F38EE     DEFB A3,8F,38,EE        ;-0.000479C77
37C8 E91563BB23   DEFB E9,15,63,BB,23        ;0.012AC77646
37CD EE920DCDED   DEFB EE,92,0D,CD,ED        ;-0.2483737B4
37D2 F1235D1BEA   DEFB F1,23,5D,1B,EA        ;1.46BA37D4
37D7 04           DEFB 04,multiply
37D8 38           DEFB 38,end-calc
37D9 C9           RET
37DA EF           RST 28,FP-CALC
37DB 31           DEFB 31,duplicate
37DC 1F           DEFB 1F,sin
37DD 01           DEFB 01,exchange
37DE 20           DEFB 20,cos
37DF 05           DEFB 05,division
37E0 38           DEFB 38,end-calc
37E1 C9           RET
37E2 CD9732       CALL 3297,RE-STACK
37E5 7E           LD A,(HL)
37E6 FE81         CP 81
37E8 380E         JR C,37F8,SMALL
37EA EF           RST 28,FP-CALC
37EB A1           DEFB A1,stk-one
37EC 1B           DEFB 1B,negate
37ED 01           DEFB 01,exchange
37EE 05           DEFB 05,division
37EF 31           DEFB 31,duplicate
37F0 36           DEFB 36,less-0
37F1 A3           DEFB A3,stk-pi/2
37F2 01           DEFB 01,exchange
37F3 00           DEFB 00,jump-true
37F4 06           DEFB 06,to-power
37F5 1B           DEFB 1B,negate
37F6 3303         DEFB 33,03,jump 37FB
37F8 EF           DEFB EF,get-mem-F
37F9 A0           DEFB A0,stk-zero
37FA 01           DEFB 01,exchange
37FB 31           DEFB 31,duplicate
37FC 31           DEFB 31,duplicate
37FD 04           DEFB 04,multiply
37FE 31           DEFB 31,duplicate
37FF 0F           DEFB 0F,addition
3800 A1           DEFB A1,stk-one
3801 03           DEFB 03,subtract
3802 8C           DEFB 8C,series-0C
3803 10B2         DEFB 10,B2        ;-0.B2exp-8
3805 130E         DEFB 13,0E        ;4.7exp-8
3807 55E48D       DEFB 55,E4,8D        ;-1.C91Aexp-7
380A 5839BC       DEFB 58,39,BC        ;0.B9BCexp-6
380D 5B98FD       DEFB 5B,98,FD        ;-4.C7E8exp-6
3810 9E003675     DEFB 9E,00,36,75        ;0.0000200D9D4
3814 A0DBE8B4     DEFB A0,DB,E8,B4        ;-0.0000DBE8B4
3818 6342C4       DEFB 63,42,C4        ;0.0006162
381B E6B50936BE   DEFB E6,B5,09,36,BE        ;-0.002D424DAF8
3820 E936731B5D   DEFB E9,36,73,1B,5D        ;0.016CE636BA
3825 ECD8DE63BE   DEFB EC,D8,DE,63,BE        ;-0.0D8DE63BE
382A F061A1B30C   DEFB F0,61,A1,B3,0C        ;0.E1A1B30C
382F 04           DEFB 04,multiply
3830 0F           DEFB 0F,addition
3831 38           DEFB 38,end-calc
3832 C9           RET
3833 EF           RST 28,FP-CALC
3834 31           DEFB 31,duplicate
3835 31           DEFB 31,duplicate
3836 04           DEFB 04,multiply
3837 A1           DEFB A1,stk-one
3838 03           DEFB 03,subtract
3839 1B           DEFB 1B,negate
383A 28           DEFB 28,sqr
383B A1           DEFB A1,stk-one
383C 0F           DEFB 0F,addition
383D 05           DEFB 05,division
383E 24           DEFB 24,atn
383F 31           DEFB 31,duplicate
3840 0F           DEFB 0F,addition
3841 38           DEFB 38,end-calc
3842 C9           RET
3843 EF           RST 28,FP-CALC
3844 22           DEFB 22,asn
3845 A3           DEFB A3,stk-pi/2
3846 03           DEFB 03,subtract
3847 1B           DEFB 1B,negate
3848 38           DEFB 38,end-calc
3849 C9           RET
384A EF           RST 28,FP-CALC
384B 31           DEFB 31,duplicate
384C 30           DEFB 30,not
384D 00           DEFB 00,jump-true
384E 1E           DEFB 1E,len
384F A2           DEFB A2,stk-half
3850 38           DEFB 38,end-calc
3851 EF           RST 28,FP-CALC
3852 01           DEFB 01,exchange
3853 31           DEFB 31,duplicate
3854 30           DEFB 30,not
3855 00           DEFB 00,jump-true
3856 07           DEFB 07,or
3857 25           DEFB 25,ln
3858 04           DEFB 04,multiply
3859 38           DEFB 38,end-calc
385A C3C436       JP 36C4,EXP
385D 02           LD (BC),A
385E 313000       LD SP,0030
3861 09           ADD HL,BC
3862 A0           AND B
3863 013700       LD BC,0037
3866 06A1         LD B,A1
3868 010502       LD BC,0205
386B A1           AND C
386C 38C9         JR C,3837
386E F3           DI
386F 217D38       LD HL,387D
3872 110080       LD DE,8000
3875 010700       LD BC,0007
3878 EDB0         LDIR
387A C30080       JP 8000
387D 3E01         LD A,01
387F D37E         OUT (7E),A
3881 C30000       JP 0000,START
3884 FF           RST 38,MASK-INT
3885 FF           RST 38,MASK-INT
3886 FF           RST 38,MASK-INT
3887 FF           RST 38,MASK-INT
3888 FF           RST 38,MASK-INT
3889 FF           RST 38,MASK-INT
388A FF           RST 38,MASK-INT
388B FF           RST 38,MASK-INT
388C FF           RST 38,MASK-INT
388D FF           RST 38,MASK-INT
388E FF           RST 38,MASK-INT
388F FF           RST 38,MASK-INT
3890 FF           RST 38,MASK-INT
3891 FF           RST 38,MASK-INT
3892 FF           RST 38,MASK-INT
3893 FF           RST 38,MASK-INT
3894 FF           RST 38,MASK-INT
3895 FF           RST 38,MASK-INT
3896 FF           RST 38,MASK-INT
3897 FF           RST 38,MASK-INT
3898 FF           RST 38,MASK-INT
3899 FF           RST 38,MASK-INT
389A FF           RST 38,MASK-INT
389B FF           RST 38,MASK-INT
389C FF           RST 38,MASK-INT
389D FF           RST 38,MASK-INT
389E FF           RST 38,MASK-INT
389F FF           RST 38,MASK-INT
38A0 FF           RST 38,MASK-INT
38A1 FF           RST 38,MASK-INT
38A2 FF           RST 38,MASK-INT
38A3 FF           RST 38,MASK-INT
38A4 FF           RST 38,MASK-INT
38A5 FF           RST 38,MASK-INT
38A6 FF           RST 38,MASK-INT
38A7 FF           RST 38,MASK-INT
38A8 FF           RST 38,MASK-INT
38A9 FF           RST 38,MASK-INT
38AA FF           RST 38,MASK-INT
38AB FF           RST 38,MASK-INT
38AC FF           RST 38,MASK-INT
38AD FF           RST 38,MASK-INT
38AE FF           RST 38,MASK-INT
38AF FF           RST 38,MASK-INT
38B0 FF           RST 38,MASK-INT
38B1 FF           RST 38,MASK-INT
38B2 FF           RST 38,MASK-INT
38B3 FF           RST 38,MASK-INT
38B4 FF           RST 38,MASK-INT
38B5 FF           RST 38,MASK-INT
38B6 FF           RST 38,MASK-INT
38B7 FF           RST 38,MASK-INT
38B8 FF           RST 38,MASK-INT
38B9 FF           RST 38,MASK-INT
38BA FF           RST 38,MASK-INT
38BB FF           RST 38,MASK-INT
38BC FF           RST 38,MASK-INT
38BD FF           RST 38,MASK-INT
38BE FF           RST 38,MASK-INT
38BF FF           RST 38,MASK-INT
38C0 FF           RST 38,MASK-INT
38C1 FF           RST 38,MASK-INT
38C2 FF           RST 38,MASK-INT
38C3 FF           RST 38,MASK-INT
38C4 FF           RST 38,MASK-INT
38C5 FF           RST 38,MASK-INT
38C6 FF           RST 38,MASK-INT
38C7 FF           RST 38,MASK-INT
38C8 FF           RST 38,MASK-INT
38C9 FF           RST 38,MASK-INT
38CA FF           RST 38,MASK-INT
38CB FF           RST 38,MASK-INT
38CC FF           RST 38,MASK-INT
38CD FF           RST 38,MASK-INT
38CE FF           RST 38,MASK-INT
38CF FF           RST 38,MASK-INT
38D0 FF           RST 38,MASK-INT
38D1 FF           RST 38,MASK-INT
38D2 FF           RST 38,MASK-INT
38D3 FF           RST 38,MASK-INT
38D4 FF           RST 38,MASK-INT
38D5 FF           RST 38,MASK-INT
38D6 FF           RST 38,MASK-INT
38D7 FF           RST 38,MASK-INT
38D8 FF           RST 38,MASK-INT
38D9 FF           RST 38,MASK-INT
38DA FF           RST 38,MASK-INT
38DB FF           RST 38,MASK-INT
38DC FF           RST 38,MASK-INT
38DD FF           RST 38,MASK-INT
38DE FF           RST 38,MASK-INT
38DF FF           RST 38,MASK-INT
38E0 FF           RST 38,MASK-INT
38E1 FF           RST 38,MASK-INT
38E2 FF           RST 38,MASK-INT
38E3 FF           RST 38,MASK-INT
38E4 FF           RST 38,MASK-INT
38E5 FF           RST 38,MASK-INT
38E6 FF           RST 38,MASK-INT
38E7 FF           RST 38,MASK-INT
38E8 FF           RST 38,MASK-INT
38E9 FF           RST 38,MASK-INT
38EA FF           RST 38,MASK-INT
38EB FF           RST 38,MASK-INT
38EC FF           RST 38,MASK-INT
38ED FF           RST 38,MASK-INT
38EE FF           RST 38,MASK-INT
38EF FF           RST 38,MASK-INT
38F0 FF           RST 38,MASK-INT
38F1 FF           RST 38,MASK-INT
38F2 FF           RST 38,MASK-INT
38F3 FF           RST 38,MASK-INT
38F4 FF           RST 38,MASK-INT
38F5 FF           RST 38,MASK-INT
38F6 FF           RST 38,MASK-INT
38F7 FF           RST 38,MASK-INT
38F8 FF           RST 38,MASK-INT
38F9 FF           RST 38,MASK-INT
38FA FF           RST 38,MASK-INT
38FB FF           RST 38,MASK-INT
38FC FF           RST 38,MASK-INT
38FD FF           RST 38,MASK-INT
38FE FF           RST 38,MASK-INT
38FF FF           RST 38,MASK-INT
3900 FF           RST 38,MASK-INT
3901 FF           RST 38,MASK-INT
3902 FF           RST 38,MASK-INT
3903 FF           RST 38,MASK-INT
3904 FF           RST 38,MASK-INT
3905 FF           RST 38,MASK-INT
3906 FF           RST 38,MASK-INT
3907 FF           RST 38,MASK-INT
3908 FF           RST 38,MASK-INT
3909 FF           RST 38,MASK-INT
390A FF           RST 38,MASK-INT
390B FF           RST 38,MASK-INT
390C FF           RST 38,MASK-INT
390D FF           RST 38,MASK-INT
390E FF           RST 38,MASK-INT
390F FF           RST 38,MASK-INT
3910 FF           RST 38,MASK-INT
3911 FF           RST 38,MASK-INT
3912 FF           RST 38,MASK-INT
3913 FF           RST 38,MASK-INT
3914 FF           RST 38,MASK-INT
3915 FF           RST 38,MASK-INT
3916 FF           RST 38,MASK-INT
3917 FF           RST 38,MASK-INT
3918 FF           RST 38,MASK-INT
3919 FF           RST 38,MASK-INT
391A FF           RST 38,MASK-INT
391B FF           RST 38,MASK-INT
391C FF           RST 38,MASK-INT
391D FF           RST 38,MASK-INT
391E FF           RST 38,MASK-INT
391F FF           RST 38,MASK-INT
3920 FF           RST 38,MASK-INT
3921 FF           RST 38,MASK-INT
3922 FF           RST 38,MASK-INT
3923 FF           RST 38,MASK-INT
3924 FF           RST 38,MASK-INT
3925 FF           RST 38,MASK-INT
3926 FF           RST 38,MASK-INT
3927 FF           RST 38,MASK-INT
3928 FF           RST 38,MASK-INT
3929 FF           RST 38,MASK-INT
392A FF           RST 38,MASK-INT
392B FF           RST 38,MASK-INT
392C FF           RST 38,MASK-INT
392D FF           RST 38,MASK-INT
392E FF           RST 38,MASK-INT
392F FF           RST 38,MASK-INT
3930 FF           RST 38,MASK-INT
3931 FF           RST 38,MASK-INT
3932 FF           RST 38,MASK-INT
3933 FF           RST 38,MASK-INT
3934 FF           RST 38,MASK-INT
3935 FF           RST 38,MASK-INT
3936 FF           RST 38,MASK-INT
3937 FF           RST 38,MASK-INT
3938 FF           RST 38,MASK-INT
3939 FF           RST 38,MASK-INT
393A FF           RST 38,MASK-INT
393B FF           RST 38,MASK-INT
393C FF           RST 38,MASK-INT
393D FF           RST 38,MASK-INT
393E FF           RST 38,MASK-INT
393F FF           RST 38,MASK-INT
3940 FF           RST 38,MASK-INT
3941 FF           RST 38,MASK-INT
3942 FF           RST 38,MASK-INT
3943 FF           RST 38,MASK-INT
3944 FF           RST 38,MASK-INT
3945 FF           RST 38,MASK-INT
3946 FF           RST 38,MASK-INT
3947 FF           RST 38,MASK-INT
3948 FF           RST 38,MASK-INT
3949 FF           RST 38,MASK-INT
394A FF           RST 38,MASK-INT
394B FF           RST 38,MASK-INT
394C FF           RST 38,MASK-INT
394D FF           RST 38,MASK-INT
394E FF           RST 38,MASK-INT
394F FF           RST 38,MASK-INT
3950 FF           RST 38,MASK-INT
3951 FF           RST 38,MASK-INT
3952 FF           RST 38,MASK-INT
3953 FF           RST 38,MASK-INT
3954 FF           RST 38,MASK-INT
3955 FF           RST 38,MASK-INT
3956 FF           RST 38,MASK-INT
3957 FF           RST 38,MASK-INT
3958 FF           RST 38,MASK-INT
3959 FF           RST 38,MASK-INT
395A FF           RST 38,MASK-INT
395B FF           RST 38,MASK-INT
395C FF           RST 38,MASK-INT
395D FF           RST 38,MASK-INT
395E FF           RST 38,MASK-INT
395F FF           RST 38,MASK-INT
3960 FF           RST 38,MASK-INT
3961 FF           RST 38,MASK-INT
3962 FF           RST 38,MASK-INT
3963 FF           RST 38,MASK-INT
3964 FF           RST 38,MASK-INT
3965 FF           RST 38,MASK-INT
3966 FF           RST 38,MASK-INT
3967 FF           RST 38,MASK-INT
3968 FF           RST 38,MASK-INT
3969 FF           RST 38,MASK-INT
396A FF           RST 38,MASK-INT
396B FF           RST 38,MASK-INT
396C FF           RST 38,MASK-INT
396D FF           RST 38,MASK-INT
396E FF           RST 38,MASK-INT
396F FF           RST 38,MASK-INT
3970 FF           RST 38,MASK-INT
3971 FF           RST 38,MASK-INT
3972 FF           RST 38,MASK-INT
3973 FF           RST 38,MASK-INT
3974 FF           RST 38,MASK-INT
3975 FF           RST 38,MASK-INT
3976 FF           RST 38,MASK-INT
3977 FF           RST 38,MASK-INT
3978 FF           RST 38,MASK-INT
3979 FF           RST 38,MASK-INT
397A FF           RST 38,MASK-INT
397B FF           RST 38,MASK-INT
397C FF           RST 38,MASK-INT
397D FF           RST 38,MASK-INT
397E FF           RST 38,MASK-INT
397F FF           RST 38,MASK-INT
3980 FF           RST 38,MASK-INT
3981 FF           RST 38,MASK-INT
3982 FF           RST 38,MASK-INT
3983 FF           RST 38,MASK-INT
3984 FF           RST 38,MASK-INT
3985 FF           RST 38,MASK-INT
3986 FF           RST 38,MASK-INT
3987 FF           RST 38,MASK-INT
3988 FF           RST 38,MASK-INT
3989 FF           RST 38,MASK-INT
398A FF           RST 38,MASK-INT
398B FF           RST 38,MASK-INT
398C FF           RST 38,MASK-INT
398D FF           RST 38,MASK-INT
398E FF           RST 38,MASK-INT
398F FF           RST 38,MASK-INT
3990 FF           RST 38,MASK-INT
3991 FF           RST 38,MASK-INT
3992 FF           RST 38,MASK-INT
3993 FF           RST 38,MASK-INT
3994 FF           RST 38,MASK-INT
3995 FF           RST 38,MASK-INT
3996 FF           RST 38,MASK-INT
3997 FF           RST 38,MASK-INT
3998 FF           RST 38,MASK-INT
3999 FF           RST 38,MASK-INT
399A FF           RST 38,MASK-INT
399B FF           RST 38,MASK-INT
399C FF           RST 38,MASK-INT
399D FF           RST 38,MASK-INT
399E FF           RST 38,MASK-INT
399F FF           RST 38,MASK-INT
39A0 FF           RST 38,MASK-INT
39A1 FF           RST 38,MASK-INT
39A2 FF           RST 38,MASK-INT
39A3 FF           RST 38,MASK-INT
39A4 FF           RST 38,MASK-INT
39A5 FF           RST 38,MASK-INT
39A6 FF           RST 38,MASK-INT
39A7 FF           RST 38,MASK-INT
39A8 FF           RST 38,MASK-INT
39A9 FF           RST 38,MASK-INT
39AA FF           RST 38,MASK-INT
39AB FF           RST 38,MASK-INT
39AC FF           RST 38,MASK-INT
39AD FF           RST 38,MASK-INT
39AE FF           RST 38,MASK-INT
39AF FF           RST 38,MASK-INT
39B0 FF           RST 38,MASK-INT
39B1 FF           RST 38,MASK-INT
39B2 FF           RST 38,MASK-INT
39B3 FF           RST 38,MASK-INT
39B4 FF           RST 38,MASK-INT
39B5 FF           RST 38,MASK-INT
39B6 FF           RST 38,MASK-INT
39B7 FF           RST 38,MASK-INT
39B8 FF           RST 38,MASK-INT
39B9 FF           RST 38,MASK-INT
39BA FF           RST 38,MASK-INT
39BB FF           RST 38,MASK-INT
39BC FF           RST 38,MASK-INT
39BD FF           RST 38,MASK-INT
39BE FF           RST 38,MASK-INT
39BF FF           RST 38,MASK-INT
39C0 FF           RST 38,MASK-INT
39C1 FF           RST 38,MASK-INT
39C2 FF           RST 38,MASK-INT
39C3 FF           RST 38,MASK-INT
39C4 FF           RST 38,MASK-INT
39C5 FF           RST 38,MASK-INT
39C6 FF           RST 38,MASK-INT
39C7 FF           RST 38,MASK-INT
39C8 FF           RST 38,MASK-INT
39C9 FF           RST 38,MASK-INT
39CA FF           RST 38,MASK-INT
39CB FF           RST 38,MASK-INT
39CC FF           RST 38,MASK-INT
39CD FF           RST 38,MASK-INT
39CE FF           RST 38,MASK-INT
39CF FF           RST 38,MASK-INT
39D0 FF           RST 38,MASK-INT
39D1 FF           RST 38,MASK-INT
39D2 FF           RST 38,MASK-INT
39D3 FF           RST 38,MASK-INT
39D4 FF           RST 38,MASK-INT
39D5 FF           RST 38,MASK-INT
39D6 FF           RST 38,MASK-INT
39D7 FF           RST 38,MASK-INT
39D8 FF           RST 38,MASK-INT
39D9 FF           RST 38,MASK-INT
39DA FF           RST 38,MASK-INT
39DB FF           RST 38,MASK-INT
39DC FF           RST 38,MASK-INT
39DD FF           RST 38,MASK-INT
39DE FF           RST 38,MASK-INT
39DF FF           RST 38,MASK-INT
39E0 FF           RST 38,MASK-INT
39E1 FF           RST 38,MASK-INT
39E2 FF           RST 38,MASK-INT
39E3 FF           RST 38,MASK-INT
39E4 FF           RST 38,MASK-INT
39E5 FF           RST 38,MASK-INT
39E6 FF           RST 38,MASK-INT
39E7 FF           RST 38,MASK-INT
39E8 FF           RST 38,MASK-INT
39E9 FF           RST 38,MASK-INT
39EA FF           RST 38,MASK-INT
39EB FF           RST 38,MASK-INT
39EC FF           RST 38,MASK-INT
39ED FF           RST 38,MASK-INT
39EE FF           RST 38,MASK-INT
39EF FF           RST 38,MASK-INT
39F0 FF           RST 38,MASK-INT
39F1 FF           RST 38,MASK-INT
39F2 FF           RST 38,MASK-INT
39F3 FF           RST 38,MASK-INT
39F4 FF           RST 38,MASK-INT
39F5 FF           RST 38,MASK-INT
39F6 FF           RST 38,MASK-INT
39F7 FF           RST 38,MASK-INT
39F8 FF           RST 38,MASK-INT
39F9 FF           RST 38,MASK-INT
39FA FF           RST 38,MASK-INT
39FB FF           RST 38,MASK-INT
39FC FF           RST 38,MASK-INT
39FD FF           RST 38,MASK-INT
39FE FF           RST 38,MASK-INT
39FF FF           RST 38,MASK-INT
3A00 FF           RST 38,MASK-INT
3A01 FF           RST 38,MASK-INT
3A02 FF           RST 38,MASK-INT
3A03 FF           RST 38,MASK-INT
3A04 FF           RST 38,MASK-INT
3A05 FF           RST 38,MASK-INT
3A06 FF           RST 38,MASK-INT
3A07 FF           RST 38,MASK-INT
3A08 FF           RST 38,MASK-INT
3A09 FF           RST 38,MASK-INT
3A0A FF           RST 38,MASK-INT
3A0B FF           RST 38,MASK-INT
3A0C FF           RST 38,MASK-INT
3A0D FF           RST 38,MASK-INT
3A0E FF           RST 38,MASK-INT
3A0F FF           RST 38,MASK-INT
3A10 FF           RST 38,MASK-INT
3A11 FF           RST 38,MASK-INT
3A12 FF           RST 38,MASK-INT
3A13 FF           RST 38,MASK-INT
3A14 FF           RST 38,MASK-INT
3A15 FF           RST 38,MASK-INT
3A16 FF           RST 38,MASK-INT
3A17 FF           RST 38,MASK-INT
3A18 FF           RST 38,MASK-INT
3A19 FF           RST 38,MASK-INT
3A1A FF           RST 38,MASK-INT
3A1B FF           RST 38,MASK-INT
3A1C FF           RST 38,MASK-INT
3A1D FF           RST 38,MASK-INT
3A1E FF           RST 38,MASK-INT
3A1F FF           RST 38,MASK-INT
3A20 FF           RST 38,MASK-INT
3A21 FF           RST 38,MASK-INT
3A22 FF           RST 38,MASK-INT
3A23 FF           RST 38,MASK-INT
3A24 FF           RST 38,MASK-INT
3A25 FF           RST 38,MASK-INT
3A26 FF           RST 38,MASK-INT
3A27 FF           RST 38,MASK-INT
3A28 FF           RST 38,MASK-INT
3A29 FF           RST 38,MASK-INT
3A2A FF           RST 38,MASK-INT
3A2B FF           RST 38,MASK-INT
3A2C FF           RST 38,MASK-INT
3A2D FF           RST 38,MASK-INT
3A2E FF           RST 38,MASK-INT
3A2F FF           RST 38,MASK-INT
3A30 FF           RST 38,MASK-INT
3A31 FF           RST 38,MASK-INT
3A32 FF           RST 38,MASK-INT
3A33 FF           RST 38,MASK-INT
3A34 FF           RST 38,MASK-INT
3A35 FF           RST 38,MASK-INT
3A36 FF           RST 38,MASK-INT
3A37 FF           RST 38,MASK-INT
3A38 FF           RST 38,MASK-INT
3A39 FF           RST 38,MASK-INT
3A3A FF           RST 38,MASK-INT
3A3B FF           RST 38,MASK-INT
3A3C FF           RST 38,MASK-INT
3A3D FF           RST 38,MASK-INT
3A3E FF           RST 38,MASK-INT
3A3F FF           RST 38,MASK-INT
3A40 FF           RST 38,MASK-INT
3A41 FF           RST 38,MASK-INT
3A42 FF           RST 38,MASK-INT
3A43 FF           RST 38,MASK-INT
3A44 FF           RST 38,MASK-INT
3A45 FF           RST 38,MASK-INT
3A46 FF           RST 38,MASK-INT
3A47 FF           RST 38,MASK-INT
3A48 FF           RST 38,MASK-INT
3A49 FF           RST 38,MASK-INT
3A4A FF           RST 38,MASK-INT
3A4B FF           RST 38,MASK-INT
3A4C FF           RST 38,MASK-INT
3A4D FF           RST 38,MASK-INT
3A4E FF           RST 38,MASK-INT
3A4F FF           RST 38,MASK-INT
3A50 FF           RST 38,MASK-INT
3A51 FF           RST 38,MASK-INT
3A52 FF           RST 38,MASK-INT
3A53 FF           RST 38,MASK-INT
3A54 FF           RST 38,MASK-INT
3A55 FF           RST 38,MASK-INT
3A56 FF           RST 38,MASK-INT
3A57 FF           RST 38,MASK-INT
3A58 FF           RST 38,MASK-INT
3A59 FF           RST 38,MASK-INT
3A5A FF           RST 38,MASK-INT
3A5B FF           RST 38,MASK-INT
3A5C FF           RST 38,MASK-INT
3A5D FF           RST 38,MASK-INT
3A5E FF           RST 38,MASK-INT
3A5F FF           RST 38,MASK-INT
3A60 FF           RST 38,MASK-INT
3A61 FF           RST 38,MASK-INT
3A62 FF           RST 38,MASK-INT
3A63 FF           RST 38,MASK-INT
3A64 FF           RST 38,MASK-INT
3A65 FF           RST 38,MASK-INT
3A66 FF           RST 38,MASK-INT
3A67 FF           RST 38,MASK-INT
3A68 FF           RST 38,MASK-INT
3A69 FF           RST 38,MASK-INT
3A6A FF           RST 38,MASK-INT
3A6B FF           RST 38,MASK-INT
3A6C FF           RST 38,MASK-INT
3A6D FF           RST 38,MASK-INT
3A6E FF           RST 38,MASK-INT
3A6F FF           RST 38,MASK-INT
3A70 FF           RST 38,MASK-INT
3A71 FF           RST 38,MASK-INT
3A72 FF           RST 38,MASK-INT
3A73 FF           RST 38,MASK-INT
3A74 FF           RST 38,MASK-INT
3A75 FF           RST 38,MASK-INT
3A76 FF           RST 38,MASK-INT
3A77 FF           RST 38,MASK-INT
3A78 FF           RST 38,MASK-INT
3A79 FF           RST 38,MASK-INT
3A7A FF           RST 38,MASK-INT
3A7B FF           RST 38,MASK-INT
3A7C FF           RST 38,MASK-INT
3A7D FF           RST 38,MASK-INT
3A7E FF           RST 38,MASK-INT
3A7F FF           RST 38,MASK-INT
3A80 FF           RST 38,MASK-INT
3A81 FF           RST 38,MASK-INT
3A82 FF           RST 38,MASK-INT
3A83 FF           RST 38,MASK-INT
3A84 FF           RST 38,MASK-INT
3A85 FF           RST 38,MASK-INT
3A86 FF           RST 38,MASK-INT
3A87 FF           RST 38,MASK-INT
3A88 FF           RST 38,MASK-INT
3A89 FF           RST 38,MASK-INT
3A8A FF           RST 38,MASK-INT
3A8B FF           RST 38,MASK-INT
3A8C FF           RST 38,MASK-INT
3A8D FF           RST 38,MASK-INT
3A8E FF           RST 38,MASK-INT
3A8F FF           RST 38,MASK-INT
3A90 FF           RST 38,MASK-INT
3A91 FF           RST 38,MASK-INT
3A92 FF           RST 38,MASK-INT
3A93 FF           RST 38,MASK-INT
3A94 FF           RST 38,MASK-INT
3A95 FF           RST 38,MASK-INT
3A96 FF           RST 38,MASK-INT
3A97 FF           RST 38,MASK-INT
3A98 FF           RST 38,MASK-INT
3A99 FF           RST 38,MASK-INT
3A9A FF           RST 38,MASK-INT
3A9B FF           RST 38,MASK-INT
3A9C FF           RST 38,MASK-INT
3A9D FF           RST 38,MASK-INT
3A9E FF           RST 38,MASK-INT
3A9F FF           RST 38,MASK-INT
3AA0 FF           RST 38,MASK-INT
3AA1 FF           RST 38,MASK-INT
3AA2 FF           RST 38,MASK-INT
3AA3 FF           RST 38,MASK-INT
3AA4 FF           RST 38,MASK-INT
3AA5 FF           RST 38,MASK-INT
3AA6 FF           RST 38,MASK-INT
3AA7 FF           RST 38,MASK-INT
3AA8 FF           RST 38,MASK-INT
3AA9 FF           RST 38,MASK-INT
3AAA FF           RST 38,MASK-INT
3AAB FF           RST 38,MASK-INT
3AAC FF           RST 38,MASK-INT
3AAD FF           RST 38,MASK-INT
3AAE FF           RST 38,MASK-INT
3AAF FF           RST 38,MASK-INT
3AB0 FF           RST 38,MASK-INT
3AB1 FF           RST 38,MASK-INT
3AB2 FF           RST 38,MASK-INT
3AB3 FF           RST 38,MASK-INT
3AB4 FF           RST 38,MASK-INT
3AB5 FF           RST 38,MASK-INT
3AB6 FF           RST 38,MASK-INT
3AB7 FF           RST 38,MASK-INT
3AB8 FF           RST 38,MASK-INT
3AB9 FF           RST 38,MASK-INT
3ABA FF           RST 38,MASK-INT
3ABB FF           RST 38,MASK-INT
3ABC FF           RST 38,MASK-INT
3ABD FF           RST 38,MASK-INT
3ABE FF           RST 38,MASK-INT
3ABF FF           RST 38,MASK-INT
3AC0 FF           RST 38,MASK-INT
3AC1 FF           RST 38,MASK-INT
3AC2 FF           RST 38,MASK-INT
3AC3 FF           RST 38,MASK-INT
3AC4 FF           RST 38,MASK-INT
3AC5 FF           RST 38,MASK-INT
3AC6 FF           RST 38,MASK-INT
3AC7 FF           RST 38,MASK-INT
3AC8 FF           RST 38,MASK-INT
3AC9 FF           RST 38,MASK-INT
3ACA FF           RST 38,MASK-INT
3ACB FF           RST 38,MASK-INT
3ACC FF           RST 38,MASK-INT
3ACD FF           RST 38,MASK-INT
3ACE FF           RST 38,MASK-INT
3ACF FF           RST 38,MASK-INT
3AD0 FF           RST 38,MASK-INT
3AD1 FF           RST 38,MASK-INT
3AD2 FF           RST 38,MASK-INT
3AD3 FF           RST 38,MASK-INT
3AD4 FF           RST 38,MASK-INT
3AD5 FF           RST 38,MASK-INT
3AD6 FF           RST 38,MASK-INT
3AD7 FF           RST 38,MASK-INT
3AD8 FF           RST 38,MASK-INT
3AD9 FF           RST 38,MASK-INT
3ADA FF           RST 38,MASK-INT
3ADB FF           RST 38,MASK-INT
3ADC FF           RST 38,MASK-INT
3ADD FF           RST 38,MASK-INT
3ADE FF           RST 38,MASK-INT
3ADF FF           RST 38,MASK-INT
3AE0 FF           RST 38,MASK-INT
3AE1 FF           RST 38,MASK-INT
3AE2 FF           RST 38,MASK-INT
3AE3 FF           RST 38,MASK-INT
3AE4 FF           RST 38,MASK-INT
3AE5 FF           RST 38,MASK-INT
3AE6 FF           RST 38,MASK-INT
3AE7 FF           RST 38,MASK-INT
3AE8 FF           RST 38,MASK-INT
3AE9 FF           RST 38,MASK-INT
3AEA FF           RST 38,MASK-INT
3AEB FF           RST 38,MASK-INT
3AEC FF           RST 38,MASK-INT
3AED FF           RST 38,MASK-INT
3AEE FF           RST 38,MASK-INT
3AEF FF           RST 38,MASK-INT
3AF0 FF           RST 38,MASK-INT
3AF1 FF           RST 38,MASK-INT
3AF2 FF           RST 38,MASK-INT
3AF3 FF           RST 38,MASK-INT
3AF4 FF           RST 38,MASK-INT
3AF5 FF           RST 38,MASK-INT
3AF6 FF           RST 38,MASK-INT
3AF7 FF           RST 38,MASK-INT
3AF8 FF           RST 38,MASK-INT
3AF9 FF           RST 38,MASK-INT
3AFA FF           RST 38,MASK-INT
3AFB FF           RST 38,MASK-INT
3AFC FF           RST 38,MASK-INT
3AFD FF           RST 38,MASK-INT
3AFE FF           RST 38,MASK-INT
3AFF FF           RST 38,MASK-INT
3B00 FF           RST 38,MASK-INT
3B01 FF           RST 38,MASK-INT
3B02 FF           RST 38,MASK-INT
3B03 FF           RST 38,MASK-INT
3B04 FF           RST 38,MASK-INT
3B05 FF           RST 38,MASK-INT
3B06 FF           RST 38,MASK-INT
3B07 FF           RST 38,MASK-INT
3B08 FF           RST 38,MASK-INT
3B09 FF           RST 38,MASK-INT
3B0A FF           RST 38,MASK-INT
3B0B FF           RST 38,MASK-INT
3B0C FF           RST 38,MASK-INT
3B0D FF           RST 38,MASK-INT
3B0E FF           RST 38,MASK-INT
3B0F FF           RST 38,MASK-INT
3B10 FF           RST 38,MASK-INT
3B11 FF           RST 38,MASK-INT
3B12 FF           RST 38,MASK-INT
3B13 FF           RST 38,MASK-INT
3B14 FF           RST 38,MASK-INT
3B15 FF           RST 38,MASK-INT
3B16 FF           RST 38,MASK-INT
3B17 FF           RST 38,MASK-INT
3B18 FF           RST 38,MASK-INT
3B19 FF           RST 38,MASK-INT
3B1A FF           RST 38,MASK-INT
3B1B FF           RST 38,MASK-INT
3B1C FF           RST 38,MASK-INT
3B1D FF           RST 38,MASK-INT
3B1E FF           RST 38,MASK-INT
3B1F FF           RST 38,MASK-INT
3B20 FF           RST 38,MASK-INT
3B21 FF           RST 38,MASK-INT
3B22 FF           RST 38,MASK-INT
3B23 FF           RST 38,MASK-INT
3B24 FF           RST 38,MASK-INT
3B25 FF           RST 38,MASK-INT
3B26 FF           RST 38,MASK-INT
3B27 FF           RST 38,MASK-INT
3B28 FF           RST 38,MASK-INT
3B29 FF           RST 38,MASK-INT
3B2A FF           RST 38,MASK-INT
3B2B FF           RST 38,MASK-INT
3B2C FF           RST 38,MASK-INT
3B2D FF           RST 38,MASK-INT
3B2E FF           RST 38,MASK-INT
3B2F FF           RST 38,MASK-INT
3B30 FF           RST 38,MASK-INT
3B31 FF           RST 38,MASK-INT
3B32 FF           RST 38,MASK-INT
3B33 FF           RST 38,MASK-INT
3B34 FF           RST 38,MASK-INT
3B35 FF           RST 38,MASK-INT
3B36 FF           RST 38,MASK-INT
3B37 FF           RST 38,MASK-INT
3B38 FF           RST 38,MASK-INT
3B39 FF           RST 38,MASK-INT
3B3A FF           RST 38,MASK-INT
3B3B FF           RST 38,MASK-INT
3B3C FF           RST 38,MASK-INT
3B3D FF           RST 38,MASK-INT
3B3E FF           RST 38,MASK-INT
3B3F FF           RST 38,MASK-INT
3B40 FF           RST 38,MASK-INT
3B41 FF           RST 38,MASK-INT
3B42 FF           RST 38,MASK-INT
3B43 FF           RST 38,MASK-INT
3B44 FF           RST 38,MASK-INT
3B45 FF           RST 38,MASK-INT
3B46 FF           RST 38,MASK-INT
3B47 FF           RST 38,MASK-INT
3B48 FF           RST 38,MASK-INT
3B49 FF           RST 38,MASK-INT
3B4A FF           RST 38,MASK-INT
3B4B FF           RST 38,MASK-INT
3B4C FF           RST 38,MASK-INT
3B4D FF           RST 38,MASK-INT
3B4E FF           RST 38,MASK-INT
3B4F FF           RST 38,MASK-INT
3B50 FF           RST 38,MASK-INT
3B51 FF           RST 38,MASK-INT
3B52 FF           RST 38,MASK-INT
3B53 FF           RST 38,MASK-INT
3B54 FF           RST 38,MASK-INT
3B55 FF           RST 38,MASK-INT
3B56 FF           RST 38,MASK-INT
3B57 FF           RST 38,MASK-INT
3B58 FF           RST 38,MASK-INT
3B59 FF           RST 38,MASK-INT
3B5A FF           RST 38,MASK-INT
3B5B FF           RST 38,MASK-INT
3B5C FF           RST 38,MASK-INT
3B5D FF           RST 38,MASK-INT
3B5E FF           RST 38,MASK-INT
3B5F FF           RST 38,MASK-INT
3B60 FF           RST 38,MASK-INT
3B61 FF           RST 38,MASK-INT
3B62 FF           RST 38,MASK-INT
3B63 FF           RST 38,MASK-INT
3B64 FF           RST 38,MASK-INT
3B65 FF           RST 38,MASK-INT
3B66 FF           RST 38,MASK-INT
3B67 FF           RST 38,MASK-INT
3B68 FF           RST 38,MASK-INT
3B69 FF           RST 38,MASK-INT
3B6A FF           RST 38,MASK-INT
3B6B FF           RST 38,MASK-INT
3B6C FF           RST 38,MASK-INT
3B6D FF           RST 38,MASK-INT
3B6E FF           RST 38,MASK-INT
3B6F FF           RST 38,MASK-INT
3B70 FF           RST 38,MASK-INT
3B71 FF           RST 38,MASK-INT
3B72 FF           RST 38,MASK-INT
3B73 FF           RST 38,MASK-INT
3B74 FF           RST 38,MASK-INT
3B75 FF           RST 38,MASK-INT
3B76 FF           RST 38,MASK-INT
3B77 FF           RST 38,MASK-INT
3B78 FF           RST 38,MASK-INT
3B79 FF           RST 38,MASK-INT
3B7A FF           RST 38,MASK-INT
3B7B FF           RST 38,MASK-INT
3B7C FF           RST 38,MASK-INT
3B7D FF           RST 38,MASK-INT
3B7E FF           RST 38,MASK-INT
3B7F FF           RST 38,MASK-INT
3B80 FF           RST 38,MASK-INT
3B81 FF           RST 38,MASK-INT
3B82 FF           RST 38,MASK-INT
3B83 FF           RST 38,MASK-INT
3B84 FF           RST 38,MASK-INT
3B85 FF           RST 38,MASK-INT
3B86 FF           RST 38,MASK-INT
3B87 FF           RST 38,MASK-INT
3B88 FF           RST 38,MASK-INT
3B89 FF           RST 38,MASK-INT
3B8A FF           RST 38,MASK-INT
3B8B FF           RST 38,MASK-INT
3B8C FF           RST 38,MASK-INT
3B8D FF           RST 38,MASK-INT
3B8E FF           RST 38,MASK-INT
3B8F FF           RST 38,MASK-INT
3B90 FF           RST 38,MASK-INT
3B91 FF           RST 38,MASK-INT
3B92 FF           RST 38,MASK-INT
3B93 FF           RST 38,MASK-INT
3B94 FF           RST 38,MASK-INT
3B95 FF           RST 38,MASK-INT
3B96 FF           RST 38,MASK-INT
3B97 FF           RST 38,MASK-INT
3B98 FF           RST 38,MASK-INT
3B99 FF           RST 38,MASK-INT
3B9A FF           RST 38,MASK-INT
3B9B FF           RST 38,MASK-INT
3B9C FF           RST 38,MASK-INT
3B9D FF           RST 38,MASK-INT
3B9E FF           RST 38,MASK-INT
3B9F FF           RST 38,MASK-INT
3BA0 FF           RST 38,MASK-INT
3BA1 FF           RST 38,MASK-INT
3BA2 FF           RST 38,MASK-INT
3BA3 FF           RST 38,MASK-INT
3BA4 FF           RST 38,MASK-INT
3BA5 FF           RST 38,MASK-INT
3BA6 FF           RST 38,MASK-INT
3BA7 FF           RST 38,MASK-INT
3BA8 FF           RST 38,MASK-INT
3BA9 FF           RST 38,MASK-INT
3BAA FF           RST 38,MASK-INT
3BAB FF           RST 38,MASK-INT
3BAC FF           RST 38,MASK-INT
3BAD FF           RST 38,MASK-INT
3BAE FF           RST 38,MASK-INT
3BAF FF           RST 38,MASK-INT
3BB0 FF           RST 38,MASK-INT
3BB1 FF           RST 38,MASK-INT
3BB2 FF           RST 38,MASK-INT
3BB3 FF           RST 38,MASK-INT
3BB4 FF           RST 38,MASK-INT
3BB5 FF           RST 38,MASK-INT
3BB6 FF           RST 38,MASK-INT
3BB7 FF           RST 38,MASK-INT
3BB8 FF           RST 38,MASK-INT
3BB9 FF           RST 38,MASK-INT
3BBA FF           RST 38,MASK-INT
3BBB FF           RST 38,MASK-INT
3BBC FF           RST 38,MASK-INT
3BBD FF           RST 38,MASK-INT
3BBE FF           RST 38,MASK-INT
3BBF FF           RST 38,MASK-INT
3BC0 FF           RST 38,MASK-INT
3BC1 FF           RST 38,MASK-INT
3BC2 FF           RST 38,MASK-INT
3BC3 FF           RST 38,MASK-INT
3BC4 FF           RST 38,MASK-INT
3BC5 FF           RST 38,MASK-INT
3BC6 FF           RST 38,MASK-INT
3BC7 FF           RST 38,MASK-INT
3BC8 FF           RST 38,MASK-INT
3BC9 FF           RST 38,MASK-INT
3BCA FF           RST 38,MASK-INT
3BCB FF           RST 38,MASK-INT
3BCC FF           RST 38,MASK-INT
3BCD FF           RST 38,MASK-INT
3BCE FF           RST 38,MASK-INT
3BCF FF           RST 38,MASK-INT
3BD0 FF           RST 38,MASK-INT
3BD1 FF           RST 38,MASK-INT
3BD2 FF           RST 38,MASK-INT
3BD3 FF           RST 38,MASK-INT
3BD4 FF           RST 38,MASK-INT
3BD5 FF           RST 38,MASK-INT
3BD6 FF           RST 38,MASK-INT
3BD7 FF           RST 38,MASK-INT
3BD8 FF           RST 38,MASK-INT
3BD9 FF           RST 38,MASK-INT
3BDA FF           RST 38,MASK-INT
3BDB FF           RST 38,MASK-INT
3BDC FF           RST 38,MASK-INT
3BDD FF           RST 38,MASK-INT
3BDE FF           RST 38,MASK-INT
3BDF FF           RST 38,MASK-INT
3BE0 FF           RST 38,MASK-INT
3BE1 FF           RST 38,MASK-INT
3BE2 FF           RST 38,MASK-INT
3BE3 FF           RST 38,MASK-INT
3BE4 FF           RST 38,MASK-INT
3BE5 FF           RST 38,MASK-INT
3BE6 FF           RST 38,MASK-INT
3BE7 FF           RST 38,MASK-INT
3BE8 FF           RST 38,MASK-INT
3BE9 FF           RST 38,MASK-INT
3BEA FF           RST 38,MASK-INT
3BEB FF           RST 38,MASK-INT
3BEC FF           RST 38,MASK-INT
3BED FF           RST 38,MASK-INT
3BEE FF           RST 38,MASK-INT
3BEF FF           RST 38,MASK-INT
3BF0 FF           RST 38,MASK-INT
3BF1 FF           RST 38,MASK-INT
3BF2 FF           RST 38,MASK-INT
3BF3 FF           RST 38,MASK-INT
3BF4 FF           RST 38,MASK-INT
3BF5 FF           RST 38,MASK-INT
3BF6 FF           RST 38,MASK-INT
3BF7 FF           RST 38,MASK-INT
3BF8 FF           RST 38,MASK-INT
3BF9 FF           RST 38,MASK-INT
3BFA FF           RST 38,MASK-INT
3BFB FF           RST 38,MASK-INT
3BFC FF           RST 38,MASK-INT
3BFD FF           RST 38,MASK-INT
3BFE FF           RST 38,MASK-INT
3BFF FF           RST 38,MASK-INT
3C00 FF           RST 38,MASK-INT
3C01 FF           RST 38,MASK-INT
3C02 FF           RST 38,MASK-INT
3C03 FF           RST 38,MASK-INT
3C04 FF           RST 38,MASK-INT
3C05 FF           RST 38,MASK-INT
3C06 FF           RST 38,MASK-INT
3C07 FF           RST 38,MASK-INT
3C08 FF           RST 38,MASK-INT
3C09 FF           RST 38,MASK-INT
3C0A FF           RST 38,MASK-INT
3C0B FF           RST 38,MASK-INT
3C0C FF           RST 38,MASK-INT
3C0D FF           RST 38,MASK-INT
3C0E FF           RST 38,MASK-INT
3C0F FF           RST 38,MASK-INT
3C10 FF           RST 38,MASK-INT
3C11 FF           RST 38,MASK-INT
3C12 FF           RST 38,MASK-INT
3C13 FF           RST 38,MASK-INT
3C14 FF           RST 38,MASK-INT
3C15 FF           RST 38,MASK-INT
3C16 FF           RST 38,MASK-INT
3C17 FF           RST 38,MASK-INT
3C18 FF           RST 38,MASK-INT
3C19 FF           RST 38,MASK-INT
3C1A FF           RST 38,MASK-INT
3C1B FF           RST 38,MASK-INT
3C1C FF           RST 38,MASK-INT
3C1D FF           RST 38,MASK-INT
3C1E FF           RST 38,MASK-INT
3C1F FF           RST 38,MASK-INT
3C20 FF           RST 38,MASK-INT
3C21 FF           RST 38,MASK-INT
3C22 FF           RST 38,MASK-INT
3C23 FF           RST 38,MASK-INT
3C24 FF           RST 38,MASK-INT
3C25 FF           RST 38,MASK-INT
3C26 FF           RST 38,MASK-INT
3C27 FF           RST 38,MASK-INT
3C28 FF           RST 38,MASK-INT
3C29 FF           RST 38,MASK-INT
3C2A FF           RST 38,MASK-INT
3C2B FF           RST 38,MASK-INT
3C2C FF           RST 38,MASK-INT
3C2D FF           RST 38,MASK-INT
3C2E FF           RST 38,MASK-INT
3C2F FF           RST 38,MASK-INT
3C30 FF           RST 38,MASK-INT
3C31 FF           RST 38,MASK-INT
3C32 FF           RST 38,MASK-INT
3C33 FF           RST 38,MASK-INT
3C34 FF           RST 38,MASK-INT
3C35 FF           RST 38,MASK-INT
3C36 FF           RST 38,MASK-INT
3C37 FF           RST 38,MASK-INT
3C38 FF           RST 38,MASK-INT
3C39 FF           RST 38,MASK-INT
3C3A FF           RST 38,MASK-INT
3C3B FF           RST 38,MASK-INT
3C3C FF           RST 38,MASK-INT
3C3D FF           RST 38,MASK-INT
3C3E FF           RST 38,MASK-INT
3C3F FF           RST 38,MASK-INT
3C40 FF           RST 38,MASK-INT
3C41 FF           RST 38,MASK-INT
3C42 FF           RST 38,MASK-INT
3C43 FF           RST 38,MASK-INT
3C44 FF           RST 38,MASK-INT
3C45 FF           RST 38,MASK-INT
3C46 FF           RST 38,MASK-INT
3C47 FF           RST 38,MASK-INT
3C48 FF           RST 38,MASK-INT
3C49 FF           RST 38,MASK-INT
3C4A FF           RST 38,MASK-INT
3C4B FF           RST 38,MASK-INT
3C4C FF           RST 38,MASK-INT
3C4D FF           RST 38,MASK-INT
3C4E FF           RST 38,MASK-INT
3C4F FF           RST 38,MASK-INT
3C50 FF           RST 38,MASK-INT
3C51 FF           RST 38,MASK-INT
3C52 FF           RST 38,MASK-INT
3C53 FF           RST 38,MASK-INT
3C54 FF           RST 38,MASK-INT
3C55 FF           RST 38,MASK-INT
3C56 FF           RST 38,MASK-INT
3C57 FF           RST 38,MASK-INT
3C58 FF           RST 38,MASK-INT
3C59 FF           RST 38,MASK-INT
3C5A FF           RST 38,MASK-INT
3C5B FF           RST 38,MASK-INT
3C5C FF           RST 38,MASK-INT
3C5D FF           RST 38,MASK-INT
3C5E FF           RST 38,MASK-INT
3C5F FF           RST 38,MASK-INT
3C60 FF           RST 38,MASK-INT
3C61 FF           RST 38,MASK-INT
3C62 FF           RST 38,MASK-INT
3C63 FF           RST 38,MASK-INT
3C64 FF           RST 38,MASK-INT
3C65 FF           RST 38,MASK-INT
3C66 FF           RST 38,MASK-INT
3C67 FF           RST 38,MASK-INT
3C68 FF           RST 38,MASK-INT
3C69 FF           RST 38,MASK-INT
3C6A FF           RST 38,MASK-INT
3C6B FF           RST 38,MASK-INT
3C6C FF           RST 38,MASK-INT
3C6D FF           RST 38,MASK-INT
3C6E FF           RST 38,MASK-INT
3C6F FF           RST 38,MASK-INT
3C70 FF           RST 38,MASK-INT
3C71 FF           RST 38,MASK-INT
3C72 FF           RST 38,MASK-INT
3C73 FF           RST 38,MASK-INT
3C74 FF           RST 38,MASK-INT
3C75 FF           RST 38,MASK-INT
3C76 FF           RST 38,MASK-INT
3C77 FF           RST 38,MASK-INT
3C78 FF           RST 38,MASK-INT
3C79 FF           RST 38,MASK-INT
3C7A FF           RST 38,MASK-INT
3C7B FF           RST 38,MASK-INT
3C7C FF           RST 38,MASK-INT
3C7D FF           RST 38,MASK-INT
3C7E FF           RST 38,MASK-INT
3C7F FF           RST 38,MASK-INT
3C80 FF           RST 38,MASK-INT
3C81 FF           RST 38,MASK-INT
3C82 FF           RST 38,MASK-INT
3C83 FF           RST 38,MASK-INT
3C84 FF           RST 38,MASK-INT
3C85 FF           RST 38,MASK-INT
3C86 FF           RST 38,MASK-INT
3C87 FF           RST 38,MASK-INT
3C88 FF           RST 38,MASK-INT
3C89 FF           RST 38,MASK-INT
3C8A FF           RST 38,MASK-INT
3C8B FF           RST 38,MASK-INT
3C8C FF           RST 38,MASK-INT
3C8D FF           RST 38,MASK-INT
3C8E FF           RST 38,MASK-INT
3C8F FF           RST 38,MASK-INT
3C90 FF           RST 38,MASK-INT
3C91 FF           RST 38,MASK-INT
3C92 FF           RST 38,MASK-INT
3C93 FF           RST 38,MASK-INT
3C94 FF           RST 38,MASK-INT
3C95 FF           RST 38,MASK-INT
3C96 FF           RST 38,MASK-INT
3C97 FF           RST 38,MASK-INT
3C98 FF           RST 38,MASK-INT
3C99 FF           RST 38,MASK-INT
3C9A FF           RST 38,MASK-INT
3C9B FF           RST 38,MASK-INT
3C9C FF           RST 38,MASK-INT
3C9D FF           RST 38,MASK-INT
3C9E FF           RST 38,MASK-INT
3C9F FF           RST 38,MASK-INT
3CA0 FF           RST 38,MASK-INT
3CA1 FF           RST 38,MASK-INT
3CA2 FF           RST 38,MASK-INT
3CA3 FF           RST 38,MASK-INT
3CA4 FF           RST 38,MASK-INT
3CA5 FF           RST 38,MASK-INT
3CA6 FF           RST 38,MASK-INT
3CA7 FF           RST 38,MASK-INT
3CA8 FF           RST 38,MASK-INT
3CA9 FF           RST 38,MASK-INT
3CAA FF           RST 38,MASK-INT
3CAB FF           RST 38,MASK-INT
3CAC FF           RST 38,MASK-INT
3CAD FF           RST 38,MASK-INT
3CAE FF           RST 38,MASK-INT
3CAF FF           RST 38,MASK-INT
3CB0 FF           RST 38,MASK-INT
3CB1 FF           RST 38,MASK-INT
3CB2 FF           RST 38,MASK-INT
3CB3 FF           RST 38,MASK-INT
3CB4 FF           RST 38,MASK-INT
3CB5 FF           RST 38,MASK-INT
3CB6 FF           RST 38,MASK-INT
3CB7 FF           RST 38,MASK-INT
3CB8 FF           RST 38,MASK-INT
3CB9 FF           RST 38,MASK-INT
3CBA FF           RST 38,MASK-INT
3CBB FF           RST 38,MASK-INT
3CBC FF           RST 38,MASK-INT
3CBD FF           RST 38,MASK-INT
3CBE FF           RST 38,MASK-INT
3CBF FF           RST 38,MASK-INT
3CC0 FF           RST 38,MASK-INT
3CC1 FF           RST 38,MASK-INT
3CC2 FF           RST 38,MASK-INT
3CC3 FF           RST 38,MASK-INT
3CC4 FF           RST 38,MASK-INT
3CC5 FF           RST 38,MASK-INT
3CC6 FF           RST 38,MASK-INT
3CC7 FF           RST 38,MASK-INT
3CC8 FF           RST 38,MASK-INT
3CC9 FF           RST 38,MASK-INT
3CCA FF           RST 38,MASK-INT
3CCB FF           RST 38,MASK-INT
3CCC FF           RST 38,MASK-INT
3CCD FF           RST 38,MASK-INT
3CCE FF           RST 38,MASK-INT
3CCF FF           RST 38,MASK-INT
3CD0 FF           RST 38,MASK-INT
3CD1 FF           RST 38,MASK-INT
3CD2 FF           RST 38,MASK-INT
3CD3 FF           RST 38,MASK-INT
3CD4 FF           RST 38,MASK-INT
3CD5 FF           RST 38,MASK-INT
3CD6 FF           RST 38,MASK-INT
3CD7 FF           RST 38,MASK-INT
3CD8 FF           RST 38,MASK-INT
3CD9 FF           RST 38,MASK-INT
3CDA FF           RST 38,MASK-INT
3CDB FF           RST 38,MASK-INT
3CDC FF           RST 38,MASK-INT
3CDD FF           RST 38,MASK-INT
3CDE FF           RST 38,MASK-INT
3CDF FF           RST 38,MASK-INT
3CE0 FF           RST 38,MASK-INT
3CE1 FF           RST 38,MASK-INT
3CE2 FF           RST 38,MASK-INT
3CE3 FF           RST 38,MASK-INT
3CE4 FF           RST 38,MASK-INT
3CE5 FF           RST 38,MASK-INT
3CE6 FF           RST 38,MASK-INT
3CE7 FF           RST 38,MASK-INT
3CE8 FF           RST 38,MASK-INT
3CE9 FF           RST 38,MASK-INT
3CEA FF           RST 38,MASK-INT
3CEB FF           RST 38,MASK-INT
3CEC FF           RST 38,MASK-INT
3CED FF           RST 38,MASK-INT
3CEE FF           RST 38,MASK-INT
3CEF FF           RST 38,MASK-INT
3CF0 FF           RST 38,MASK-INT
3CF1 FF           RST 38,MASK-INT
3CF2 FF           RST 38,MASK-INT
3CF3 FF           RST 38,MASK-INT
3CF4 FF           RST 38,MASK-INT
3CF5 FF           RST 38,MASK-INT
3CF6 FF           RST 38,MASK-INT
3CF7 FF           RST 38,MASK-INT
3CF8 FF           RST 38,MASK-INT
3CF9 FF           RST 38,MASK-INT
3CFA FF           RST 38,MASK-INT
3CFB FF           RST 38,MASK-INT
3CFC FF           RST 38,MASK-INT
3CFD FF           RST 38,MASK-INT
3CFE FF           RST 38,MASK-INT
3CFF FF           RST 38,MASK-INT
3D00 00           NOP
3D01 00           NOP
3D02 00           NOP
3D03 00           NOP
3D04 00           NOP
3D05 00           NOP
3D06 00           NOP
3D07 00           NOP
3D08 00           NOP
3D09 1010         DJNZ 3D1B
3D0B 1010         DJNZ 3D1D
3D0D 00           NOP
3D0E 1000         DJNZ 3D10
3D10 00           NOP
3D11 24           INC H
3D12 24           INC H
3D13 00           NOP
3D14 00           NOP
3D15 00           NOP
3D16 00           NOP
3D17 00           NOP
3D18 00           NOP
3D19 24           INC H
3D1A 7E           LD A,(HL)
3D1B 24           INC H
3D1C 24           INC H
3D1D 7E           LD A,(HL)
3D1E 24           INC H
3D1F 00           NOP
3D20 00           NOP
3D21 08           EX AF,AF'
3D22 3E28         LD A,28
3D24 3E0A         LD A,0A
3D26 3E08         LD A,08
3D28 00           NOP
3D29 62           LD H,D
3D2A 64           LD H,H
3D2B 08           EX AF,AF'
3D2C 1026         DJNZ 3D54
3D2E 46           LD B,(HL)
3D2F 00           NOP
3D30 00           NOP
3D31 1028         DJNZ 3D5B
3D33 102A         DJNZ 3D5F
3D35 44           LD B,H
3D36 3A0000       LD A,(0000)  {START}
3D39 08           EX AF,AF'
3D3A 1000         DJNZ 3D3C
3D3C 00           NOP
3D3D 00           NOP
3D3E 00           NOP
3D3F 00           NOP
3D40 00           NOP
3D41 04           INC B
3D42 08           EX AF,AF'
3D43 08           EX AF,AF'
3D44 08           EX AF,AF'
3D45 08           EX AF,AF'
3D46 04           INC B
3D47 00           NOP
3D48 00           NOP
3D49 2010         JR NZ,3D5B
3D4B 1010         DJNZ 3D5D
3D4D 1020         DJNZ 3D6F
3D4F 00           NOP
3D50 00           NOP
3D51 00           NOP
3D52 14           INC D
3D53 08           EX AF,AF'
3D54 3E08         LD A,08
3D56 14           INC D
3D57 00           NOP
3D58 00           NOP
3D59 00           NOP
3D5A 08           EX AF,AF'
3D5B 08           EX AF,AF'
3D5C 3E08         LD A,08
3D5E 08           EX AF,AF'
3D5F 00           NOP
3D60 00           NOP
3D61 00           NOP
3D62 00           NOP
3D63 00           NOP
3D64 00           NOP
3D65 08           EX AF,AF'
3D66 08           EX AF,AF'
3D67 1000         DJNZ 3D69
3D69 00           NOP
3D6A 00           NOP
3D6B 00           NOP
3D6C 3E00         LD A,00
3D6E 00           NOP
3D6F 00           NOP
3D70 00           NOP
3D71 00           NOP
3D72 00           NOP
3D73 00           NOP
3D74 00           NOP
3D75 1818         JR 3D8F
3D77 00           NOP
3D78 00           NOP
3D79 00           NOP
3D7A 02           LD (BC),A
3D7B 04           INC B
3D7C 08           EX AF,AF'
3D7D 1020         DJNZ 3D9F
3D7F 00           NOP
3D80 00           NOP
3D81 3C           INC A
3D82 46           LD B,(HL)
3D83 4A           LD C,D
3D84 52           LD D,D
3D85 62           LD H,D
3D86 3C           INC A
3D87 00           NOP
3D88 00           NOP
3D89 1828         JR 3DB3
3D8B 08           EX AF,AF'
3D8C 08           EX AF,AF'
3D8D 08           EX AF,AF'
3D8E 3E00         LD A,00
3D90 00           NOP
3D91 3C           INC A
3D92 42           LD B,D
3D93 02           LD (BC),A
3D94 3C           INC A
3D95 40           LD B,B
3D96 7E           LD A,(HL)
3D97 00           NOP
3D98 00           NOP
3D99 3C           INC A
3D9A 42           LD B,D
3D9B 0C           INC C
3D9C 02           LD (BC),A
3D9D 42           LD B,D
3D9E 3C           INC A
3D9F 00           NOP
3DA0 00           NOP
3DA1 08           EX AF,AF'
3DA2 1828         JR 3DCC
3DA4 48           LD C,B
3DA5 7E           LD A,(HL)
3DA6 08           EX AF,AF'
3DA7 00           NOP
3DA8 00           NOP
3DA9 7E           LD A,(HL)
3DAA 40           LD B,B
3DAB 7C           LD A,H
3DAC 02           LD (BC),A
3DAD 42           LD B,D
3DAE 3C           INC A
3DAF 00           NOP
3DB0 00           NOP
3DB1 3C           INC A
3DB2 40           LD B,B
3DB3 7C           LD A,H
3DB4 42           LD B,D
3DB5 42           LD B,D
3DB6 3C           INC A
3DB7 00           NOP
3DB8 00           NOP
3DB9 7E           LD A,(HL)
3DBA 02           LD (BC),A
3DBB 04           INC B
3DBC 08           EX AF,AF'
3DBD 1010         DJNZ 3DCF
3DBF 00           NOP
3DC0 00           NOP
3DC1 3C           INC A
3DC2 42           LD B,D
3DC3 3C           INC A
3DC4 42           LD B,D
3DC5 42           LD B,D
3DC6 3C           INC A
3DC7 00           NOP
3DC8 00           NOP
3DC9 3C           INC A
3DCA 42           LD B,D
3DCB 42           LD B,D
3DCC 3E02         LD A,02
3DCE 3C           INC A
3DCF 00           NOP
3DD0 00           NOP
3DD1 00           NOP
3DD2 00           NOP
3DD3 1000         DJNZ 3DD5
3DD5 00           NOP
3DD6 1000         DJNZ 3DD8
3DD8 00           NOP
3DD9 00           NOP
3DDA 1000         DJNZ 3DDC
3DDC 00           NOP
3DDD 1010         DJNZ 3DEF
3DDF 2000         JR NZ,3DE1
3DE1 00           NOP
3DE2 04           INC B
3DE3 08           EX AF,AF'
3DE4 1008         DJNZ 3DEE
3DE6 04           INC B
3DE7 00           NOP
3DE8 00           NOP
3DE9 00           NOP
3DEA 00           NOP
3DEB 3E00         LD A,00
3DED 3E00         LD A,00
3DEF 00           NOP
3DF0 00           NOP
3DF1 00           NOP
3DF2 1008         DJNZ 3DFC
3DF4 04           INC B
3DF5 08           EX AF,AF'
3DF6 1000         DJNZ 3DF8
3DF8 00           NOP
3DF9 3C           INC A
3DFA 42           LD B,D
3DFB 04           INC B
3DFC 08           EX AF,AF'
3DFD 00           NOP
3DFE 08           EX AF,AF'
3DFF 00           NOP
3E00 00           NOP
3E01 3C           INC A
3E02 4A           LD C,D
3E03 56           LD D,(HL)
3E04 5E           LD E,(HL)
3E05 40           LD B,B
3E06 3C           INC A
3E07 00           NOP
3E08 00           NOP
3E09 3C           INC A
3E0A 42           LD B,D
3E0B 42           LD B,D
3E0C 7E           LD A,(HL)
3E0D 42           LD B,D
3E0E 42           LD B,D
3E0F 00           NOP
3E10 00           NOP
3E11 7C           LD A,H
3E12 42           LD B,D
3E13 7C           LD A,H
3E14 42           LD B,D
3E15 42           LD B,D
3E16 7C           LD A,H
3E17 00           NOP
3E18 00           NOP
3E19 3C           INC A
3E1A 42           LD B,D
3E1B 40           LD B,B
3E1C 40           LD B,B
3E1D 42           LD B,D
3E1E 3C           INC A
3E1F 00           NOP
3E20 00           NOP
3E21 78           LD A,B
3E22 44           LD B,H
3E23 42           LD B,D
3E24 42           LD B,D
3E25 44           LD B,H
3E26 78           LD A,B
3E27 00           NOP
3E28 00           NOP
3E29 7E           LD A,(HL)
3E2A 40           LD B,B
3E2B 7C           LD A,H
3E2C 40           LD B,B
3E2D 40           LD B,B
3E2E 7E           LD A,(HL)
3E2F 00           NOP
3E30 00           NOP
3E31 7E           LD A,(HL)
3E32 40           LD B,B
3E33 7C           LD A,H
3E34 40           LD B,B
3E35 40           LD B,B
3E36 40           LD B,B
3E37 00           NOP
3E38 00           NOP
3E39 3C           INC A
3E3A 42           LD B,D
3E3B 40           LD B,B
3E3C 4E           LD C,(HL)
3E3D 42           LD B,D
3E3E 3C           INC A
3E3F 00           NOP
3E40 00           NOP
3E41 42           LD B,D
3E42 42           LD B,D
3E43 7E           LD A,(HL)
3E44 42           LD B,D
3E45 42           LD B,D
3E46 42           LD B,D
3E47 00           NOP
3E48 00           NOP
3E49 3E08         LD A,08
3E4B 08           EX AF,AF'
3E4C 08           EX AF,AF'
3E4D 08           EX AF,AF'
3E4E 3E00         LD A,00
3E50 00           NOP
3E51 02           LD (BC),A
3E52 02           LD (BC),A
3E53 02           LD (BC),A
3E54 42           LD B,D
3E55 42           LD B,D
3E56 3C           INC A
3E57 00           NOP
3E58 00           NOP
3E59 44           LD B,H
3E5A 48           LD C,B
3E5B 70           LD (HL),B
3E5C 48           LD C,B
3E5D 44           LD B,H
3E5E 42           LD B,D
3E5F 00           NOP
3E60 00           NOP
3E61 40           LD B,B
3E62 40           LD B,B
3E63 40           LD B,B
3E64 40           LD B,B
3E65 40           LD B,B
3E66 7E           LD A,(HL)
3E67 00           NOP
3E68 00           NOP
3E69 42           LD B,D
3E6A 66           LD H,(HL)
3E6B 5A           LD E,D
3E6C 42           LD B,D
3E6D 42           LD B,D
3E6E 42           LD B,D
3E6F 00           NOP
3E70 00           NOP
3E71 42           LD B,D
3E72 62           LD H,D
3E73 52           LD D,D
3E74 4A           LD C,D
3E75 46           LD B,(HL)
3E76 42           LD B,D
3E77 00           NOP
3E78 00           NOP
3E79 3C           INC A
3E7A 42           LD B,D
3E7B 42           LD B,D
3E7C 42           LD B,D
3E7D 42           LD B,D
3E7E 3C           INC A
3E7F 00           NOP
3E80 00           NOP
3E81 7C           LD A,H
3E82 42           LD B,D
3E83 42           LD B,D
3E84 7C           LD A,H
3E85 40           LD B,B
3E86 40           LD B,B
3E87 00           NOP
3E88 00           NOP
3E89 3C           INC A
3E8A 42           LD B,D
3E8B 42           LD B,D
3E8C 52           LD D,D
3E8D 4A           LD C,D
3E8E 3C           INC A
3E8F 00           NOP
3E90 00           NOP
3E91 7C           LD A,H
3E92 42           LD B,D
3E93 42           LD B,D
3E94 7C           LD A,H
3E95 44           LD B,H
3E96 42           LD B,D
3E97 00           NOP
3E98 00           NOP
3E99 3C           INC A
3E9A 40           LD B,B
3E9B 3C           INC A
3E9C 02           LD (BC),A
3E9D 42           LD B,D
3E9E 3C           INC A
3E9F 00           NOP
3EA0 00           NOP
3EA1 FE10         CP 10
3EA3 1010         DJNZ 3EB5
3EA5 1010         DJNZ 3EB7
3EA7 00           NOP
3EA8 00           NOP
3EA9 42           LD B,D
3EAA 42           LD B,D
3EAB 42           LD B,D
3EAC 42           LD B,D
3EAD 42           LD B,D
3EAE 3C           INC A
3EAF 00           NOP
3EB0 00           NOP
3EB1 42           LD B,D
3EB2 42           LD B,D
3EB3 42           LD B,D
3EB4 42           LD B,D
3EB5 24           INC H
3EB6 1800         JR 3EB8
3EB8 00           NOP
3EB9 42           LD B,D
3EBA 42           LD B,D
3EBB 42           LD B,D
3EBC 42           LD B,D
3EBD 5A           LD E,D
3EBE 24           INC H
3EBF 00           NOP
3EC0 00           NOP
3EC1 42           LD B,D
3EC2 24           INC H
3EC3 1818         JR 3EDD
3EC5 24           INC H
3EC6 42           LD B,D
3EC7 00           NOP
3EC8 00           NOP
3EC9 82           ADD A,D
3ECA 44           LD B,H
3ECB 2810         JR Z,3EDD
3ECD 1010         DJNZ 3EDF
3ECF 00           NOP
3ED0 00           NOP
3ED1 7E           LD A,(HL)
3ED2 04           INC B
3ED3 08           EX AF,AF'
3ED4 1020         DJNZ 3EF6
3ED6 7E           LD A,(HL)
3ED7 00           NOP
3ED8 00           NOP
3ED9 0E08         LD C,08
3EDB 08           EX AF,AF'
3EDC 08           EX AF,AF'
3EDD 08           EX AF,AF'
3EDE 0E00         LD C,00
3EE0 00           NOP
3EE1 00           NOP
3EE2 40           LD B,B
3EE3 2010         JR NZ,3EF5
3EE5 08           EX AF,AF'
3EE6 04           INC B
3EE7 00           NOP
3EE8 00           NOP
3EE9 70           LD (HL),B
3EEA 1010         DJNZ 3EFC
3EEC 1010         DJNZ 3EFE
3EEE 70           LD (HL),B
3EEF 00           NOP
3EF0 00           NOP
3EF1 1038         DJNZ 3F2B
3EF3 54           LD D,H
3EF4 1010         DJNZ 3F06
3EF6 1000         DJNZ 3EF8
3EF8 00           NOP
3EF9 00           NOP
3EFA 00           NOP
3EFB 00           NOP
3EFC 00           NOP
3EFD 00           NOP
3EFE 00           NOP
3EFF FF           RST 38,MASK-INT
3F00 00           NOP
3F01 1C           INC E
3F02 227820       LD (2078),HL
3F05 207E         JR NZ,3F85
3F07 00           NOP
3F08 00           NOP
3F09 00           NOP
3F0A 3804         JR C,3F10
3F0C 3C           INC A
3F0D 44           LD B,H
3F0E 3C           INC A
3F0F 00           NOP
3F10 00           NOP
3F11 2020         JR NZ,3F33
3F13 3C           INC A
3F14 22223C       LD (3C22),HL
3F17 00           NOP
3F18 00           NOP
3F19 00           NOP
3F1A 1C           INC E
3F1B 2020         JR NZ,3F3D
3F1D 201C         JR NZ,3F3B
3F1F 00           NOP
3F20 00           NOP
3F21 04           INC B
3F22 04           INC B
3F23 3C           INC A
3F24 44           LD B,H
3F25 44           LD B,H
3F26 3C           INC A
3F27 00           NOP
3F28 00           NOP
3F29 00           NOP
3F2A 3844         JR C,3F70
3F2C 78           LD A,B
3F2D 40           LD B,B
3F2E 3C           INC A
3F2F 00           NOP
3F30 00           NOP
3F31 0C           INC C
3F32 1018         DJNZ 3F4C
3F34 1010         DJNZ 3F46
3F36 1000         DJNZ 3F38
3F38 00           NOP
3F39 00           NOP
3F3A 3C           INC A
3F3B 44           LD B,H
3F3C 44           LD B,H
3F3D 3C           INC A
3F3E 04           INC B
3F3F 3800         JR C,3F41
3F41 40           LD B,B
3F42 40           LD B,B
3F43 78           LD A,B
3F44 44           LD B,H
3F45 44           LD B,H
3F46 44           LD B,H
3F47 00           NOP
3F48 00           NOP
3F49 1000         DJNZ 3F4B
3F4B 3010         JR NC,3F5D
3F4D 1038         DJNZ 3F87
3F4F 00           NOP
3F50 00           NOP
3F51 04           INC B
3F52 00           NOP
3F53 04           INC B
3F54 04           INC B
3F55 04           INC B
3F56 24           INC H
3F57 1800         JR 3F59
3F59 2028         JR NZ,3F83
3F5B 3030         JR NC,3F8D
3F5D 2824         JR Z,3F83
3F5F 00           NOP
3F60 00           NOP
3F61 1010         DJNZ 3F73
3F63 1010         DJNZ 3F75
3F65 100C         DJNZ 3F73
3F67 00           NOP
3F68 00           NOP
3F69 00           NOP
3F6A 68           LD L,B
3F6B 54           LD D,H
3F6C 54           LD D,H
3F6D 54           LD D,H
3F6E 54           LD D,H
3F6F 00           NOP
3F70 00           NOP
3F71 00           NOP
3F72 78           LD A,B
3F73 44           LD B,H
3F74 44           LD B,H
3F75 44           LD B,H
3F76 44           LD B,H
3F77 00           NOP
3F78 00           NOP
3F79 00           NOP
3F7A 3844         JR C,3FC0
3F7C 44           LD B,H
3F7D 44           LD B,H
3F7E 3800         JR C,3F80
3F80 00           NOP
3F81 00           NOP
3F82 78           LD A,B
3F83 44           LD B,H
3F84 44           LD B,H
3F85 78           LD A,B
3F86 40           LD B,B
3F87 40           LD B,B
3F88 00           NOP
3F89 00           NOP
3F8A 3C           INC A
3F8B 44           LD B,H
3F8C 44           LD B,H
3F8D 3C           INC A
3F8E 04           INC B
3F8F 0600         LD B,00
3F91 00           NOP
3F92 1C           INC E
3F93 2020         JR NZ,3FB5
3F95 2020         JR NZ,3FB7
3F97 00           NOP
3F98 00           NOP
3F99 00           NOP
3F9A 3840         JR C,3FDC
3F9C 3804         JR C,3FA2
3F9E 78           LD A,B
3F9F 00           NOP
3FA0 00           NOP
3FA1 1038         DJNZ 3FDB
3FA3 1010         DJNZ 3FB5
3FA5 100C         DJNZ 3FB3
3FA7 00           NOP
3FA8 00           NOP
3FA9 00           NOP
3FAA 44           LD B,H
3FAB 44           LD B,H
3FAC 44           LD B,H
3FAD 44           LD B,H
3FAE 3800         JR C,3FB0
3FB0 00           NOP
3FB1 00           NOP
3FB2 44           LD B,H
3FB3 44           LD B,H
3FB4 2828         JR Z,3FDE
3FB6 1000         DJNZ 3FB8
3FB8 00           NOP
3FB9 00           NOP
3FBA 44           LD B,H
3FBB 54           LD D,H
3FBC 54           LD D,H
3FBD 54           LD D,H
3FBE 2800         JR Z,3FC0
3FC0 00           NOP
3FC1 00           NOP
3FC2 44           LD B,H
3FC3 2810         JR Z,3FD5
3FC5 2844         JR Z,400B
3FC7 00           NOP
3FC8 00           NOP
3FC9 00           NOP
3FCA 44           LD B,H
3FCB 44           LD B,H
3FCC 44           LD B,H
3FCD 3C           INC A
3FCE 04           INC B
3FCF 3800         JR C,3FD1
3FD1 00           NOP
3FD2 7C           LD A,H
3FD3 08           EX AF,AF'
3FD4 1020         DJNZ 3FF6
3FD6 7C           LD A,H
3FD7 00           NOP
3FD8 00           NOP
3FD9 0E08         LD C,08
3FDB 3008         JR NC,3FE5
3FDD 08           EX AF,AF'
3FDE 0E00         LD C,00
3FE0 00           NOP
3FE1 08           EX AF,AF'
3FE2 08           EX AF,AF'
3FE3 08           EX AF,AF'
3FE4 08           EX AF,AF'
3FE5 08           EX AF,AF'
3FE6 08           EX AF,AF'
3FE7 00           NOP
3FE8 00           NOP
3FE9 70           LD (HL),B
3FEA 100C         DJNZ 3FF8
3FEC 1010         DJNZ 3FFE
3FEE 70           LD (HL),B
3FEF 00           NOP
3FF0 00           NOP
3FF1 14           INC D
3FF2 2800         JR Z,3FF4
3FF4 00           NOP
3FF5 00           NOP
3FF6 00           NOP
3FF7 00           NOP
3FF8 3C           INC A
3FF9 42           LD B,D
3FFA 99           SBC A,C
3FFB A1           AND C
3FFC A1           AND C
3FFD 99           SBC A,C
3FFE 42           LD B,D
3FFF 3C           INC A
