// Generated from Opcodes by tools/Z80DecoderGenerator; do not edit cases.
namespace Hc2000.Z80;

public sealed partial class Z80
{
    internal const string GeneratedBaseOpcodes = "00,01,02,03,04,05,06,07,08,09,0A,0B,0C,0D,0E,0F,10,11,12,13,14,15,16,17,18,19,1A,1B,1C,1D,1E,1F,20,21,22,23,24,25,26,27,28,29,2A,2B,2C,2D,2E,2F,30,31,32,33,34,35,36,37,38,39,3A,3B,3C,3D,3E,3F,40,41,42,43,44,45,46,47,48,49,4A,4B,4C,4D,4E,4F,50,51,52,53,54,55,56,57,58,59,5A,5B,5C,5D,5E,5F,60,61,62,63,64,65,66,67,68,69,6A,6B,6C,6D,6E,6F,70,71,72,73,74,75,76,77,78,79,7A,7B,7C,7D,7E,7F,80,81,82,83,84,85,86,87,88,89,8A,8B,8C,8D,8E,8F,90,91,92,93,94,95,96,97,98,99,9A,9B,9C,9D,9E,9F,A0,A1,A2,A3,A4,A5,A6,A7,A8,A9,AA,AB,AC,AD,AE,AF,B0,B1,B2,B3,B4,B5,B6,B7,B8,B9,BA,BB,BC,BD,BE,BF,C0,C1,C2,C3,C4,C5,C6,C7,C8,C9,CA,CC,CD,CE,CF,D0,D1,D2,D3,D4,D5,D6,D7,D8,D9,DA,DB,DC,DE,DF,E0,E1,E2,E3,E4,E5,E6,E7,E8,E9,EA,EB,EC,EE,EF,F0,F1,F2,F3,F4,F5,F6,F7,F8,F9,FA,FB,FC,FE,FF";
    private void ExecuteGeneratedBase(byte opcode, byte previousQ)
    {
        switch (opcode)
        {
            case 0x00: {  return; } // NOP
            case 0x01: { R.BC = ReadWordImmediate(); return; } // LD BC,nn
            case 0x02: { ExecuteControl(new DecoderKey(PrefixFamily.Base, 0x02)); return; } // LD (BC),A
            case 0x03: { Idle((ushort)((R.I << 8) | R.R), 2); R.BC = unchecked((ushort)(R.BC + 1)); return; } // INC BC
            case 0x04: { R.B = IncDec(R.B, false); return; } // INC B
            case 0x05: { R.B = IncDec(R.B, true); return; } // DEC B
            case 0x06: { R.B = ReadImmediate(); return; } // LD B,n
            case 0x07: { ExecuteAccumulatorMisc(0, previousQ); return; } // RLCA
            case 0x08: { (R.AF, R.AFShadow) = (R.AFShadow, R.AF); return; } // EX AF,AF'
            case 0x09: { Add16(0, PrefixFamily.Base); return; } // ADD HL,BC
            case 0x0a: { ExecuteControl(new DecoderKey(PrefixFamily.Base, 0x0a)); return; } // LD A,(BC)
            case 0x0b: { Idle((ushort)((R.I << 8) | R.R), 2); R.BC = unchecked((ushort)(R.BC - 1)); return; } // DEC BC
            case 0x0c: { R.C = IncDec(R.C, false); return; } // INC C
            case 0x0d: { R.C = IncDec(R.C, true); return; } // DEC C
            case 0x0e: { R.C = ReadImmediate(); return; } // LD C,n
            case 0x0f: { ExecuteAccumulatorMisc(1, previousQ); return; } // RRCA
            case 0x10: { Idle((ushort)((R.I << 8) | R.R), 1); R.B--; var displacement = (sbyte)ReadImmediate(); if (R.B != 0) { Idle(unchecked((ushort)(R.PC - 1)), 5); R.PC = R.WZ = unchecked((ushort)(R.PC + displacement)); } return; } // DJNZ e
            case 0x11: { R.DE = ReadWordImmediate(); return; } // LD DE,nn
            case 0x12: { ExecuteControl(new DecoderKey(PrefixFamily.Base, 0x12)); return; } // LD (DE),A
            case 0x13: { Idle((ushort)((R.I << 8) | R.R), 2); R.DE = unchecked((ushort)(R.DE + 1)); return; } // INC DE
            case 0x14: { R.D = IncDec(R.D, false); return; } // INC D
            case 0x15: { R.D = IncDec(R.D, true); return; } // DEC D
            case 0x16: { R.D = ReadImmediate(); return; } // LD D,n
            case 0x17: { ExecuteAccumulatorMisc(2, previousQ); return; } // RLA
            case 0x18: { var displacement = (sbyte)ReadImmediate(); if (true) { Idle(unchecked((ushort)(R.PC - 1)), 5); R.PC = R.WZ = unchecked((ushort)(R.PC + displacement)); } return; } // JR e
            case 0x19: { Add16(1, PrefixFamily.Base); return; } // ADD HL,DE
            case 0x1a: { ExecuteControl(new DecoderKey(PrefixFamily.Base, 0x1a)); return; } // LD A,(DE)
            case 0x1b: { Idle((ushort)((R.I << 8) | R.R), 2); R.DE = unchecked((ushort)(R.DE - 1)); return; } // DEC DE
            case 0x1c: { R.E = IncDec(R.E, false); return; } // INC E
            case 0x1d: { R.E = IncDec(R.E, true); return; } // DEC E
            case 0x1e: { R.E = ReadImmediate(); return; } // LD E,n
            case 0x1f: { ExecuteAccumulatorMisc(3, previousQ); return; } // RRA
            case 0x20: { var displacement = (sbyte)ReadImmediate(); if (Condition(0)) { Idle(unchecked((ushort)(R.PC - 1)), 5); R.PC = R.WZ = unchecked((ushort)(R.PC + displacement)); } return; } // JR NZ,e
            case 0x21: { R.HL = ReadWordImmediate(); return; } // LD HL,nn
            case 0x22: { ExecuteControl(new DecoderKey(PrefixFamily.Base, 0x22)); return; } // LD (nn),HL
            case 0x23: { Idle((ushort)((R.I << 8) | R.R), 2); R.HL = unchecked((ushort)(R.HL + 1)); return; } // INC HL
            case 0x24: { R.H = IncDec(R.H, false); return; } // INC H
            case 0x25: { R.H = IncDec(R.H, true); return; } // DEC H
            case 0x26: { R.H = ReadImmediate(); return; } // LD H,n
            case 0x27: { ExecuteAccumulatorMisc(4, previousQ); return; } // DAA
            case 0x28: { var displacement = (sbyte)ReadImmediate(); if (Condition(1)) { Idle(unchecked((ushort)(R.PC - 1)), 5); R.PC = R.WZ = unchecked((ushort)(R.PC + displacement)); } return; } // JR Z,e
            case 0x29: { Add16(2, PrefixFamily.Base); return; } // ADD HL,HL
            case 0x2a: { ExecuteControl(new DecoderKey(PrefixFamily.Base, 0x2a)); return; } // LD HL,(nn)
            case 0x2b: { Idle((ushort)((R.I << 8) | R.R), 2); R.HL = unchecked((ushort)(R.HL - 1)); return; } // DEC HL
            case 0x2c: { R.L = IncDec(R.L, false); return; } // INC L
            case 0x2d: { R.L = IncDec(R.L, true); return; } // DEC L
            case 0x2e: { R.L = ReadImmediate(); return; } // LD L,n
            case 0x2f: { ExecuteAccumulatorMisc(5, previousQ); return; } // CPL
            case 0x30: { var displacement = (sbyte)ReadImmediate(); if (Condition(2)) { Idle(unchecked((ushort)(R.PC - 1)), 5); R.PC = R.WZ = unchecked((ushort)(R.PC + displacement)); } return; } // JR NC,e
            case 0x31: { R.SP = ReadWordImmediate(); return; } // LD SP,nn
            case 0x32: { ExecuteControl(new DecoderKey(PrefixFamily.Base, 0x32)); return; } // LD (nn),A
            case 0x33: { Idle((ushort)((R.I << 8) | R.R), 2); R.SP = unchecked((ushort)(R.SP + 1)); return; } // INC SP
            case 0x34: { var address = R.HL; var value = ReadMemory(address); Idle(address, 1); WriteMemory(address, IncDec(value, false)); return; } // INC (HL)
            case 0x35: { var address = R.HL; var value = ReadMemory(address); Idle(address, 1); WriteMemory(address, IncDec(value, true)); return; } // DEC (HL)
            case 0x36: { WriteMemory(R.HL, ReadImmediate()); return; } // LD (HL),n
            case 0x37: { ExecuteAccumulatorMisc(6, previousQ); return; } // SCF
            case 0x38: { var displacement = (sbyte)ReadImmediate(); if (Condition(3)) { Idle(unchecked((ushort)(R.PC - 1)), 5); R.PC = R.WZ = unchecked((ushort)(R.PC + displacement)); } return; } // JR C,e
            case 0x39: { Add16(3, PrefixFamily.Base); return; } // ADD HL,SP
            case 0x3a: { ExecuteControl(new DecoderKey(PrefixFamily.Base, 0x3a)); return; } // LD A,(nn)
            case 0x3b: { Idle((ushort)((R.I << 8) | R.R), 2); R.SP = unchecked((ushort)(R.SP - 1)); return; } // DEC SP
            case 0x3c: { R.A = IncDec(R.A, false); return; } // INC A
            case 0x3d: { R.A = IncDec(R.A, true); return; } // DEC A
            case 0x3e: { R.A = ReadImmediate(); return; } // LD A,n
            case 0x3f: { ExecuteAccumulatorMisc(7, previousQ); return; } // CCF
            case 0x40: {  return; } // LD B,B
            case 0x41: { R.B = R.C; return; } // LD B,C
            case 0x42: { R.B = R.D; return; } // LD B,D
            case 0x43: { R.B = R.E; return; } // LD B,E
            case 0x44: { R.B = R.H; return; } // LD B,H
            case 0x45: { R.B = R.L; return; } // LD B,L
            case 0x46: { R.B = ReadMemory(R.HL); return; } // LD B,(HL)
            case 0x47: { R.B = R.A; return; } // LD B,A
            case 0x48: { R.C = R.B; return; } // LD C,B
            case 0x49: {  return; } // LD C,C
            case 0x4a: { R.C = R.D; return; } // LD C,D
            case 0x4b: { R.C = R.E; return; } // LD C,E
            case 0x4c: { R.C = R.H; return; } // LD C,H
            case 0x4d: { R.C = R.L; return; } // LD C,L
            case 0x4e: { R.C = ReadMemory(R.HL); return; } // LD C,(HL)
            case 0x4f: { R.C = R.A; return; } // LD C,A
            case 0x50: { R.D = R.B; return; } // LD D,B
            case 0x51: { R.D = R.C; return; } // LD D,C
            case 0x52: {  return; } // LD D,D
            case 0x53: { R.D = R.E; return; } // LD D,E
            case 0x54: { R.D = R.H; return; } // LD D,H
            case 0x55: { R.D = R.L; return; } // LD D,L
            case 0x56: { R.D = ReadMemory(R.HL); return; } // LD D,(HL)
            case 0x57: { R.D = R.A; return; } // LD D,A
            case 0x58: { R.E = R.B; return; } // LD E,B
            case 0x59: { R.E = R.C; return; } // LD E,C
            case 0x5a: { R.E = R.D; return; } // LD E,D
            case 0x5b: {  return; } // LD E,E
            case 0x5c: { R.E = R.H; return; } // LD E,H
            case 0x5d: { R.E = R.L; return; } // LD E,L
            case 0x5e: { R.E = ReadMemory(R.HL); return; } // LD E,(HL)
            case 0x5f: { R.E = R.A; return; } // LD E,A
            case 0x60: { R.H = R.B; return; } // LD H,B
            case 0x61: { R.H = R.C; return; } // LD H,C
            case 0x62: { R.H = R.D; return; } // LD H,D
            case 0x63: { R.H = R.E; return; } // LD H,E
            case 0x64: {  return; } // LD H,H
            case 0x65: { R.H = R.L; return; } // LD H,L
            case 0x66: { R.H = ReadMemory(R.HL); return; } // LD H,(HL)
            case 0x67: { R.H = R.A; return; } // LD H,A
            case 0x68: { R.L = R.B; return; } // LD L,B
            case 0x69: { R.L = R.C; return; } // LD L,C
            case 0x6a: { R.L = R.D; return; } // LD L,D
            case 0x6b: { R.L = R.E; return; } // LD L,E
            case 0x6c: { R.L = R.H; return; } // LD L,H
            case 0x6d: {  return; } // LD L,L
            case 0x6e: { R.L = ReadMemory(R.HL); return; } // LD L,(HL)
            case 0x6f: { R.L = R.A; return; } // LD L,A
            case 0x70: { WriteMemory(R.HL, R.B); return; } // LD (HL),B
            case 0x71: { WriteMemory(R.HL, R.C); return; } // LD (HL),C
            case 0x72: { WriteMemory(R.HL, R.D); return; } // LD (HL),D
            case 0x73: { WriteMemory(R.HL, R.E); return; } // LD (HL),E
            case 0x74: { WriteMemory(R.HL, R.H); return; } // LD (HL),H
            case 0x75: { WriteMemory(R.HL, R.L); return; } // LD (HL),L
            case 0x76: { Halted = true; return; } // HALT
            case 0x77: { WriteMemory(R.HL, R.A); return; } // LD (HL),A
            case 0x78: { R.A = R.B; return; } // LD A,B
            case 0x79: { R.A = R.C; return; } // LD A,C
            case 0x7a: { R.A = R.D; return; } // LD A,D
            case 0x7b: { R.A = R.E; return; } // LD A,E
            case 0x7c: { R.A = R.H; return; } // LD A,H
            case 0x7d: { R.A = R.L; return; } // LD A,L
            case 0x7e: { R.A = ReadMemory(R.HL); return; } // LD A,(HL)
            case 0x7f: {  return; } // LD A,A
            case 0x80: { Alu(0, R.B); return; } // ADD A,B
            case 0x81: { Alu(0, R.C); return; } // ADD A,C
            case 0x82: { Alu(0, R.D); return; } // ADD A,D
            case 0x83: { Alu(0, R.E); return; } // ADD A,E
            case 0x84: { Alu(0, R.H); return; } // ADD A,H
            case 0x85: { Alu(0, R.L); return; } // ADD A,L
            case 0x86: { Alu(0, ReadMemory(R.HL)); return; } // ADD A,(HL)
            case 0x87: { Alu(0, R.A); return; } // ADD A,A
            case 0x88: { Alu(1, R.B); return; } // ADC A,B
            case 0x89: { Alu(1, R.C); return; } // ADC A,C
            case 0x8a: { Alu(1, R.D); return; } // ADC A,D
            case 0x8b: { Alu(1, R.E); return; } // ADC A,E
            case 0x8c: { Alu(1, R.H); return; } // ADC A,H
            case 0x8d: { Alu(1, R.L); return; } // ADC A,L
            case 0x8e: { Alu(1, ReadMemory(R.HL)); return; } // ADC A,(HL)
            case 0x8f: { Alu(1, R.A); return; } // ADC A,A
            case 0x90: { Alu(2, R.B); return; } // SUB B
            case 0x91: { Alu(2, R.C); return; } // SUB C
            case 0x92: { Alu(2, R.D); return; } // SUB D
            case 0x93: { Alu(2, R.E); return; } // SUB E
            case 0x94: { Alu(2, R.H); return; } // SUB H
            case 0x95: { Alu(2, R.L); return; } // SUB L
            case 0x96: { Alu(2, ReadMemory(R.HL)); return; } // SUB (HL)
            case 0x97: { Alu(2, R.A); return; } // SUB A
            case 0x98: { Alu(3, R.B); return; } // SBC A,B
            case 0x99: { Alu(3, R.C); return; } // SBC A,C
            case 0x9a: { Alu(3, R.D); return; } // SBC A,D
            case 0x9b: { Alu(3, R.E); return; } // SBC A,E
            case 0x9c: { Alu(3, R.H); return; } // SBC A,H
            case 0x9d: { Alu(3, R.L); return; } // SBC A,L
            case 0x9e: { Alu(3, ReadMemory(R.HL)); return; } // SBC A,(HL)
            case 0x9f: { Alu(3, R.A); return; } // SBC A,A
            case 0xa0: { Alu(4, R.B); return; } // AND B
            case 0xa1: { Alu(4, R.C); return; } // AND C
            case 0xa2: { Alu(4, R.D); return; } // AND D
            case 0xa3: { Alu(4, R.E); return; } // AND E
            case 0xa4: { Alu(4, R.H); return; } // AND H
            case 0xa5: { Alu(4, R.L); return; } // AND L
            case 0xa6: { Alu(4, ReadMemory(R.HL)); return; } // AND (HL)
            case 0xa7: { Alu(4, R.A); return; } // AND A
            case 0xa8: { Alu(5, R.B); return; } // XOR B
            case 0xa9: { Alu(5, R.C); return; } // XOR C
            case 0xaa: { Alu(5, R.D); return; } // XOR D
            case 0xab: { Alu(5, R.E); return; } // XOR E
            case 0xac: { Alu(5, R.H); return; } // XOR H
            case 0xad: { Alu(5, R.L); return; } // XOR L
            case 0xae: { Alu(5, ReadMemory(R.HL)); return; } // XOR (HL)
            case 0xaf: { Alu(5, R.A); return; } // XOR A
            case 0xb0: { Alu(6, R.B); return; } // OR B
            case 0xb1: { Alu(6, R.C); return; } // OR C
            case 0xb2: { Alu(6, R.D); return; } // OR D
            case 0xb3: { Alu(6, R.E); return; } // OR E
            case 0xb4: { Alu(6, R.H); return; } // OR H
            case 0xb5: { Alu(6, R.L); return; } // OR L
            case 0xb6: { Alu(6, ReadMemory(R.HL)); return; } // OR (HL)
            case 0xb7: { Alu(6, R.A); return; } // OR A
            case 0xb8: { Alu(7, R.B); return; } // CP B
            case 0xb9: { Alu(7, R.C); return; } // CP C
            case 0xba: { Alu(7, R.D); return; } // CP D
            case 0xbb: { Alu(7, R.E); return; } // CP E
            case 0xbc: { Alu(7, R.H); return; } // CP H
            case 0xbd: { Alu(7, R.L); return; } // CP L
            case 0xbe: { Alu(7, ReadMemory(R.HL)); return; } // CP (HL)
            case 0xbf: { Alu(7, R.A); return; } // CP A
            case 0xc0: { Idle((ushort)((R.I << 8) | R.R), 1); if (Condition(0)) R.PC = R.WZ = Pop(); return; } // RET NZ
            case 0xc1: { R.BC = Pop(); return; } // POP BC
            case 0xc2: { R.WZ = ReadWordImmediate(); if (Condition(0)) R.PC = R.WZ; return; } // JP NZ,nn
            case 0xc3: { R.PC = R.WZ = ReadWordImmediate(); return; } // JP nn
            case 0xc4: { R.WZ = ReadWordImmediate(); if (Condition(0)) { Idle(unchecked((ushort)(R.PC - 1)), 1); Push(R.PC); R.PC = R.WZ; } return; } // CALL NZ,nn
            case 0xc5: { Idle((ushort)((R.I << 8) | R.R), 1); Push(R.BC); return; } // PUSH BC
            case 0xc6: { Alu(0, ReadImmediate()); return; } // ADD A,n
            case 0xc7: { Idle((ushort)((R.I << 8) | R.R), 1); Push(R.PC); R.PC = R.WZ = 0x00; return; } // RST 00h
            case 0xc8: { Idle((ushort)((R.I << 8) | R.R), 1); if (Condition(1)) R.PC = R.WZ = Pop(); return; } // RET Z
            case 0xc9: { R.PC = R.WZ = Pop(); return; } // RET
            case 0xca: { R.WZ = ReadWordImmediate(); if (Condition(1)) R.PC = R.WZ; return; } // JP Z,nn
            case 0xcc: { R.WZ = ReadWordImmediate(); if (Condition(1)) { Idle(unchecked((ushort)(R.PC - 1)), 1); Push(R.PC); R.PC = R.WZ; } return; } // CALL Z,nn
            case 0xcd: { R.WZ = ReadWordImmediate(); Idle(unchecked((ushort)(R.PC - 1)), 1); Push(R.PC); R.PC = R.WZ; return; } // CALL nn
            case 0xce: { Alu(1, ReadImmediate()); return; } // ADC A,n
            case 0xcf: { Idle((ushort)((R.I << 8) | R.R), 1); Push(R.PC); R.PC = R.WZ = 0x08; return; } // RST 08h
            case 0xd0: { Idle((ushort)((R.I << 8) | R.R), 1); if (Condition(2)) R.PC = R.WZ = Pop(); return; } // RET NC
            case 0xd1: { R.DE = Pop(); return; } // POP DE
            case 0xd2: { R.WZ = ReadWordImmediate(); if (Condition(2)) R.PC = R.WZ; return; } // JP NC,nn
            case 0xd3: { var n = ReadImmediate(); var port = (ushort)((R.A << 8) | n); Output(port, R.A); R.WZ = (ushort)((port & 0xff00) | ((n + 1) & 255)); return; } // OUT (n),A
            case 0xd4: { R.WZ = ReadWordImmediate(); if (Condition(2)) { Idle(unchecked((ushort)(R.PC - 1)), 1); Push(R.PC); R.PC = R.WZ; } return; } // CALL NC,nn
            case 0xd5: { Idle((ushort)((R.I << 8) | R.R), 1); Push(R.DE); return; } // PUSH DE
            case 0xd6: { Alu(2, ReadImmediate()); return; } // SUB n
            case 0xd7: { Idle((ushort)((R.I << 8) | R.R), 1); Push(R.PC); R.PC = R.WZ = 0x10; return; } // RST 10h
            case 0xd8: { Idle((ushort)((R.I << 8) | R.R), 1); if (Condition(3)) R.PC = R.WZ = Pop(); return; } // RET C
            case 0xd9: { (R.BC, R.BCShadow) = (R.BCShadow, R.BC); (R.DE, R.DEShadow) = (R.DEShadow, R.DE); (R.HL, R.HLShadow) = (R.HLShadow, R.HL); return; } // EXX
            case 0xda: { R.WZ = ReadWordImmediate(); if (Condition(3)) R.PC = R.WZ; return; } // JP C,nn
            case 0xdb: { var n = ReadImmediate(); var port = (ushort)((R.A << 8) | n); R.A = Input(port); R.WZ = unchecked((ushort)(port + 1)); return; } // IN A,(n)
            case 0xdc: { R.WZ = ReadWordImmediate(); if (Condition(3)) { Idle(unchecked((ushort)(R.PC - 1)), 1); Push(R.PC); R.PC = R.WZ; } return; } // CALL C,nn
            case 0xde: { Alu(3, ReadImmediate()); return; } // SBC A,n
            case 0xdf: { Idle((ushort)((R.I << 8) | R.R), 1); Push(R.PC); R.PC = R.WZ = 0x18; return; } // RST 18h
            case 0xe0: { Idle((ushort)((R.I << 8) | R.R), 1); if (Condition(4)) R.PC = R.WZ = Pop(); return; } // RET PO
            case 0xe1: { R.HL = Pop(); return; } // POP HL
            case 0xe2: { R.WZ = ReadWordImmediate(); if (Condition(4)) R.PC = R.WZ; return; } // JP PO,nn
            case 0xe3: { var low = ReadMemory(R.SP); var highAddress = unchecked((ushort)(R.SP + 1)); var high = ReadMemory(highAddress); Idle(highAddress, 1); var old = R.HL; WriteMemory(highAddress, (byte)(old >> 8)); WriteMemory(R.SP, (byte)old); Idle(R.SP, 2); R.HL = R.WZ = (ushort)(low | (high << 8)); return; } // EX (SP),HL
            case 0xe4: { R.WZ = ReadWordImmediate(); if (Condition(4)) { Idle(unchecked((ushort)(R.PC - 1)), 1); Push(R.PC); R.PC = R.WZ; } return; } // CALL PO,nn
            case 0xe5: { Idle((ushort)((R.I << 8) | R.R), 1); Push(R.HL); return; } // PUSH HL
            case 0xe6: { Alu(4, ReadImmediate()); return; } // AND n
            case 0xe7: { Idle((ushort)((R.I << 8) | R.R), 1); Push(R.PC); R.PC = R.WZ = 0x20; return; } // RST 20h
            case 0xe8: { Idle((ushort)((R.I << 8) | R.R), 1); if (Condition(5)) R.PC = R.WZ = Pop(); return; } // RET PE
            case 0xe9: { R.PC = R.HL; return; } // JP (HL)
            case 0xea: { R.WZ = ReadWordImmediate(); if (Condition(5)) R.PC = R.WZ; return; } // JP PE,nn
            case 0xeb: { (R.DE, R.HL) = (R.HL, R.DE); return; } // EX DE,HL
            case 0xec: { R.WZ = ReadWordImmediate(); if (Condition(5)) { Idle(unchecked((ushort)(R.PC - 1)), 1); Push(R.PC); R.PC = R.WZ; } return; } // CALL PE,nn
            case 0xee: { Alu(5, ReadImmediate()); return; } // XOR n
            case 0xef: { Idle((ushort)((R.I << 8) | R.R), 1); Push(R.PC); R.PC = R.WZ = 0x28; return; } // RST 28h
            case 0xf0: { Idle((ushort)((R.I << 8) | R.R), 1); if (Condition(6)) R.PC = R.WZ = Pop(); return; } // RET P
            case 0xf1: { R.AF = Pop(); return; } // POP AF
            case 0xf2: { R.WZ = ReadWordImmediate(); if (Condition(6)) R.PC = R.WZ; return; } // JP P,nn
            case 0xf3: { R.IFF1 = R.IFF2 = false; eiDelay = 0; return; } // DI
            case 0xf4: { R.WZ = ReadWordImmediate(); if (Condition(6)) { Idle(unchecked((ushort)(R.PC - 1)), 1); Push(R.PC); R.PC = R.WZ; } return; } // CALL P,nn
            case 0xf5: { Idle((ushort)((R.I << 8) | R.R), 1); Push(R.AF); return; } // PUSH AF
            case 0xf6: { Alu(6, ReadImmediate()); return; } // OR n
            case 0xf7: { Idle((ushort)((R.I << 8) | R.R), 1); Push(R.PC); R.PC = R.WZ = 0x30; return; } // RST 30h
            case 0xf8: { Idle((ushort)((R.I << 8) | R.R), 1); if (Condition(7)) R.PC = R.WZ = Pop(); return; } // RET M
            case 0xf9: { Idle((ushort)((R.I << 8) | R.R), 2); R.SP = R.HL; return; } // LD SP,HL
            case 0xfa: { R.WZ = ReadWordImmediate(); if (Condition(7)) R.PC = R.WZ; return; } // JP M,nn
            case 0xfb: { R.IFF1 = R.IFF2 = true; eiDelay = 2; return; } // EI
            case 0xfc: { R.WZ = ReadWordImmediate(); if (Condition(7)) { Idle(unchecked((ushort)(R.PC - 1)), 1); Push(R.PC); R.PC = R.WZ; } return; } // CALL M,nn
            case 0xfe: { Alu(7, ReadImmediate()); return; } // CP n
            case 0xff: { Idle((ushort)((R.I << 8) | R.R), 1); Push(R.PC); R.PC = R.WZ = 0x38; return; } // RST 38h
            default: throw new ArgumentOutOfRangeException(nameof(opcode));
        }
    }
}
