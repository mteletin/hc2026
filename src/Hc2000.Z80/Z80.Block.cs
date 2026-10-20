namespace Hc2000.Z80;

public sealed partial class Z80
{
    private void ExecuteBlock(DecoderKey key)
    {
        var opcode = key.Opcode;
        var direction = (opcode & 8) == 0 ? 1 : -1;
        var operation = opcode & 3;
        var ir = (ushort)((R.I << 8) | R.R);
        var repeatAddress = operation == 0 ? R.DE : R.HL;
        byte ioData = 0;
        switch (operation)
        {
            case 0: // LDI / LDD
                var transferred = ReadMemory(R.HL);
                WriteMemory(R.DE, transferred);
                Idle(R.DE, 2);
                R.HL = unchecked((ushort)(R.HL + direction));
                R.DE = unchecked((ushort)(R.DE + direction));
                R.BC--;
                var sum = R.A + transferred;
                Flags((R.F & (Sign | Zero | Carry)) | (R.BC != 0 ? Parity : 0) | (sum & X) | ((sum & 2) << 4));
                break;
            case 1: // CPI / CPD
                var compared = ReadMemory(R.HL);
                Idle(R.HL, 5);
                var difference = R.A - compared;
                var halfBorrow = (R.A & 15) < (compared & 15);
                var adjusted = difference - (halfBorrow ? 1 : 0);
                R.HL = unchecked((ushort)(R.HL + direction));
                R.BC--;
                R.WZ = unchecked((ushort)(R.WZ + direction));
                Flags((R.F & Carry) | Subtract | (difference & Sign) | ((byte)difference == 0 ? Zero : 0)
                    | (halfBorrow ? Half : 0) | (R.BC != 0 ? Parity : 0) | (adjusted & X) | ((adjusted & 2) << 4));
                break;
            case 2: // INI / IND: input uses B before decrement
                Idle(ir, 1);
                var oldPort = R.BC;
                var input = ioData = Input(oldPort);
                WriteMemory(R.HL, input);
                R.HL = unchecked((ushort)(R.HL + direction));
                R.B--;
                R.WZ = unchecked((ushort)(oldPort + direction));
                BlockIoFlags(input, input + ((R.C + direction) & 255));
                break;
            default: // OUTI / OUTD: output uses B after decrement
                Idle(ir, 1);
                var output = ioData = ReadMemory(R.HL);
                R.HL = unchecked((ushort)(R.HL + direction));
                R.B--;
                Output(R.BC, output);
                R.WZ = unchecked((ushort)(R.BC + direction));
                BlockIoFlags(output, output + R.L);
                break;
        }
        var repeat = (opcode & 0x10) != 0 && (operation >= 2 ? R.B != 0 : R.BC != 0 && (operation != 1 || (R.F & Zero) == 0));
        if (!repeat) return;
        Idle(operation == 3 ? R.BC : repeatAddress, 5);
        R.PC = unchecked((ushort)(R.PC - 2)); // ED address, not an ignored preceding DD/FD
        var flags = R.F;
        if (operation >= 2)
        {
            // Banks 2018 extra-cycle transform, pinned in hc2000-z80-repeat-flags.
            var k = R.B & 7;
            if ((flags & Carry) != 0)
            {
                var negativeData = (ioData & 0x80) != 0;
                var half = negativeData ? (R.B & 15) == 0 : (R.B & 15) == 15;
                flags = (byte)((flags & ~Half) | (half ? Half : 0));
                k = (R.B + (negativeData ? -1 : 1)) & 7;
            }
            // PV ^= evenParity(k) ^ 1: flip iff k has odd parity.
            if ((Sz53P(k) & Parity) == 0) flags ^= Parity;
        }
        R.WZ = unchecked((ushort)(R.PC + 1));
        Flags((flags & ~(X | Y)) | ((R.PC >> 8) & (X | Y)));
    }

    private void BlockIoFlags(byte value, int sum)
    {
        Flags(Sz53(R.B) | ((value & 0x80) != 0 ? Subtract : 0) | (sum > 255 ? Half | Carry : 0)
            | (Sz53P((sum & 7) ^ R.B) & Parity));
    }
}
