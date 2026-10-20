namespace Hc2000.Z80;

public sealed partial class Z80
{
    private byte Input(ushort port)
    {
        bus.Contend(port, 1);
        TStates++;
        var value = bus.In(port);
        bus.Contend(port, 3);
        TStates += 3;
        return value;
    }
    private void Output(ushort port, byte value)
    {
        bus.Contend(port, 1);
        TStates++;
        bus.Out(port, value);
        bus.Contend(port, 3);
        TStates += 3;
    }

    private void ExecuteEd(DecoderKey key)
    {
        var opcode = key.Opcode;
        var x = opcode >> 6; var y = (opcode >> 3) & 7; var z = opcode & 7;
        var p = y >> 1; var q = y & 1;
        if (opcodes[key].Pattern == "NOP") return;
        if (x == 1)
        {
            if (z == 0)
            {
                var port = R.BC;
                var value = Input(port);
                if (y != 6) WriteReg(y, PrefixFamily.Base, value);
                Flags((R.F & Carry) | Sz53P(value));
                R.WZ = unchecked((ushort)(port + 1));
                return;
            }
            if (z == 1)
            {
                Output(R.BC, y == 6 ? (byte)0 : ReadReg(y, PrefixFamily.Base));
                R.WZ = unchecked((ushort)(R.BC + 1));
                return;
            }
            if (z == 2)
            {
                var a = R.HL; var b = Pair(p, PrefixFamily.Base); var c = R.F & Carry;
                var sub = q == 0;
                var result = sub ? a - b - c : a + b + c;
                var overflow = sub ? (a ^ b) & (a ^ result) & 0x8000 : ~(a ^ b) & (a ^ result) & 0x8000;
                Idle((ushort)((R.I << 8) | R.R), 7);
                R.WZ = unchecked((ushort)(a + 1));
                R.HL = (ushort)result;
                Flags(((result >> 8) & (Sign | X | Y)) | (R.HL == 0 ? Zero : 0)
                    | (((a ^ b ^ result) >> 8) & Half) | (overflow != 0 ? Parity : 0)
                    | ((result & 0x10000) != 0 ? Carry : 0) | (sub ? Subtract : 0));
                return;
            }
            if (z == 3)
            {
                var address = ReadWordImmediate();
                if (q == 0)
                {
                    var value = Pair(p, PrefixFamily.Base);
                    WriteMemory(address, (byte)value);
                    WriteMemory(unchecked((ushort)(address + 1)), (byte)(value >> 8));
                }
                else
                {
                    var low = ReadMemory(address);
                    SetPair(p, PrefixFamily.Base, (ushort)(low | (ReadMemory(unchecked((ushort)(address + 1))) << 8)));
                }
                R.WZ = unchecked((ushort)(address + 1));
                return;
            }
            if (z == 5)
            {
                R.PC = R.WZ = Pop();
                R.IFF1 = R.IFF2;
                return;
            }
            if (z == 6)
            {
                R.IM = (byte)(y is 2 or 6 ? 1 : y is 3 or 7 ? 2 : 0);
                return;
            }
            if (z == 7 && y < 4)
            {
                Idle((ushort)((R.I << 8) | R.R), 1);
                if (y == 0) R.I = R.A;
                else if (y == 1) R.R = R.A;
                else
                {
                    R.A = y == 2 ? R.I : R.R;
                    Flags((R.F & Carry) | Sz53(R.A) | (R.IFF2 ? Parity : 0));
                    lastLdAiOrR = true;
                }
                return;
            }
            if (z == 4)
            {
                var value = R.A;
                R.A = 0;
                Alu(2, value);
                return;
            }
            if (z == 7 && y is 4 or 5)
            {
                var value = ReadMemory(R.HL);
                byte result;
                if (y == 4) // RRD
                {
                    result = (byte)((R.A << 4) | (value >> 4));
                    R.A = (byte)((R.A & 0xf0) | (value & 15));
                }
                else // RLD
                {
                    result = (byte)((value << 4) | (R.A & 15));
                    R.A = (byte)((R.A & 0xf0) | (value >> 4));
                }
                Idle(R.HL, 4);
                WriteMemory(R.HL, result);
                R.WZ = unchecked((ushort)(R.HL + 1));
                Flags((R.F & Carry) | Sz53P(R.A));
                return;
            }
        }
        if (x == 2 && y >= 4 && z < 4) { ExecuteBlock(key); return; }
        throw new InvalidOperationException($"Unmapped ED decoder key {key}.");
    }
}
