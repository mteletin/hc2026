namespace Hc2000.Z80;

public sealed partial class Z80
{
    private void BeginBaseFrontier()
    {
        if (FX == 2)
        {
            if (FZ == 6) BaseMemoryAddress(5);
            else { Alu(FY, ReadReg(FZ, frontierFamily)); RetireFrontier(); }
            return;
        }
        if (FX == 3 && FZ == 6) { FImmediate(FrontierStage.BaseImmediate); return; }
        if (FX == 0 && FZ is 4 or 5)
        {
            if (FY == 6) BaseMemoryAddress(5);
            else { WriteReg(FY, frontierFamily, IncDec(ReadReg(FY, frontierFamily), FZ == 5)); RetireFrontier(); }
            return;
        }
        if (FX == 0 && FZ == 1)
        {
            if (FQ == 1) FIdle(FIR, 7, FrontierStage.BaseAdd16);
            else FReadWord(FrontierStage.BasePairImmediate);
            return;
        }
        if (FX == 0 && FZ == 3) { FIdle(FIR, 2, FrontierStage.BaseIncPair); return; }
        if (FX == 0 && FZ == 6)
        {
            if (FY == 6) BaseMemoryAddress(0);
            else FImmediate(FrontierStage.BaseImmediate);
            return;
        }
        if (FX == 1)
        {
            if (frontierOpcode == 0x76) { Halted = true; RetireFrontier(); }
            else if (FY == 6 || FZ == 6) BaseMemoryAddress(5);
            else { WriteReg(FY, frontierFamily, ReadReg(FZ, frontierFamily)); RetireFrontier(); }
            return;
        }
        if (frontierOpcode == 0) { RetireFrontier(); return; }
        if (FX == 0 && FZ == 7) { ExecuteAccumulatorMisc(FY, FT[0]); RetireFrontier(); return; }
        if (FX == 0)
        {
            if (FZ == 0)
            {
                if (FY == 1) { (R.AF, R.AFShadow) = (R.AFShadow, R.AF); RetireFrontier(); }
                else if (FY == 2) FIdle(FIR, 1, FrontierStage.BaseRelativeBegin);
                else FImmediate(FrontierStage.BaseRelativeRead);
            }
            else if (FZ == 2)
            {
                if (FP < 2)
                {
                    FAddress = FP == 0 ? R.BC : R.DE;
                    DispatchBaseFrontier(FrontierStage.BaseAddress, 0);
                }
                else FReadWord(FrontierStage.BaseAddress);
            }
            else throw new InvalidOperationException("Unmapped base frontier.");
            return;
        }
        switch (FZ)
        {
            case 0: FIdle(FIR, 1, FrontierStage.BaseReturnCondition); break;
            case 1:
                if (FQ == 0) FPop(FrontierStage.BasePop);
                else switch (FP)
                {
                    case 0: FPop(FrontierStage.BaseReturn); break;
                    case 1:
                        (R.BC, R.BCShadow) = (R.BCShadow, R.BC);
                        (R.DE, R.DEShadow) = (R.DEShadow, R.DE);
                        (R.HL, R.HLShadow) = (R.HLShadow, R.HL);
                        RetireFrontier();
                        break;
                    case 2: R.PC = Pair(2, frontierFamily); RetireFrontier(); break;
                    case 3: FIdle(FIR, 2, FrontierStage.BaseLoadSp); break;
                }
                break;
            case 2: FReadWord(FrontierStage.BaseJump); break;
            case 3:
                switch (FY)
                {
                    case 0: FReadWord(FrontierStage.BaseJump); break;
                    case 2: case 3: FImmediate(FrontierStage.BasePortImmediate); break;
                    case 4: FRead(R.SP, FrontierStage.BaseExchangeLow); break;
                    case 5: (R.DE, R.HL) = (R.HL, R.DE); RetireFrontier(); break;
                    case 6: R.IFF1 = R.IFF2 = false; eiDelay = 0; RetireFrontier(); break;
                    case 7: R.IFF1 = R.IFF2 = true; eiDelay = 2; RetireFrontier(); break;
                    default: throw new InvalidOperationException("Unconsumed prefix.");
                }
                break;
            case 4: FReadWord(FrontierStage.BaseCall); break;
            case 5:
                if (FQ == 0) FIdle(FIR, 1, FrontierStage.BasePush);
                else FReadWord(FrontierStage.BaseCall);
                break;
            case 7: FIdle(FIR, 1, FrontierStage.BaseRestart); break;
            default: throw new InvalidOperationException("Unmapped base frontier.");
        }
    }

    private void BaseMemoryAddress(int delay)
    {
        if (FIndexed) FIndex(delay, FrontierStage.BaseMemory);
        else { FAddress = R.HL; DispatchBaseFrontier(FrontierStage.BaseMemory, 0); }
    }

    private void DispatchBaseFrontier(FrontierStage stage, byte value)
    {
        switch (stage)
        {
            case FrontierStage.BaseMemory:
                if (FX == 0 && FZ is 4 or 5) FRead(FAddress, FrontierStage.BaseIncDecRead);
                else if (FX == 0 && FZ == 6) FImmediate(FrontierStage.BaseImmediate);
                else if (FX == 1 && FY == 6) FWrite(FAddress, ReadReg(FZ, PrefixFamily.Base), FrontierStage.Retire);
                else FRead(FAddress, FrontierStage.BaseMemoryValue);
                break;
            case FrontierStage.BaseMemoryValue:
                if (FX == 2) Alu(FY, value);
                else WriteReg(FY, PrefixFamily.Base, value);
                RetireFrontier();
                break;
            case FrontierStage.BaseIncDecRead: FT[1] = value; FIdle(FAddress, 1, FrontierStage.BaseIncDecIdle); break;
            case FrontierStage.BaseIncDecIdle: FWrite(FAddress, IncDec(FT[1], FZ == 5), FrontierStage.Retire); break;
            case FrontierStage.BaseImmediate:
                if (FX == 3) { Alu(FY, value); RetireFrontier(); }
                else if (FY != 6) { WriteReg(FY, frontierFamily, value); RetireFrontier(); }
                else
                {
                    FT[1] = value;
                    if (FIndexed) FIdle(unchecked((ushort)(R.PC - 1)), 2, FrontierStage.BaseImmediateWrite);
                    else FWrite(FAddress, value, FrontierStage.Retire);
                }
                break;
            case FrontierStage.BaseImmediateWrite: FWrite(FAddress, FT[1], FrontierStage.Retire); break;
            case FrontierStage.BasePairImmediate: SetPair(FP, frontierFamily, FWord); RetireFrontier(); break;
            case FrontierStage.BaseAdd16:
            {
                var a = Pair(2, frontierFamily);
                var b = Pair(FP, frontierFamily);
                var result = a + b;
                R.WZ = unchecked((ushort)(a + 1));
                SetPair(2, frontierFamily, (ushort)result);
                Flags((R.F & (Sign | Zero | Parity)) | ((result >> 8) & (X | Y))
                    | (((a ^ b ^ result) >> 8) & Half) | (result > 65535 ? Carry : 0));
                RetireFrontier();
                break;
            }
            case FrontierStage.BaseIncPair:
                SetPair(FP, frontierFamily, unchecked((ushort)(Pair(FP, frontierFamily) + (FQ == 0 ? 1 : -1))));
                RetireFrontier();
                break;
            case FrontierStage.BaseRelativeBegin: R.B--; FImmediate(FrontierStage.BaseRelativeRead); break;
            case FrontierStage.BaseRelativeRead:
                FT[3] = value;
                if (FY == 2 ? R.B != 0 : FY == 3 || Condition(FY - 4))
                    FIdle(unchecked((ushort)(R.PC - 1)), 5, FrontierStage.BaseRelativeTaken);
                else RetireFrontier();
                break;
            case FrontierStage.BaseRelativeTaken:
                R.PC = R.WZ = unchecked((ushort)(R.PC + (sbyte)FT[3]));
                RetireFrontier();
                break;
            case FrontierStage.BaseAddress:
                if (FP >= 2) FAddress = FWord;
                if (FQ != 0) FRead(FAddress, FrontierStage.BaseAddressLow);
                else if (FP == 2)
                {
                    FOld = Pair(2, frontierFamily);
                    FWrite(FAddress, (byte)FOld, FrontierStage.BaseAddressWriteHigh);
                }
                else FWrite(FAddress, R.A, FrontierStage.BaseAddressDone);
                break;
            case FrontierStage.BaseAddressLow:
                if (FP == 2) { FWord = value; FRead(unchecked((ushort)(FAddress + 1)), FrontierStage.BaseAddressHigh); }
                else { R.A = value; R.WZ = unchecked((ushort)(FAddress + 1)); RetireFrontier(); }
                break;
            case FrontierStage.BaseAddressHigh:
                SetPair(2, frontierFamily, (ushort)(FWord | (value << 8)));
                R.WZ = unchecked((ushort)(FAddress + 1));
                RetireFrontier();
                break;
            case FrontierStage.BaseAddressWriteHigh:
                FWrite(unchecked((ushort)(FAddress + 1)), (byte)(FOld >> 8), FrontierStage.BaseAddressDone);
                break;
            case FrontierStage.BaseAddressDone:
                R.WZ = FP == 2 ? unchecked((ushort)(FAddress + 1)) :
                    (ushort)((R.A << 8) | ((FAddress + 1) & 0xff));
                RetireFrontier();
                break;
            case FrontierStage.BaseReturnCondition:
                if (Condition(FY)) FPop(FrontierStage.BaseReturn);
                else RetireFrontier();
                break;
            case FrontierStage.BaseReturn: R.PC = R.WZ = FWord; RetireFrontier(); break;
            case FrontierStage.BasePop:
                if (FP == 3) R.AF = FWord;
                else SetPair(FP, frontierFamily, FWord);
                RetireFrontier();
                break;
            case FrontierStage.BaseJump:
                R.WZ = FWord;
                if (FZ == 3 || Condition(FY)) R.PC = FWord;
                RetireFrontier();
                break;
            case FrontierStage.BasePortImmediate:
                FT[3] = value;
                if (FY == 2) FOutput((ushort)((R.A << 8) | value), R.A, FrontierStage.BasePortValue);
                else FInput((ushort)((R.A << 8) | value), FrontierStage.BasePortValue);
                break;
            case FrontierStage.BasePortValue:
                FT[1] = value;
                FIdle(FPort, 3, FrontierStage.BasePortDone);
                break;
            case FrontierStage.BasePortDone:
                if (FY == 3) R.A = FT[1];
                R.WZ = FY == 2 ? (ushort)((FPort & 0xff00) | ((FT[3] + 1) & 0xff)) : unchecked((ushort)(FPort + 1));
                RetireFrontier();
                break;
            case FrontierStage.BaseExchangeLow:
                FWord = value;
                FRead(unchecked((ushort)(R.SP + 1)), FrontierStage.BaseExchangeHigh);
                break;
            case FrontierStage.BaseExchangeHigh:
                FWord |= (ushort)(value << 8);
                FOld = Pair(2, frontierFamily);
                FIdle(unchecked((ushort)(R.SP + 1)), 1, FrontierStage.BaseExchangeIdle);
                break;
            case FrontierStage.BaseExchangeIdle:
                FWrite(unchecked((ushort)(R.SP + 1)), (byte)(FOld >> 8), FrontierStage.BaseExchangeWriteHigh);
                break;
            case FrontierStage.BaseExchangeWriteHigh: FWrite(R.SP, (byte)FOld, FrontierStage.BaseExchangeWriteLow); break;
            case FrontierStage.BaseExchangeWriteLow: FIdle(R.SP, 2, FrontierStage.BaseExchangeDone); break;
            case FrontierStage.BaseExchangeDone: SetPair(2, frontierFamily, R.WZ = FWord); RetireFrontier(); break;
            case FrontierStage.BaseCall:
                R.WZ = FWord;
                if (FZ == 5 || Condition(FY))
                {
                    FAddress = FWord;
                    FIdle(unchecked((ushort)(R.PC - 1)), 1, FrontierStage.BaseCallPush);
                }
                else RetireFrontier();
                break;
            case FrontierStage.BaseCallPush: FPush(R.PC, FrontierStage.BaseCallDone); break;
            case FrontierStage.BaseCallDone: R.PC = R.WZ = FAddress; RetireFrontier(); break;
            case FrontierStage.BasePush: FPush(FP == 3 ? R.AF : Pair(FP, frontierFamily), FrontierStage.Retire); break;
            case FrontierStage.BaseRestart: FPush(R.PC, FrontierStage.BaseRestartDone); break;
            case FrontierStage.BaseRestartDone: R.PC = R.WZ = (ushort)(FY * 8); RetireFrontier(); break;
            case FrontierStage.BaseLoadSp: R.SP = Pair(2, frontierFamily); RetireFrontier(); break;
            default: throw new InvalidOperationException("Invalid base continuation.");
        }
    }

    private void DispatchCbFrontier(FrontierStage stage, byte value)
    {
        var indexed = frontierFamily is PrefixFamily.DDCB or PrefixFamily.FDCB;
        var memoryOperand = indexed || FZ == 6;
        switch (stage)
        {
            case FrontierStage.CbBegin:
                if (memoryOperand) FRead(FAddress, FrontierStage.CbRead);
                else { FT[1] = ReadReg(FZ, PrefixFamily.Base); DispatchCbFrontier(FrontierStage.CbIdle, 0); }
                break;
            case FrontierStage.CbRead: FT[1] = value; FIdle(FAddress, 1, FrontierStage.CbIdle); break;
            case FrontierStage.CbIdle:
                if (FX == 1)
                {
                    var set = (FT[1] & (1 << FY)) != 0;
                    var xy = memoryOperand ? R.WZ >> 8 : FT[1];
                    Flags((R.F & Carry) | Half | (set ? 0 : Zero | Parity)
                        | (FY == 7 && set ? Sign : 0) | (xy & (X | Y)));
                    RetireFrontier();
                }
                else
                {
                    FT[1] = FX == 0 ? Rotate(FY, FT[1]) : FX == 2 ?
                        (byte)(FT[1] & ~(1 << FY)) : (byte)(FT[1] | (1 << FY));
                    if (memoryOperand) FWrite(FAddress, FT[1], FrontierStage.CbWritten);
                    else DispatchCbFrontier(FrontierStage.CbWritten, 0);
                }
                break;
            case FrontierStage.CbWritten:
                if (FZ != 6) WriteReg(FZ, PrefixFamily.Base, FT[1]);
                RetireFrontier();
                break;
            default: throw new InvalidOperationException("Invalid CB continuation.");
        }
    }
}
