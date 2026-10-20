namespace Hc2000.Z80;

/// <summary>
/// NMOS Z80 state and bus boundary. Full external-suite timing and undocumented
/// behaviour verification are still under construction.
/// </summary>
public sealed partial class Z80
{
    private readonly IBus bus;
    private readonly IM1FetchBus? m1Bus;
    private readonly byte[]? fastMemory;
    private readonly FastTimer fastTimer;
    private readonly bool fastNoContend;
    private bool nmiPending;
    private readonly Opcodes opcodes = new();

    public Registers R;
    public long TStates;
    public bool Halted;
    public bool IntLine;

    public Z80(IBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        this.bus = bus;
        m1Bus = bus as IM1FetchBus;
        var arrayBus = bus as IArrayBus;
        fastMemory = arrayBus?.Memory;
        fastNoContend = arrayBus is { Contends: false };
        fastTimer = new FastTimer();
        Reset();
    }

    /// <summary>Latch an NMI edge until an instruction boundary.</summary>
    public void Nmi() => nmiPending = true;

    /// <summary>
    /// Reset state and the T-state epoch. Unspecified power-on registers are
    /// deterministically zero; AF and SP start at FFFF. Does not access the bus.
    /// </summary>
    public void Reset()
    {
        R = default;
        R.AF = R.SP = 0xffff;
        TStates = 0;
        Halted = false;
        IntLine = false;
        nmiPending = false;
        eiDelay = 0;
        lastLdAiOrR = false;
        frontierActive = frontierPrepared = frontierRetired = frontierOpcodePresent = false;
        frontierImmediate = frontierRefresh = false;
        frontierStage = FrontierStage.None;
        frontierFamily = PrefixFamily.Base;
        frontierOpcode = 0;
        frontierEffect = default;
        if (frontierTemps is not null) Array.Clear(frontierTemps);
    }

    /// <summary>Execute one instruction. Prefixes are not interrupt boundaries.</summary>
    public int Step()
    {
        if (frontierActive) throw new InvalidOperationException("A partial frontier instruction cannot use Step.");
        var start = TStates;
        // Interrupt boundary: NMI takes priority, then INT.
        if ((nmiPending || (IntLine && R.IFF1 && eiDelay == 0)) && AcceptInterrupt())
            return checked((int)(TStates - start));
        lastLdAiOrR = false;
        var previousQ = R.Q;
        R.Q = 0;
        // M1 fetch: exact per-access TStates (IM1FetchBus.FetchM1 observes TStates).
        if (!fastNoContend) bus.Contend(R.PC, 4);
        TStates += 4;
        var memory = fastMemory;
        var opcode = m1Bus is not null && !Halted
            ? m1Bus.FetchM1(R.PC, instructionStart: true, TStates)
            : memory is not null ? memory[R.PC] : bus.Read(R.PC);
        R.R = (byte)((R.R & 0x80) | ((R.R + 1) & 0x7f));
        if (!Halted) R.PC = unchecked((ushort)(R.PC + 1));
        if (Halted)
        {
            if (eiDelay > 0) eiDelay--;
            fastTimer.Flush(ref TStates);
            return checked((int)(TStates - start));
        }
        // Common path: base opcode, no prefix. Inlined to avoid ExecuteInstruction overhead.
        if (opcode is not 0xdd and not 0xfd and not 0xcb and not 0xed)
        {
            // The generated decoder verifies the catalog's M1-only base NOP.
            if (opcode != 0) ExecuteGeneratedBase(opcode, previousQ);
        }
        else
        {
            // Rare path: prefix or CB/ED. Dispatch through ExecuteInstruction.
            ExecutePrefix(opcode, previousQ);
        }
        if (eiDelay > 0) eiDelay--;
        fastTimer.Flush(ref TStates);
        return checked((int)(TStates - start));
    }

    private void ExecutePrefix(byte opcode, byte previousQ)
    {
        var family = PrefixFamily.Base;
        if (opcode is 0xdd or 0xfd)
        {
            previousQ = 0;
            family = opcode == 0xdd ? PrefixFamily.DD : PrefixFamily.FD;
            opcode = Fetch();
        }
        while (opcode is 0xdd or 0xfd)
        {
            previousQ = 0;
            family = opcode == 0xdd ? PrefixFamily.DD : PrefixFamily.FD;
            opcode = Fetch();
        }
        if (opcode == 0xcb)
        {
            if (family == PrefixFamily.Base)
            {
                family = PrefixFamily.CB;
                opcode = Fetch();
                ExecuteCb(DecodeExecutionKey(family, opcode), R.HL);
            }
            else
            {
                var address = IndexedAddress(family, 0);
                family = family == PrefixFamily.DD ? PrefixFamily.DDCB : PrefixFamily.FDCB;
                opcode = ReadImmediate();
                ExecuteCb(DecodeExecutionKey(family, opcode), address);
            }
        }
        else if (opcode == 0xed)
        {
            opcode = Fetch();
            ExecuteEd(DecodeExecutionKey(PrefixFamily.ED, opcode));
        }
        else
        {
            var key = DecodeExecutionKey(family, opcode);
            if (key.Opcode != 0)
            {
                if (family == PrefixFamily.Base) ExecuteGeneratedBase(key.Opcode, previousQ);
                else ExecuteBase(key, previousQ);
            }
        }
    }

}
