using System.Runtime.CompilerServices;

namespace Hc2000.Z80;

public sealed partial class Z80
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte Fetch(bool instructionStart = false)
    {
        if (!fastNoContend) bus.Contend(R.PC, 4);
        TStates += 4;
        var memory = fastMemory;
        var value = m1Bus is not null && !Halted
            ? m1Bus.FetchM1(R.PC, instructionStart, TStates)
            : memory is not null ? memory[R.PC] : bus.Read(R.PC);
        R.R = (byte)((R.R & 0x80) | ((R.R + 1) & 0x7f));
        if (!Halted) R.PC = unchecked((ushort)(R.PC + 1));
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadMemory(ushort address)
    {
        if (!fastNoContend) bus.Contend(address, 3);
        var memory = fastMemory;
        if (memory is null)
        {
            TStates += 3; // observing buses read TStates inside Read: keep the exact per-access value
            return bus.Read(address);
        }
        fastTimer.Advance(3);
        return memory[address];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteMemory(ushort address, byte value)
    {
        if (!fastNoContend) bus.Contend(address, 3);
        var memory = fastMemory;
        if (memory is null)
        {
            TStates += 3; // observing buses read TStates inside Write: keep the exact per-access value
            bus.Write(address, value);
            return;
        }
        fastTimer.Advance(3);
        memory[address] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadImmediate()
    {
        var value = ReadMemory(R.PC);
        R.PC = unchecked((ushort)(R.PC + 1));
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort ReadWordImmediate()
    {
        var low = ReadImmediate();
        return (ushort)(low | (ReadImmediate() << 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Idle(ushort address, int count)
    {
        if (count == 0) return;
        if (!fastNoContend) bus.Contend(address, count);
        if (fastMemory is null)
            TStates += count; // observing buses read TStates inside Contend: exact per-access value
        else
            fastTimer.Advance(count);
    }

    private ushort IndexedAddress(PrefixFamily family, int delay)
    {
        var displacement = (sbyte)ReadImmediate();
        Idle(unchecked((ushort)(R.PC - 1)), delay);
        R.WZ = unchecked((ushort)(Index(family) + displacement));
        return R.WZ;
    }

    private ushort Index(PrefixFamily family) => family is PrefixFamily.DD or PrefixFamily.DDCB ? R.IX : R.IY;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadReg(int code, PrefixFamily family)
    {
        var indexed = family is PrefixFamily.DD or PrefixFamily.FD;
        return code switch
        {
            0 => R.B, 1 => R.C, 2 => R.D, 3 => R.E,
            4 => indexed ? (byte)(Index(family) >> 8) : R.H,
            5 => indexed ? (byte)Index(family) : R.L,
            7 => R.A,
            _ => throw new ArgumentOutOfRangeException(nameof(code))
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteReg(int code, PrefixFamily family, byte value)
    {
        var indexed = family is PrefixFamily.DD or PrefixFamily.FD;
        switch (code)
        {
            case 0: R.B = value; break;
            case 1: R.C = value; break;
            case 2: R.D = value; break;
            case 3: R.E = value; break;
            case 4:
                if (!indexed) R.H = value;
                else if (family == PrefixFamily.DD) R.IXH = value;
                else R.IYH = value;
                break;
            case 5:
                if (!indexed) R.L = value;
                else if (family == PrefixFamily.DD) R.IXL = value;
                else R.IYL = value;
                break;
            case 7: R.A = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(code));
        }
    }
}
