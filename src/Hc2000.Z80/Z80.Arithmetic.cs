namespace Hc2000.Z80;

public sealed partial class Z80
{
    private const int Carry = 1, Subtract = 2, Parity = 4, X = 8, Half = 16, Y = 32, Zero = 64, Sign = 128;

    private static byte Sz53(int value) => (byte)((value & (Sign | X | Y)) | ((byte)value == 0 ? Zero : 0));
    private static byte Sz53P(int value)
    {
        var v = (byte)value;
        var p = v ^ (v >> 4);
        p ^= p >> 2;
        p ^= p >> 1;
        return (byte)(Sz53(v) | ((p & 1) == 0 ? Parity : 0));
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void Flags(int flags) { R.F = (byte)flags; R.Q = (byte)flags; }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void AccumulatorAndFlags(byte value, int flags)
    {
        // Publish the two halves together instead of chaining AF stores/loads.
        // Arithmetic has no bus callback between its A and F/Q updates.
        R.AF = (ushort)((value << 8) | (byte)flags);
        R.Q = (byte)flags;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void Alu(int operation, byte value)
    {
        var a = R.A;
        var c = operation is 1 or 3 ? R.F & Carry : 0;
        if (operation <= 3 || operation == 7)
        {
            var sub = operation is 2 or 3 or 7;
            var result = sub ? a - value - c : a + value + c;
            var overflow = sub ? ((a ^ value) & (a ^ result) & 0x80) : (~(a ^ value) & (a ^ result) & 0x80);
            var f = Sz53(result) | ((a ^ value ^ result) & Half) | (overflow != 0 ? Parity : 0)
                | ((result & 0x100) != 0 ? Carry : 0) | (sub ? Subtract : 0);
            if (operation == 7)
                Flags((f & ~(X | Y)) | (value & (X | Y)));
            else
                AccumulatorAndFlags((byte)result, f);
        }
        else
        {
            var result = operation switch { 4 => (byte)(a & value), 5 => (byte)(a ^ value), _ => (byte)(a | value) };
            AccumulatorAndFlags(result, Sz53P(result) | (operation == 4 ? Half : 0));
        }
    }

    private byte IncDec(byte value, bool decrement)
    {
        var result = (byte)(decrement ? value - 1 : value + 1);
        Flags((R.F & Carry) | Sz53(result) | ((value ^ result) & Half)
            | (decrement ? Subtract : 0) | (value == (decrement ? 0x80 : 0x7f) ? Parity : 0));
        return result;
    }

    private byte Rotate(int operation, byte value)
    {
        var c = R.F & Carry;
        var outgoing = operation is 0 or 2 or 4 or 6 ? value >> 7 : value & 1;
        var result = (byte)(operation switch
        {
            0 => (value << 1) | outgoing, 1 => (value >> 1) | (outgoing << 7),
            2 => (value << 1) | c, 3 => (value >> 1) | (c << 7),
            4 => value << 1, 5 => (value >> 1) | (value & 0x80),
            6 => (value << 1) | 1, _ => value >> 1
        });
        Flags(Sz53P(result) | outgoing);
        return result;
    }

    private void DecimalAdjust()
    {
        var old = R.A;
        var correction = 0;
        var carry = R.F & Carry;
        if ((R.F & Half) != 0 || (old & 15) > 9) correction |= 6;
        if (carry != 0 || old > 0x99) { correction |= 0x60; carry = Carry; }
        R.A = (byte)((R.F & Subtract) != 0 ? old - correction : old + correction);
        Flags((R.F & Subtract) | carry | ((old ^ R.A) & Half) | Sz53P(R.A));
    }

    private ushort Pair(int p, PrefixFamily family) => p switch
    {
        0 => R.BC, 1 => R.DE, 2 => family == PrefixFamily.Base ? R.HL : Index(family), _ => R.SP
    };
    private void SetPair(int p, PrefixFamily family, ushort value)
    {
        switch (p)
        {
            case 0: R.BC = value; break;
            case 1: R.DE = value; break;
            case 2:
                if (family == PrefixFamily.Base) R.HL = value;
                else if (family == PrefixFamily.DD) R.IX = value;
                else R.IY = value;
                break;
            default: R.SP = value; break;
        }
    }

    private void Add16(int p, PrefixFamily family)
    {
        var a = Pair(2, family);
        var b = Pair(p, family);
        var result = a + b;
        Idle((ushort)((R.I << 8) | R.R), 7);
        R.WZ = unchecked((ushort)(a + 1));
        SetPair(2, family, (ushort)result);
        Flags((R.F & (Sign | Zero | Parity)) | ((result >> 8) & (X | Y))
            | (((a ^ b ^ result) >> 8) & Half) | (result > 65535 ? Carry : 0));
    }

    private void ExecuteBase(DecoderKey key, byte previousQ)
    {
        var opcode = key.Opcode;
        var family = key.PrefixFamily;
        var indexed = family is PrefixFamily.DD or PrefixFamily.FD;
        var x = opcode >> 6; var y = (opcode >> 3) & 7; var z = opcode & 7;
        var p = y >> 1; var q = y & 1;
        if (x == 2)
        {
            var value = z == 6 ? ReadMemory(indexed ? IndexedAddress(family, 5) : R.HL) : ReadReg(z, family);
            Alu(y, value);
            return;
        }
        if (x == 3 && z == 6) { Alu(y, ReadImmediate()); return; }
        if (x == 0 && z is 4 or 5)
        {
            if (y == 6)
            {
                var address = indexed ? IndexedAddress(family, 5) : R.HL;
                var value = ReadMemory(address);
                Idle(address, 1);
                WriteMemory(address, IncDec(value, z == 5));
            }
            else WriteReg(y, family, IncDec(ReadReg(y, family), z == 5));
            return;
        }
        if (x == 0 && z == 1 && q == 1) { Add16(p, family); return; }
        if (x == 0 && z == 3)
        {
            Idle((ushort)((R.I << 8) | R.R), 2);
            SetPair(p, family, unchecked((ushort)(Pair(p, family) + (q == 0 ? 1 : -1))));
            return;
        }
        // Transfers here provide register and memory operands; complete control flow is slice 01c.
        if (x == 0 && z == 1 && q == 0) { SetPair(p, family, ReadWordImmediate()); return; }
        if (x == 0 && z == 6)
        {
            if (y == 6)
            {
                var address = indexed ? IndexedAddress(family, 0) : R.HL;
                var value = ReadImmediate();
                if (indexed) Idle(unchecked((ushort)(R.PC - 1)), 2);
                WriteMemory(address, value);
            }
            else WriteReg(y, family, ReadImmediate());
            return;
        }
        if (x == 1)
        {
            if (opcode == 0x76) { Halted = true; return; }
            if (y == 6 || z == 6)
            {
                var address = indexed ? IndexedAddress(family, 5) : R.HL;
                if (z == 6) WriteReg(y, PrefixFamily.Base, ReadMemory(address));
                else WriteMemory(address, ReadReg(z, PrefixFamily.Base));
            }
            else WriteReg(y, family, ReadReg(z, family));
            return;
        }
        if (opcode == 0) return;
        if (x == 0 && z == 7) { ExecuteAccumulatorMisc(y, previousQ); return; }
        ExecuteControl(key);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void ExecuteAccumulatorMisc(int y, byte previousQ)
    {
        if (y < 4)
        {
            // Accumulator rotates retain S/Z/PV. Do not calculate the CB
            // rotate's SZ/parity or publish an intermediate F/Q only to
            // overwrite both immediately afterwards.
            var retained = R.F & (Sign | Zero | Parity);
            var a = R.A;
            var carry = R.F & Carry;
            var outgoing = (y & 1) == 0 ? a >> 7 : a & 1;
            var value = (byte)(y switch
            {
                0 => (a << 1) | outgoing,
                1 => (a >> 1) | (outgoing << 7),
                2 => (a << 1) | carry,
                _ => (a >> 1) | (carry << 7)
            });
            AccumulatorAndFlags(value, retained | (value & (X | Y)) | outgoing);
        }
        else if (y == 4) DecimalAdjust();
        else if (y == 5)
        {
            R.A ^= 0xff;
            Flags((R.F & (Sign | Zero | Parity | Carry)) | Half | Subtract | (R.A & (X | Y)));
        }
        else
        {
            var f = R.F;
            var xy = (R.A | (previousQ == 0 ? f : 0)) & (X | Y);
            Flags((f & (Sign | Zero | Parity)) | xy | (y == 6 ? Carry : (f & Carry) == 0 ? Carry : Half));
        }
    }

    private void ExecuteCb(DecoderKey key, ushort address)
    {
        var indexed = key.PrefixFamily != PrefixFamily.CB;
        var opcode = key.Opcode;
        var x = opcode >> 6; var y = (opcode >> 3) & 7; var z = opcode & 7;
        var memory = indexed || z == 6;
        if (indexed) Idle(unchecked((ushort)(R.PC - 1)), 2);
        var value = memory ? ReadMemory(address) : ReadReg(z, PrefixFamily.Base);
        if (memory) Idle(address, 1);
        if (x == 1)
        {
            var tested = value & (1 << y);
            var xy = memory ? (R.WZ >> 8) & (X | Y) : value & (X | Y);
            Flags((R.F & Carry) | Half | xy | (tested == 0 ? Zero | Parity : 0) | (y == 7 && tested != 0 ? Sign : 0));
            return;
        }
        var result = x switch { 0 => Rotate(y, value), 2 => (byte)(value & ~(1 << y)), _ => (byte)(value | (1 << y)) };
        if (memory) WriteMemory(address, result);
        if (z != 6) WriteReg(z, PrefixFamily.Base, result);
    }
}
