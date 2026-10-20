namespace Hc2000.Z80;

public sealed partial class Z80
{
    private void BeginEdFrontier()
    {
        if (opcodes[DecodeExecutionKey(PrefixFamily.ED, frontierOpcode)].Pattern == "NOP") { RetireFrontier(); return; }
        if (FX == 1)
        {
            switch (FZ)
            {
                case 0: FInput(R.BC, FrontierStage.EdInput); return;
                case 1: FOutput(R.BC, FY == 6 ? (byte)0 : ReadReg(FY, PrefixFamily.Base), FrontierStage.EdOutputDone); return;
                case 2: FIdle(FIR, 7, FrontierStage.EdAdd16); return;
                case 3: FReadWord(FrontierStage.EdAddress); return;
                case 4:
                    var value = R.A;
                    R.A = 0;
                    Alu(2, value);
                    RetireFrontier();
                    return;
                case 5: FPop(FrontierStage.EdReturn); return;
                case 6: R.IM = (byte)(FY is 2 or 6 ? 1 : FY is 3 or 7 ? 2 : 0); RetireFrontier(); return;
                case 7:
                    if (FY < 4) FIdle(FIR, 1, FrontierStage.EdSpecial);
                    else if (FY is 4 or 5) FRead(R.HL, FrontierStage.EdNibbleRead);
                    else throw new InvalidOperationException("Unmapped ED special frontier.");
                    return;
            }
        }
        if (FX == 2 && FY >= 4 && FZ < 4)
        {
            FRepeatAddress = FZ == 0 ? R.DE : R.HL;
            if (FZ < 2) FRead(R.HL, FrontierStage.BlockRead);
            else FIdle(FIR, 1, FZ == 2 ? FrontierStage.BlockInputBegin : FrontierStage.BlockOutputBegin);
            return;
        }
        throw new InvalidOperationException("Unmapped ED frontier.");
    }

    private void DispatchEdFrontier(FrontierStage stage, byte value)
    {
        switch (stage)
        {
            case FrontierStage.EdInput:
                FT[1] = value;
                FIdle(FPort, 3, FrontierStage.EdInputDone);
                break;
            case FrontierStage.EdInputDone:
                if (FY != 6) WriteReg(FY, PrefixFamily.Base, FT[1]);
                Flags((R.F & Carry) | Sz53P(FT[1]));
                R.WZ = unchecked((ushort)(FPort + 1));
                RetireFrontier();
                break;
            case FrontierStage.EdOutputDone:
                // The port callback is at T+1; its three remaining cycles are a separate frontier.
                FIdle(FPort, 3, FrontierStage.EdAddressDone);
                break;
            case FrontierStage.EdAdd16:
            {
                var a = R.HL;
                var b = Pair(FP, PrefixFamily.Base);
                var c = R.F & Carry;
                var sub = FQ == 0;
                var result = sub ? a - b - c : a + b + c;
                var overflow = sub ? (a ^ b) & (a ^ result) & 0x8000 : ~(a ^ b) & (a ^ result) & 0x8000;
                R.WZ = unchecked((ushort)(a + 1));
                R.HL = (ushort)result;
                Flags(((result >> 8) & (Sign | X | Y)) | (R.HL == 0 ? Zero : 0)
                    | (((a ^ b ^ result) >> 8) & Half) | (overflow != 0 ? Parity : 0)
                    | ((result & 0x10000) != 0 ? Carry : 0) | (sub ? Subtract : 0));
                RetireFrontier();
                break;
            }
            case FrontierStage.EdAddress:
                FAddress = FWord;
                if (FQ == 0)
                {
                    FOld = Pair(FP, PrefixFamily.Base);
                    FWrite(FAddress, (byte)FOld, FrontierStage.EdAddressWriteHigh);
                }
                else FRead(FAddress, FrontierStage.EdAddressLow);
                break;
            case FrontierStage.EdAddressLow:
                FWord = value;
                FRead(unchecked((ushort)(FAddress + 1)), FrontierStage.EdAddressHigh);
                break;
            case FrontierStage.EdAddressHigh:
                SetPair(FP, PrefixFamily.Base, (ushort)(FWord | (value << 8)));
                DispatchEdFrontier(FrontierStage.EdAddressDone, 0);
                break;
            case FrontierStage.EdAddressWriteHigh:
                FWrite(unchecked((ushort)(FAddress + 1)), (byte)(FOld >> 8), FrontierStage.EdAddressDone);
                break;
            case FrontierStage.EdAddressDone:
                R.WZ = unchecked((ushort)((FZ == 1 ? FPort : FAddress) + 1));
                RetireFrontier();
                break;
            case FrontierStage.EdReturn:
                R.PC = R.WZ = FWord;
                R.IFF1 = R.IFF2;
                RetireFrontier();
                break;
            case FrontierStage.EdSpecial:
                if (FY == 0) R.I = R.A;
                else if (FY == 1) R.R = R.A;
                else
                {
                    R.A = FY == 2 ? R.I : R.R;
                    Flags((R.F & Carry) | Sz53(R.A) | (R.IFF2 ? Parity : 0));
                    lastLdAiOrR = true;
                }
                RetireFrontier();
                break;
            case FrontierStage.EdNibbleRead:
                if (FY == 4)
                {
                    FT[1] = (byte)((R.A << 4) | (value >> 4));
                    R.A = (byte)((R.A & 0xf0) | (value & 15));
                }
                else
                {
                    FT[1] = (byte)((value << 4) | (R.A & 15));
                    R.A = (byte)((R.A & 0xf0) | (value >> 4));
                }
                FIdle(R.HL, 4, FrontierStage.EdNibbleIdle);
                break;
            case FrontierStage.EdNibbleIdle: FWrite(R.HL, FT[1], FrontierStage.EdNibbleDone); break;
            case FrontierStage.EdNibbleDone:
                R.WZ = unchecked((ushort)(R.HL + 1));
                Flags((R.F & Carry) | Sz53P(R.A));
                RetireFrontier();
                break;
            default: throw new InvalidOperationException("Invalid ED continuation.");
        }
    }

    private int FDirection => (frontierOpcode & 8) == 0 ? 1 : -1;
    private void DispatchBlockFrontier(FrontierStage stage, byte value)
    {
        var direction = FDirection;
        switch (stage)
        {
            case FrontierStage.BlockRead:
                FT[1] = value;
                if (FZ == 0) FWrite(R.DE, value, FrontierStage.BlockWrite);
                else FIdle(R.HL, 5, FrontierStage.BlockCompareDone);
                break;
            case FrontierStage.BlockWrite: FIdle(R.DE, 2, FrontierStage.BlockTransferDone); break;
            case FrontierStage.BlockTransferDone:
            {
                R.HL = unchecked((ushort)(R.HL + direction));
                R.DE = unchecked((ushort)(R.DE + direction));
                R.BC--;
                var sum = R.A + FT[1];
                Flags((R.F & (Sign | Zero | Carry)) | (R.BC != 0 ? Parity : 0) | (sum & X) | ((sum & 2) << 4));
                FinishBlockFrontier();
                break;
            }
            case FrontierStage.BlockCompareDone:
            {
                var difference = R.A - FT[1];
                var halfBorrow = (R.A & 15) < (FT[1] & 15);
                var adjusted = difference - (halfBorrow ? 1 : 0);
                R.HL = unchecked((ushort)(R.HL + direction));
                R.BC--;
                R.WZ = unchecked((ushort)(R.WZ + direction));
                Flags((R.F & Carry) | Subtract | (difference & Sign) | ((byte)difference == 0 ? Zero : 0)
                    | (halfBorrow ? Half : 0) | (R.BC != 0 ? Parity : 0) | (adjusted & X) | ((adjusted & 2) << 4));
                FinishBlockFrontier();
                break;
            }
            case FrontierStage.BlockInputBegin: FInput(R.BC, FrontierStage.BlockInputValue); break;
            case FrontierStage.BlockInputValue:
                FT[14] = value;
                FIdle(FPort, 3, FrontierStage.BlockInputDone);
                break;
            case FrontierStage.BlockInputDone: FWrite(R.HL, FT[14], FrontierStage.BlockOutputDone); break;
            case FrontierStage.BlockOutputBegin: FRead(R.HL, FrontierStage.BlockOutputRead); break;
            case FrontierStage.BlockOutputRead:
                FT[14] = value;
                R.HL = unchecked((ushort)(R.HL + direction));
                R.B--;
                FOutput(R.BC, value, FrontierStage.BlockOutputDone);
                break;
            case FrontierStage.BlockOutputDone:
                if (FZ == 2)
                {
                    R.HL = unchecked((ushort)(R.HL + direction));
                    R.B--;
                    R.WZ = unchecked((ushort)(FPort + direction));
                    BlockIoFlags(FT[14], FT[14] + ((R.C + direction) & 255));
                    FinishBlockFrontier();
                }
                else FIdle(FPort, 3, FrontierStage.BlockRepeatDone);
                break;
            case FrontierStage.BlockRepeatDone:
                if (FT[23] == 0)
                {
                    R.WZ = unchecked((ushort)(R.BC + direction));
                    BlockIoFlags(FT[14], FT[14] + R.L);
                    FinishBlockFrontier();
                }
                else CompleteBlockRepeat();
                break;
            default: throw new InvalidOperationException("Invalid block continuation.");
        }
    }

    private void FinishBlockFrontier()
    {
        var repeat = (frontierOpcode & 0x10) != 0 &&
            (FZ >= 2 ? R.B != 0 : R.BC != 0 && (FZ != 1 || (R.F & Zero) == 0));
        if (!repeat) { RetireFrontier(); return; }
        FT[23] = 1;
        FIdle(FZ == 3 ? R.BC : FRepeatAddress, 5, FrontierStage.BlockRepeatDone);
    }

    private void CompleteBlockRepeat()
    {
        R.PC = unchecked((ushort)(R.PC - 2));
        var flags = R.F;
        if (FZ >= 2)
        {
            var k = R.B & 7;
            if ((flags & Carry) != 0)
            {
                var negativeData = (FT[14] & 0x80) != 0;
                var half = negativeData ? (R.B & 15) == 0 : (R.B & 15) == 15;
                flags = (byte)((flags & ~Half) | (half ? Half : 0));
                k = (R.B + (negativeData ? -1 : 1)) & 7;
            }
            if ((Sz53P(k) & Parity) == 0) flags ^= Parity;
        }
        R.WZ = unchecked((ushort)(R.PC + 1));
        Flags((flags & ~(X | Y)) | ((R.PC >> 8) & (X | Y)));
        RetireFrontier();
    }
}
