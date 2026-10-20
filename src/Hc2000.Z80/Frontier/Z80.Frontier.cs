using Hc2000.Z80.Frontier;

namespace Hc2000.Z80;

public sealed partial class Z80
{
    // This is an explicit, finite continuation, not an instruction log or a suspended CLR stack.
    // Storage is lazy: CPUs using the original Step path allocate no frontier temporary buffer.
    private byte[]? frontierTemps;
    private FrontierEffect frontierEffect;
    private FrontierStage frontierStage;
    private PrefixFamily frontierFamily;
    private byte frontierOpcode;
    private bool frontierActive, frontierPrepared, frontierImmediate, frontierRefresh, frontierRetired, frontierOpcodePresent;

    private enum FrontierStage : ushort
    {
        None, Fetch, CbFetch, EdFetch, IndexedCbAddress, IndexedCbOpcode,
        WordLow, WordHigh, IndexDisplacement, IndexReady, PushHigh, PushLow, PopLow, PopHigh,
        Retire, InterruptBegin, InterruptVectorLow, InterruptVectorHigh, InterruptDone,
        BaseMemory, BaseMemoryValue, BaseIncDecRead, BaseIncDecIdle, BaseImmediate,
        BaseImmediateWrite, BasePairImmediate, BaseAdd16, BaseIncPair, BaseRelativeBegin,
        BaseRelativeRead, BaseRelativeTaken, BaseAddress, BaseAddressLow, BaseAddressHigh,
        BaseAddressWriteHigh, BaseAddressDone, BaseReturnCondition, BaseReturn, BasePop,
        BaseJump, BasePortImmediate, BasePortValue, BasePortDone, BaseExchangeLow,
        BaseExchangeHigh, BaseExchangeIdle, BaseExchangeWriteHigh, BaseExchangeWriteLow,
        BaseExchangeDone, BaseCall, BaseCallPush, BaseCallDone, BasePush, BaseRestart,
        BaseRestartDone, BaseLoadSp, CbBegin, CbRead, CbIdle, CbWritten,
        EdInput, EdInputDone, EdOutputDone, EdAdd16, EdAddress, EdAddressLow,
        EdAddressHigh, EdAddressWriteHigh, EdAddressDone, EdReturn, EdSpecial,
        EdNibbleRead, EdNibbleIdle, EdNibbleDone, BlockRead, BlockWrite, BlockTransferDone,
        BlockCompareDone, BlockInputBegin, BlockInputValue, BlockInputDone, BlockOutputBegin,
        BlockOutputRead, BlockOutputDone, BlockRepeatDone
    }

    private byte[] FT => frontierTemps ??= new byte[32];
    private ushort FAddress { get => TempWord(4); set => SetTempWord(4, value); }
    private ushort FWord { get => TempWord(6); set => SetTempWord(6, value); }
    private ushort FOld { get => TempWord(8); set => SetTempWord(8, value); }
    private ushort FRepeatAddress { get => TempWord(10); set => SetTempWord(10, value); }
    private ushort FPort { get => TempWord(12); set => SetTempWord(12, value); }
    private ushort TempWord(int index) => (ushort)(FT[index] | (FT[index + 1] << 8));
    private void SetTempWord(int index, ushort value) { FT[index] = (byte)value; FT[index + 1] = (byte)(value >> 8); }
    private int FX => frontierOpcode >> 6;
    private int FY => (frontierOpcode >> 3) & 7;
    private int FZ => frontierOpcode & 7;
    private int FP => FY >> 1;
    private int FQ => FY & 1;
    private bool FIndexed => frontierFamily is PrefixFamily.DD or PrefixFamily.FD;
    private ushort FIR => (ushort)((R.I << 8) | R.R);

    public bool AtInstructionBoundary => !frontierActive;

    public FrontierEffect PeekNextFrontier()
    {
        if (!frontierPrepared) BeginFrontier();
        return frontierEffect;
    }

    public FrontierCommit CommitFrontier(byte readValue = 0, bool intLine = false, bool nmiEdge = false)
    {
        if (!frontierPrepared) throw new InvalidOperationException("Peek a frontier before committing its effect.");
        frontierPrepared = false;
        frontierRetired = false;
        IntLine = intLine;
        if (FT[31] != 0)
        {
            FT[31] = 0;
            if (FT[22] != 0)
            {
                Halted = false;
                if (FT[22] == 1) { nmiPending = false; R.IFF2 = R.IFF1; R.IFF1 = false; }
                else
                {
                    R.IFF1 = R.IFF2 = false;
                    if (lastLdAiOrR && NmosLdAirInterruptBug) R.F &= unchecked((byte)~Parity);
                }
            }
            FT[0] = R.Q;
            R.Q = 0;
            lastLdAiOrR = false;
        }
        // An edge sampled at this effect is newer than the NMI being acknowledged.
        if (nmiEdge) nmiPending = true;
        TStates = frontierEffect.AtCpuTState;
        if (frontierRefresh) R.R = (byte)((R.R & 0x80) | ((R.R + 1) & 0x7f));
        if (frontierImmediate) R.PC = unchecked((ushort)(R.PC + 1));
        DispatchFrontier(frontierStage, readValue);
        return new(frontierRetired);
    }

    private void BeginFrontier()
    {
        if (frontierActive) throw new InvalidOperationException("Missing pending frontier.");
        frontierActive = true;
        frontierFamily = PrefixFamily.Base;
        frontierOpcode = 0;
        frontierOpcodePresent = false;
        Array.Clear(FT);
        FT[31] = 1;
        if (nmiPending)
        {
            FT[22] = 1;
            ScheduleFrontier(FrontierEffectKind.Internal, 5, R.PC, FrontierStage.InterruptBegin, refresh: true);
        }
        else if (IntLine && R.IFF1 && eiDelay == 0)
        {
            FT[22] = 2;
            ScheduleFrontier(FrontierEffectKind.InterruptAck, 7, 0, FrontierStage.InterruptBegin, refresh: true);
        }
        else ScheduleFrontier(Halted ? FrontierEffectKind.MemoryRead : FrontierEffectKind.M1Read,
            4, R.PC, Halted ? FrontierStage.Retire : FrontierStage.Fetch,
            immediate: !Halted, refresh: true, instructionStart: !Halted);
    }

    private void ScheduleFrontier(FrontierEffectKind kind, int duration, ushort address, FrontierStage next,
        byte value = 0, bool immediate = false, bool refresh = false, bool instructionStart = false)
    {
        if (duration <= 0) throw new InvalidOperationException("Frontiers must advance CPU time.");
        frontierEffect = new(checked(TStates + duration), kind, address, value, instructionStart);
        frontierStage = next;
        frontierImmediate = immediate;
        frontierRefresh = refresh;
        frontierPrepared = true;
    }
    private void FRead(ushort address, FrontierStage next) => ScheduleFrontier(FrontierEffectKind.MemoryRead, 3, address, next);
    private void FWrite(ushort address, byte value, FrontierStage next) => ScheduleFrontier(FrontierEffectKind.MemoryWrite, 3, address, next, value);
    private void FImmediate(FrontierStage next) => ScheduleFrontier(FrontierEffectKind.MemoryRead, 3, R.PC, next, immediate: true);
    private void FIdle(ushort address, int duration, FrontierStage next) => ScheduleFrontier(FrontierEffectKind.Internal, duration, address, next);
    private void FFetch(FrontierStage next) => ScheduleFrontier(FrontierEffectKind.M1Read, 4, R.PC, next, immediate: true, refresh: true);
    private void FReadWord(FrontierStage next) { SetTempWord(17, (ushort)next); FImmediate(FrontierStage.WordLow); }
    private void FPop(FrontierStage next) { SetTempWord(17, (ushort)next); FRead(R.SP, FrontierStage.PopLow); }
    private void FPush(ushort word, FrontierStage next)
    {
        SetTempWord(17, (ushort)next);
        FOld = word;
        R.SP = unchecked((ushort)(R.SP - 1));
        FWrite(R.SP, (byte)(word >> 8), FrontierStage.PushHigh);
    }
    private void FIndex(int delay, FrontierStage next)
    {
        SetTempWord(19, (ushort)next);
        FT[16] = checked((byte)delay);
        FImmediate(FrontierStage.IndexDisplacement);
    }
    private void FInput(ushort port, FrontierStage next)
    {
        FPort = port;
        ScheduleFrontier(FrontierEffectKind.PortRead, 1, port, next);
    }
    private void FOutput(ushort port, byte value, FrontierStage next)
    {
        FPort = port;
        ScheduleFrontier(FrontierEffectKind.PortWrite, 1, port, next, value);
    }
    private void RetireFrontier()
    {
        if (FT[22] == 0 && eiDelay > 0) eiDelay--;
        frontierRetired = true;
        frontierActive = frontierPrepared = false;
        frontierStage = FrontierStage.None;
    }

    private void DispatchFrontier(FrontierStage stage, byte value = 0)
    {
        switch (stage)
        {
            case FrontierStage.Retire: RetireFrontier(); break;
            case FrontierStage.Fetch:
                if (value is 0xdd or 0xfd)
                {
                    FT[0] = 0;
                    frontierFamily = value == 0xdd ? PrefixFamily.DD : PrefixFamily.FD;
                    FFetch(FrontierStage.Fetch);
                }
                else if (value == 0xcb)
                {
                    if (FIndexed) FIndex(0, FrontierStage.IndexedCbAddress);
                    else { frontierFamily = PrefixFamily.CB; FFetch(FrontierStage.CbFetch); }
                }
                else if (value == 0xed) { frontierFamily = PrefixFamily.ED; FFetch(FrontierStage.EdFetch); }
                else { frontierOpcode = value; frontierOpcodePresent = true; BeginBaseFrontier(); }
                break;
            case FrontierStage.IndexedCbAddress:
                frontierFamily = frontierFamily == PrefixFamily.DD ? PrefixFamily.DDCB : PrefixFamily.FDCB;
                FImmediate(FrontierStage.IndexedCbOpcode);
                break;
            case FrontierStage.IndexedCbOpcode:
                frontierOpcode = value;
                frontierOpcodePresent = true;
                FIdle(unchecked((ushort)(R.PC - 1)), 2, FrontierStage.CbBegin);
                break;
            case FrontierStage.CbFetch:
                frontierOpcode = value;
                frontierOpcodePresent = true;
                FAddress = R.HL;
                DispatchCbFrontier(FrontierStage.CbBegin, 0);
                break;
            case FrontierStage.EdFetch: frontierOpcode = value; frontierOpcodePresent = true; BeginEdFrontier(); break;
            case FrontierStage.WordLow: FWord = value; FImmediate(FrontierStage.WordHigh); break;
            case FrontierStage.WordHigh:
                FWord |= (ushort)(value << 8);
                DispatchFrontier((FrontierStage)TempWord(17));
                break;
            case FrontierStage.IndexDisplacement:
                FAddress = unchecked((ushort)(Index(frontierFamily) + (sbyte)value));
                R.WZ = FAddress;
                if (FT[16] != 0) FIdle(unchecked((ushort)(R.PC - 1)), FT[16], FrontierStage.IndexReady);
                else DispatchFrontier((FrontierStage)TempWord(19));
                break;
            case FrontierStage.IndexReady: DispatchFrontier((FrontierStage)TempWord(19)); break;
            case FrontierStage.PushHigh:
                R.SP = unchecked((ushort)(R.SP - 1));
                FWrite(R.SP, (byte)FOld, FrontierStage.PushLow);
                break;
            case FrontierStage.PushLow: DispatchFrontier((FrontierStage)TempWord(17)); break;
            case FrontierStage.PopLow:
                FWord = value;
                R.SP = unchecked((ushort)(R.SP + 1));
                FRead(R.SP, FrontierStage.PopHigh);
                break;
            case FrontierStage.PopHigh:
                FWord |= (ushort)(value << 8);
                R.SP = unchecked((ushort)(R.SP + 1));
                DispatchFrontier((FrontierStage)TempWord(17));
                break;
            case FrontierStage.InterruptBegin:
                if (FT[22] == 1) { FPush(R.PC, FrontierStage.InterruptDone); break; }
                FT[15] = value;
                if (R.IM == 0 && (value & 0xc7) != 0xc7)
                    throw new NotSupportedException("IM 0 supports RST bus instructions only.");
                FPush(R.PC, FrontierStage.InterruptVectorLow);
                break;
            case FrontierStage.InterruptVectorLow:
                if (R.IM == 2)
                {
                    FAddress = (ushort)((R.I << 8) | FT[15]);
                    FRead(FAddress, FrontierStage.InterruptVectorHigh);
                }
                else { R.PC = R.IM == 0 ? (ushort)(FT[15] & 0x38) : (ushort)0x38; R.WZ = R.PC; RetireFrontier(); }
                break;
            case FrontierStage.InterruptVectorHigh:
                FT[1] = value;
                FRead(unchecked((ushort)(FAddress + 1)), FrontierStage.InterruptDone);
                break;
            case FrontierStage.InterruptDone:
                R.PC = R.WZ = FT[22] == 1 ? (ushort)0x66 : (ushort)(FT[1] | (value << 8));
                RetireFrontier();
                break;
            default:
                if (stage >= FrontierStage.BlockRead) DispatchBlockFrontier(stage, value);
                else if (stage >= FrontierStage.EdInput) DispatchEdFrontier(stage, value);
                else if (stage >= FrontierStage.CbBegin) DispatchCbFrontier(stage, value);
                else DispatchBaseFrontier(stage, value);
                break;
        }
    }
}
