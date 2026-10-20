namespace Hc2000.Z80;

public sealed partial class Z80
{
    private int eiDelay;
    private bool lastLdAiOrR;
    internal bool EiPending { get => eiDelay != 0; set => eiDelay = value ? 1 : 0; }
    public bool LdAirPending { get => lastLdAiOrR; set => lastLdAiOrR = value; }
    /// <summary>Full-width EI delay counter for snapshot capture/restore; 0 when EI is not pending.</summary>
    public int EiDelayRaw { get => eiDelay; set => eiDelay = value; }
    /// <summary>Pending NMI edge for snapshot capture/restore.</summary>
    public bool NmiPendingState { get => nmiPending; set => nmiPending = value; }
    /// <summary>Enable the NMOS LD A,I / LD A,R interrupt parity quirk.</summary>
    public bool NmosLdAirInterruptBug { get; set; } = true;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void Push(ushort value)
    {
        R.SP = unchecked((ushort)(R.SP - 1));
        WriteMemory(R.SP, (byte)(value >> 8));
        R.SP = unchecked((ushort)(R.SP - 1));
        WriteMemory(R.SP, (byte)value);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private ushort Pop()
    {
        var low = ReadMemory(R.SP);
        R.SP = unchecked((ushort)(R.SP + 1));
        var high = ReadMemory(R.SP);
        R.SP = unchecked((ushort)(R.SP + 1));
        return (ushort)(low | (high << 8));
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private bool Condition(int y) => y switch
    {
        0 => (R.F & Zero) == 0, 1 => (R.F & Zero) != 0,
        2 => (R.F & Carry) == 0, 3 => (R.F & Carry) != 0,
        4 => (R.F & Parity) == 0, 5 => (R.F & Parity) != 0,
        6 => (R.F & Sign) == 0, _ => (R.F & Sign) != 0
    };
    private bool AcceptInterrupt()
    {
        if (nmiPending)
        {
            nmiPending = false;
            Halted = false;
            R.IFF2 = R.IFF1;
            R.IFF1 = false;
            R.R = (byte)((R.R & 0x80) | ((R.R + 1) & 0x7f));
            Idle(R.PC, 5);
            Push(R.PC);
            R.PC = R.WZ = 0x66;
            R.Q = 0;
            lastLdAiOrR = false;
            return true;
        }
        if (!IntLine || !R.IFF1 || eiDelay != 0) return false;
        Halted = false;
        R.IFF1 = R.IFF2 = false;
        if (lastLdAiOrR && NmosLdAirInterruptBug) R.F &= unchecked((byte)~Parity);
        lastLdAiOrR = false;
        R.Q = 0;
        R.R = (byte)((R.R & 0x80) | ((R.R + 1) & 0x7f));
        Idle(R.PC, 7);
        var vector = bus.IntVector();
        if (R.IM == 0 && (vector & 0xc7) != 0xc7)
            throw new NotSupportedException("IM 0 supports RST bus instructions only.");
        Push(R.PC);
        if (R.IM == 2)
        {
            var address = (ushort)((R.I << 8) | vector); // odd vector values are retained
            var low = ReadMemory(address);
            var high = ReadMemory(unchecked((ushort)(address + 1)));
            R.PC = (ushort)(low | (high << 8));
        }
        else R.PC = R.IM == 0 ? (ushort)(vector & 0x38) : (ushort)0x38;
        R.WZ = R.PC;
        return true;
    }

    private void ExecuteControl(DecoderKey key)
    {
        var opcode = key.Opcode;
        var family = key.PrefixFamily;
        var x = opcode >> 6; var y = (opcode >> 3) & 7; var z = opcode & 7;
        var p = y >> 1; var q = y & 1;
        if (x == 0)
        {
            if (z == 0)
            {
                if (y == 1) { (R.AF, R.AFShadow) = (R.AFShadow, R.AF); return; }
                if (y == 2) { Idle((ushort)((R.I << 8) | R.R), 1); R.B--; }
                var displacement = (sbyte)ReadImmediate();
                if (y == 2 ? R.B != 0 : y == 3 || Condition(y - 4))
                {
                    Idle(unchecked((ushort)(R.PC - 1)), 5);
                    R.PC = R.WZ = unchecked((ushort)(R.PC + displacement));
                }
                return;
            }
            if (z == 2)
            {
                ushort address;
                if (p < 2)
                {
                    address = p == 0 ? R.BC : R.DE;
                    if (q == 0)
                    {
                        WriteMemory(address, R.A);
                        R.WZ = (ushort)((R.A << 8) | ((address + 1) & 0xff));
                    }
                    else { R.A = ReadMemory(address); R.WZ = unchecked((ushort)(address + 1)); }
                }
                else
                {
                    address = ReadWordImmediate();
                    if (p == 2)
                    {
                        if (q == 0)
                        {
                            var value = Pair(2, family);
                            WriteMemory(address, (byte)value);
                            WriteMemory(unchecked((ushort)(address + 1)), (byte)(value >> 8));
                        }
                        else
                        {
                            var low = ReadMemory(address);
                            SetPair(2, family, (ushort)(low | (ReadMemory(unchecked((ushort)(address + 1))) << 8)));
                        }
                        R.WZ = unchecked((ushort)(address + 1));
                    }
                    else if (q == 0)
                    {
                        WriteMemory(address, R.A);
                        R.WZ = (ushort)((R.A << 8) | ((address + 1) & 0xff));
                    }
                    else { R.A = ReadMemory(address); R.WZ = unchecked((ushort)(address + 1)); }
                }
                return;
            }
        }
        if (x == 3)
        {
            switch (z)
            {
                case 0:
                    Idle((ushort)((R.I << 8) | R.R), 1);
                    if (Condition(y)) R.PC = R.WZ = Pop();
                    return;
                case 1:
                    if (q == 0)
                    {
                        var value = Pop();
                        if (p == 3) R.AF = value; else SetPair(p, family, value);
                    }
                    else switch (p)
                    {
                        case 0: R.PC = R.WZ = Pop(); break;
                        case 1:
                            (R.BC, R.BCShadow) = (R.BCShadow, R.BC);
                            (R.DE, R.DEShadow) = (R.DEShadow, R.DE);
                            (R.HL, R.HLShadow) = (R.HLShadow, R.HL);
                            break;
                        case 2: R.PC = Pair(2, family); break;
                        case 3: Idle((ushort)((R.I << 8) | R.R), 2); R.SP = Pair(2, family); break;
                    }
                    return;
                case 2:
                    R.WZ = ReadWordImmediate();
                    if (Condition(y)) R.PC = R.WZ;
                    return;
                case 3:
                    switch (y)
                    {
                        case 0: R.PC = R.WZ = ReadWordImmediate(); return;
                        case 2:
                        case 3:
                            var n = ReadImmediate();
                            var port = (ushort)((R.A << 8) | n);
                            if (y == 2) Output(port, R.A); else R.A = Input(port);
                            R.WZ = y == 2 ? (ushort)((port & 0xff00) | ((n + 1) & 255)) : unchecked((ushort)(port + 1));
                            return;
                        case 4:
                            var low = ReadMemory(R.SP);
                            var highAddress = unchecked((ushort)(R.SP + 1));
                            var high = ReadMemory(highAddress);
                            Idle(highAddress, 1);
                            var old = Pair(2, family);
                            WriteMemory(highAddress, (byte)(old >> 8));
                            WriteMemory(R.SP, (byte)old);
                            Idle(R.SP, 2);
                            SetPair(2, family, R.WZ = (ushort)(low | (high << 8)));
                            return;
                        case 5: (R.DE, R.HL) = (R.HL, R.DE); return;
                        case 6: R.IFF1 = R.IFF2 = false; eiDelay = 0; return;
                        case 7: R.IFF1 = R.IFF2 = true; eiDelay = 2; return;
                    }
                    break;
                case 4:
                    R.WZ = ReadWordImmediate();
                    if (Condition(y))
                    {
                        Idle(unchecked((ushort)(R.PC - 1)), 1);
                        Push(R.PC); R.PC = R.WZ;
                    }
                    return;
                case 5:
                    if (q == 0)
                    {
                        Idle((ushort)((R.I << 8) | R.R), 1);
                        Push(p == 3 ? R.AF : Pair(p, family));
                    }
                    else
                    {
                        R.WZ = ReadWordImmediate();
                        Idle(unchecked((ushort)(R.PC - 1)), 1);
                        Push(R.PC); R.PC = R.WZ;
                    }
                    return;
                case 7:
                    Idle((ushort)((R.I << 8) | R.R), 1);
                    Push(R.PC); R.PC = R.WZ = (ushort)(y * 8);
                    return;
            }
        }
        throw new InvalidOperationException($"Unmapped base decoder key {key}.");
    }
}
