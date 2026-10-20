using Hc2000.Z80.Frontier;

namespace Hc2000.Z80;

public sealed partial class Z80
{
    public const string FrontierModelId = "nmos-z80-frontier";
    public const int FrontierModelVersion = 1;

    public FrontierState CaptureFrontier()
    {
        var active = frontierActive;
        var state = new FrontierState(FrontierModelVersion, R, TStates, Halted, IntLine,
            nmiPending, eiDelay, lastLdAiOrR, NmosLdAirInterruptBug,
            active ? frontierFamily : PrefixFamily.Base, active && frontierOpcodePresent,
            active ? frontierOpcode : (byte)0, active ? (ushort)frontierStage : (ushort)0,
            active ? (byte)32 : (byte)0, active ? (byte[])FT.Clone() : [],
            active ? frontierEffect : null, active && frontierImmediate, active && frontierRefresh);
        if (active)
        {
            var mask = LegalFrontierRule(state).Mask;
            for (var i = 0; i < 32; i++) if ((mask & (1u << i)) == 0) state.Temporaries[i] = 0;
        }
        ValidateFrontierState(state);
        return state;
    }

    public void RestoreFrontier(FrontierState state)
    {
        ValidateFrontierState(state);
        // Validation and the only allocation precede every live assignment.
        var temps = state.Pending.HasValue ? (byte[])state.Temporaries.Clone() : null;
        R = state.Registers;
        TStates = state.TStates;
        Halted = state.Halted;
        IntLine = state.IntLine;
        nmiPending = state.NmiPending;
        eiDelay = state.EiDelay;
        lastLdAiOrR = state.LdAirPending;
        NmosLdAirInterruptBug = state.InterruptBug;
        frontierFamily = state.Family;
        frontierOpcodePresent = state.OpcodePresent;
        frontierOpcode = state.Opcode;
        frontierStage = (FrontierStage)state.Stage;
        frontierTemps = temps;
        frontierEffect = state.Pending.GetValueOrDefault();
        frontierActive = frontierPrepared = state.Pending.HasValue;
        frontierImmediate = state.Immediate;
        frontierRefresh = state.Refresh;
        frontierRetired = false;
    }

    public static void ValidateFrontierState(FrontierState state)
    {
        if (state is null || state.Version != FrontierModelVersion || state.TStates < 0 ||
            state.Registers.IM > 2 || state.EiDelay is < 0 or > 2 || state.Temporaries is null)
            throw InvalidFrontier();
        if (!state.Pending.HasValue)
        {
            if (state.Family != PrefixFamily.Base || state.OpcodePresent || state.Opcode != 0 ||
                state.Stage != 0 || state.TemporaryCount != 0 || state.Temporaries.Length != 0 ||
                state.Immediate || state.Refresh) throw InvalidFrontier();
            return;
        }
        if (state.TemporaryCount != 32 || state.Temporaries.Length != 32) throw InvalidFrontier();
        var rule = LegalFrontierRule(state);
        var pending = state.Pending.Value;
        if (pending.AtCpuTState <= state.TStates || pending.AtCpuTState - state.TStates != rule.Cycles ||
            pending.Kind != rule.Kind || state.Immediate != rule.Immediate || state.Refresh != rule.Refresh ||
            pending.InstructionStart != ((FrontierStage)state.Stage == FrontierStage.Fetch && state.Temporaries[31] == 1) ||
            (pending.Kind is not (FrontierEffectKind.MemoryWrite or FrontierEffectKind.PortWrite) && pending.WriteValue != 0))
            throw InvalidFrontier();
        for (var i = 0; i < 32; i++)
            if ((rule.Mask & (1u << i)) == 0 && state.Temporaries[i] != 0) throw InvalidFrontier();
        if ((rule.Immediate && pending.Address != state.Registers.PC) ||
            (pending.Kind == FrontierEffectKind.InterruptAck && pending.Address != 0) ||
            ((FrontierStage)state.Stage is FrontierStage.PushHigh or FrontierStage.PushLow or FrontierStage.PopLow or FrontierStage.PopHigh &&
             pending.Address != state.Registers.SP)) throw InvalidFrontier();
    }

    private static ArgumentException InvalidFrontier() => new("Illegal nmos-z80-frontier v1 continuation.", "state");
    private readonly record struct FrontierRule(uint Mask, FrontierEffectKind Kind, int Cycles, bool Immediate = false, bool Refresh = false);
    private static FrontierRule RequireFrontier(bool legal, FrontierRule rule) => legal ? rule : throw InvalidFrontier();

    // Model-v1 static opcode/family/stage/temporary-mask table. Zero-time dispatch labels are
    // intentionally absent: a saved continuation must have one actual future effect, not a replay PC.
    private static FrontierRule LegalFrontierRule(FrontierState s)
    {
        if (s.Temporaries.Length != 32) throw InvalidFrontier();
        const uint Data = 1u << 1, Displacement = 1u << 3, Address = 3u << 4, Word = 3u << 6,
            Old = 3u << 8, RepeatAddress = 3u << 10, Port = 3u << 12, Io = 1u << 14,
            Vector = 1u << 15, IndexDelay = 1u << 16, Return = 3u << 17, IndexReturn = 3u << 19,
            Interrupt = 1u << 22, Repeat = 1u << 23, Prologue = 1u << 31;
        var t = s.Temporaries;
        var stage = (FrontierStage)s.Stage;
        var op = s.Opcode;
        var x = op >> 6; var y = (op >> 3) & 7; var z = op & 7; var p = y >> 1; var q = y & 1;
        var baseFamily = s.Family is PrefixFamily.Base or PrefixFamily.DD or PrefixFamily.FD;
        var b = baseFamily && s.OpcodePresent;
        var ed = s.Family == PrefixFamily.ED && s.OpcodePresent;
        var cb = s.Family is PrefixFamily.CB or PrefixFamily.DDCB or PrefixFamily.FDCB && s.OpcodePresent;
        var indexed = s.Family is PrefixFamily.DD or PrefixFamily.FD;
        var block = ed && x == 2 && y >= 4 && z < 4;
        var memory = (x == 0 && y == 6 && z is 4 or 5 or 6) ||
            (x == 1 && op != 0x76 && (y == 6 || z == 6)) || (x == 2 && z == 6);
        var call = b && x == 3 && (z == 4 || op == 0xcd);
        var ret = b && x == 3 && (z == 0 || op == 0xc9);
        var pop = b && x == 3 && z == 1 && q == 0;
        var push = b && x == 3 && z == 5 && q == 0;
        var relative = b && x == 0 && z == 0 && y >= 2;
        var absolute = b && x == 0 && z == 2;
        var exchange = b && op == 0xe3;
        var port = b && op is 0xd3 or 0xdb;
        var cbMemory = cb && (s.Family != PrefixFamily.CB || z == 6);
        var returnStage = (FrontierStage)(t[17] | (t[18] << 8));
        var indexStage = (FrontierStage)(t[19] | (t[20] << 8));
        var interrupt = t[22];
        if (interrupt > 2 || t[31] > 1 || t[23] > 1 ||
            (!s.OpcodePresent && op != 0) ||
            (b && op is 0xcb or 0xdd or 0xed or 0xfd) ||
            (interrupt != 0 && (s.Family != PrefixFamily.Base || s.OpcodePresent)) ||
            (t[31] != 0 && stage is not (FrontierStage.Fetch or FrontierStage.InterruptBegin or FrontierStage.Retire)) ||
            (s.Halted && !(stage is FrontierStage.Retire or FrontierStage.InterruptBegin && t[31] == 1))) throw InvalidFrontier();
        var common = interrupt == 0 ? 0u : Interrupt;
        if (stage is FrontierStage.Fetch or FrontierStage.InterruptBegin or FrontierStage.Retire) common |= Prologue;
        FrontierRule Read(uint mask = 0) => new(mask | common, FrontierEffectKind.MemoryRead, 3);
        FrontierRule Write(uint mask = 0) => new(mask | common, FrontierEffectKind.MemoryWrite, 3);
        FrontierRule Idle(int cycles, uint mask = 0) => new(mask | common, FrontierEffectKind.Internal, cycles);
        FrontierRule Immediate(uint mask = 0) => new(mask | common, FrontierEffectKind.MemoryRead, 3, Immediate: true);
        FrontierRule M1(uint mask = 0) => new(mask | common, FrontierEffectKind.M1Read, 4, Immediate: true, Refresh: true);
        FrontierRule Input(uint mask = 0) => new(mask | common, FrontierEffectKind.PortRead, 1);
        FrontierRule Output(uint mask = 0) => new(mask | common, FrontierEffectKind.PortWrite, 1);
        switch (stage)
        {
            case FrontierStage.Fetch:
                return RequireFrontier(baseFamily && !s.OpcodePresent && interrupt == 0 && !s.Halted &&
                    (t[31] == 0 || s.Family == PrefixFamily.Base), M1(1));
            case FrontierStage.CbFetch: return RequireFrontier(s.Family == PrefixFamily.CB && !s.OpcodePresent, M1());
            case FrontierStage.EdFetch: return RequireFrontier(s.Family == PrefixFamily.ED && !s.OpcodePresent, M1());
            case FrontierStage.IndexedCbOpcode:
                return RequireFrontier(s.Family is PrefixFamily.DDCB or PrefixFamily.FDCB && !s.OpcodePresent, Immediate(Address));
            case FrontierStage.WordLow:
            case FrontierStage.WordHigh:
            {
                var legal = returnStage switch
                {
                    FrontierStage.BasePairImmediate => b && x == 0 && z == 1 && q == 0,
                    FrontierStage.BaseAddress => absolute && p >= 2,
                    FrontierStage.BaseJump => b && x == 3 && (z == 2 || op == 0xc3),
                    FrontierStage.BaseCall => call,
                    FrontierStage.EdAddress => ed && x == 1 && z == 3,
                    _ => false
                };
                return RequireFrontier(legal, Immediate(Return | (stage == FrontierStage.WordHigh ? 1u << 6 : 0)));
            }
            case FrontierStage.IndexDisplacement:
            case FrontierStage.IndexReady:
            {
                var cbAddress = indexStage == FrontierStage.IndexedCbAddress && !s.OpcodePresent;
                var legal = indexed && (cbAddress || (indexStage == FrontierStage.BaseMemory && b && memory));
                var delay = cbAddress || (x == 0 && z == 6) ? 0 : 5;
                if (stage == FrontierStage.IndexDisplacement)
                    return RequireFrontier(legal && t[16] == delay, Immediate(IndexDelay | IndexReturn));
                return RequireFrontier(legal && !cbAddress && delay == 5, Idle(5, Address | IndexReturn));
            }
            case FrontierStage.PushHigh:
            case FrontierStage.PushLow:
            {
                var legal = interrupt switch
                {
                    1 => returnStage == FrontierStage.InterruptDone,
                    2 => returnStage == FrontierStage.InterruptVectorLow,
                    _ => (returnStage == FrontierStage.BaseCallDone && call) ||
                        (returnStage == FrontierStage.BaseRestartDone && b && x == 3 && z == 7) ||
                        (returnStage == FrontierStage.Retire && push)
                };
                var mask = Return | (stage == FrontierStage.PushHigh ? Old : 0) |
                    (interrupt == 2 ? Vector : 0) | (returnStage == FrontierStage.BaseCallDone ? Address : 0);
                return RequireFrontier(legal, Write(mask));
            }
            case FrontierStage.PopLow:
            case FrontierStage.PopHigh:
                return RequireFrontier((returnStage == FrontierStage.BaseReturn && ret) ||
                    (returnStage == FrontierStage.BasePop && pop) ||
                    (returnStage == FrontierStage.EdReturn && ed && x == 1 && z == 5),
                    Read(Return | (stage == FrontierStage.PopHigh ? 1u << 6 : 0)));
            case FrontierStage.Retire:
                if (s.Halted) return RequireFrontier(!s.OpcodePresent && s.Family == PrefixFamily.Base && interrupt == 0 && t[31] == 1,
                    new(common, FrontierEffectKind.MemoryRead, 4, Refresh: true));
                return RequireFrontier(b && t[31] == 0 && interrupt == 0 &&
                    ((x == 1 && y == 6 && z != 6) || (x == 0 && y == 6 && z is 4 or 5 or 6)), Write());
            case FrontierStage.InterruptBegin:
                return RequireFrontier(interrupt != 0 && t[31] == 1,
                    new(common, interrupt == 1 ? FrontierEffectKind.Internal : FrontierEffectKind.InterruptAck,
                        interrupt == 1 ? 5 : 7, Refresh: true));
            case FrontierStage.InterruptVectorHigh: return RequireFrontier(interrupt == 2 && s.Registers.IM == 2, Read(Address));
            case FrontierStage.InterruptDone: return RequireFrontier(interrupt == 2 && s.Registers.IM == 2, Read(Data));
            case FrontierStage.BaseMemoryValue:
                return RequireFrontier(b && ((x == 1 && z == 6 && y != 6) || (x == 2 && z == 6)), Read());
            case FrontierStage.BaseIncDecRead: return RequireFrontier(b && x == 0 && y == 6 && z is 4 or 5, Read(Address));
            case FrontierStage.BaseIncDecIdle: return RequireFrontier(b && x == 0 && y == 6 && z is 4 or 5, Idle(1, Address | Data));
            case FrontierStage.BaseImmediate: return RequireFrontier(b && z == 6 && x is 0 or 3, Immediate(x == 0 && y == 6 ? Address : 0));
            case FrontierStage.BaseImmediateWrite: return RequireFrontier(b && indexed && op == 0x36, Idle(2, Address | Data));
            case FrontierStage.BaseAdd16: return RequireFrontier(b && x == 0 && z == 1 && q == 1, Idle(7));
            case FrontierStage.BaseIncPair: return RequireFrontier(b && x == 0 && z == 3, Idle(2));
            case FrontierStage.BaseRelativeBegin: return RequireFrontier(b && op == 0x10, Idle(1));
            case FrontierStage.BaseRelativeRead: return RequireFrontier(relative, Immediate());
            case FrontierStage.BaseRelativeTaken: return RequireFrontier(relative, Idle(5, Displacement));
            case FrontierStage.BaseAddressLow: return RequireFrontier(absolute && q == 1, Read(Address));
            case FrontierStage.BaseAddressHigh: return RequireFrontier(absolute && p == 2 && q == 1, Read(Address | (1u << 6)));
            case FrontierStage.BaseAddressWriteHigh: return RequireFrontier(absolute && p == 2 && q == 0, Write(Address | Old));
            case FrontierStage.BaseAddressDone: return RequireFrontier(absolute && q == 0, Write(Address));
            case FrontierStage.BaseReturnCondition: return RequireFrontier(b && x == 3 && z == 0, Idle(1));
            case FrontierStage.BasePortImmediate: return RequireFrontier(port, Immediate());
            case FrontierStage.BasePortValue: return RequireFrontier(port, y == 2 ? Output(Port | Displacement) : Input(Port | Displacement));
            case FrontierStage.BasePortDone: return RequireFrontier(port, Idle(3, Port | Data | Displacement));
            case FrontierStage.BaseExchangeLow: return RequireFrontier(exchange, Read());
            case FrontierStage.BaseExchangeHigh: return RequireFrontier(exchange, Read(1u << 6));
            case FrontierStage.BaseExchangeIdle: return RequireFrontier(exchange, Idle(1, Word | Old));
            case FrontierStage.BaseExchangeWriteHigh: return RequireFrontier(exchange, Write(Word | Old));
            case FrontierStage.BaseExchangeWriteLow: return RequireFrontier(exchange, Write(Word));
            case FrontierStage.BaseExchangeDone: return RequireFrontier(exchange, Idle(2, Word));
            case FrontierStage.BaseCallPush: return RequireFrontier(call, Idle(1, Address));
            case FrontierStage.BasePush: return RequireFrontier(push, Idle(1));
            case FrontierStage.BaseRestart: return RequireFrontier(b && x == 3 && z == 7, Idle(1));
            case FrontierStage.BaseLoadSp: return RequireFrontier(b && op == 0xf9, Idle(2));
            case FrontierStage.CbBegin: return RequireFrontier(cb && s.Family != PrefixFamily.CB, Idle(2, Address));
            case FrontierStage.CbRead: return RequireFrontier(cbMemory, Read(Address));
            case FrontierStage.CbIdle: return RequireFrontier(cbMemory, Idle(1, Address | Data));
            case FrontierStage.CbWritten: return RequireFrontier(cbMemory && x != 1, Write(Data));
            case FrontierStage.EdInput: return RequireFrontier(ed && x == 1 && z == 0, Input(Port));
            case FrontierStage.EdInputDone: return RequireFrontier(ed && x == 1 && z == 0, Idle(3, Port | Data));
            case FrontierStage.EdOutputDone: return RequireFrontier(ed && x == 1 && z == 1, Output(Port));
            case FrontierStage.EdAdd16: return RequireFrontier(ed && x == 1 && z == 2, Idle(7));
            case FrontierStage.EdAddressLow: return RequireFrontier(ed && x == 1 && z == 3 && q == 1, Read(Address));
            case FrontierStage.EdAddressHigh: return RequireFrontier(ed && x == 1 && z == 3 && q == 1, Read(Address | (1u << 6)));
            case FrontierStage.EdAddressWriteHigh: return RequireFrontier(ed && x == 1 && z == 3 && q == 0, Write(Address | Old));
            case FrontierStage.EdAddressDone:
                return RequireFrontier(ed && x == 1 && z is 1 or 3, z == 1 ? Idle(3, Port) : Write(Address));
            case FrontierStage.EdSpecial: return RequireFrontier(ed && x == 1 && z == 7 && y < 4, Idle(1));
            case FrontierStage.EdNibbleRead: return RequireFrontier(ed && x == 1 && z == 7 && y is 4 or 5, Read());
            case FrontierStage.EdNibbleIdle: return RequireFrontier(ed && x == 1 && z == 7 && y is 4 or 5, Idle(4, Data));
            case FrontierStage.EdNibbleDone: return RequireFrontier(ed && x == 1 && z == 7 && y is 4 or 5, Write());
            case FrontierStage.BlockRead: return RequireFrontier(block && z < 2, Read(RepeatAddress));
            case FrontierStage.BlockWrite: return RequireFrontier(block && z == 0, Write(RepeatAddress | Data));
            case FrontierStage.BlockTransferDone: return RequireFrontier(block && z == 0, Idle(2, RepeatAddress | Data));
            case FrontierStage.BlockCompareDone: return RequireFrontier(block && z == 1, Idle(5, RepeatAddress | Data));
            case FrontierStage.BlockInputBegin: return RequireFrontier(block && z == 2, Idle(1, RepeatAddress));
            case FrontierStage.BlockInputValue: return RequireFrontier(block && z == 2, Input(RepeatAddress | Port));
            case FrontierStage.BlockInputDone: return RequireFrontier(block && z == 2, Idle(3, RepeatAddress | Port | Io));
            case FrontierStage.BlockOutputBegin: return RequireFrontier(block && z == 3, Idle(1, RepeatAddress));
            case FrontierStage.BlockOutputRead: return RequireFrontier(block && z == 3, Read(RepeatAddress));
            case FrontierStage.BlockOutputDone:
                return RequireFrontier(block && z >= 2, z == 2 ? Write(RepeatAddress | Port | Io) : Output(RepeatAddress | Port | Io));
            case FrontierStage.BlockRepeatDone:
                return RequireFrontier(block && (t[23] == 1 ? y >= 6 : z == 3),
                    t[23] == 1 ? Idle(5, Io | Repeat) : Idle(3, RepeatAddress | Io));
            default: throw InvalidFrontier();
        }
    }
}
