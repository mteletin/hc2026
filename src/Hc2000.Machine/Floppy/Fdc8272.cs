using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Floppy;

public enum FdcPhase { Reset, Command, Execution, Result }

/// <summary>Polled non-DMA 8272. RDY/DACK are tied high; INT/DRQ do not reach the CPU. No host I/O or memory access.</summary>
public sealed class Fdc8272
{
    private const int SearchDone = 1, ByteDeadline = 2, NoData = 3, IdDone = 4, Unload = 5, FormatStart = 6, SectorEnd = 7;
    private readonly DeviceScheduler scheduler;
    private readonly int deviceId;
    private readonly Func<int, FloppyDrive?> selectedDrive;
    private readonly FdcTiming timing;
    private readonly bool instant;
    private readonly List<byte> command = new(9);
    private readonly Queue<byte> result = new();
    private readonly Queue<(byte Status, byte Cylinder)> interrupts = new();
    private byte[] pcn = new byte[4];
    private readonly bool[] seeking = new bool[4];
    private readonly int[] seekTargets = new int[4], seekSteps = new int[4], seekHeads = new int[4];
    private readonly long[] unloadTokens = new long[4];
    private readonly FloppyDrive?[] loadedDrives = new FloppyDrive?[4];
    private readonly RationalClock?[] seekClocks = new RationalClock?[4];
    private byte specify1 = 0xdf, specify2 = 0xff;
    private int opcode, unit, side, sectorIndex, byteIndex, transferLength, trackCount;
    private byte c, h, r, n, eot, st1, st2;
    private bool byteReady, terminal, headLoadedAtStart;
    private FloppyDrive? activeDrive;
    private DiskSector? sector;
    private RationalClock? byteClock;
    private bool scanEqual, scanSatisfied;
    private int diagReads, diagStatusPolls, diagReadReturns, diagBrSet, diagBrClear;
    private readonly System.Text.StringBuilder diagLog = new();
    public string DiagLog => diagLog.ToString();
    private byte diagLatch;
    private bool diagMotorTimer;
    /// <summary>The FloppyController stamps every latch write here so FDC command lines can be correlated with the motor/selection state.</summary>
    internal void LogLatch(byte value) { diagLatch = value; diagMotorTimer = true; diagLog.AppendLine($"LATCH@{scheduler.Now:X6} v={value:X2}"); }
    public int DiagBrSet => diagBrSet;
    public int DiagBrClear => diagBrClear;
    private readonly List<byte> formatIds = new();
    private readonly List<(byte C, byte H, byte R, byte N)> formattedIds = new();
    private byte[] lastCommand = [];
    private byte? lastSt0, lastSt1, lastSt2, lastSt3;
    public FdcPhase Phase { get; private set; } = FdcPhase.Reset;
    public bool NonDma => true; // physical HC2000 wiring, even if software clears ND
    public byte Specify1 => specify1;
    public byte Specify2 => specify2;
    public IReadOnlyList<byte> PresentCylinders => pcn.ToArray();
    public bool HeadLoaded => activeDrive?.HeadLoaded == true;
    /// <summary>Diagnostic only: whether the currently selected drive's motor is on (last transfer's drive).</summary>
    public bool DiagActiveDriveMotorOn => activeDrive?.MotorOn == true;
    public int DiagReads => diagReads;
    public int DiagStatusPolls => diagStatusPolls;
    public int DiagReadReturns => diagReadReturns;
    public bool DiagByteReady => byteReady;
    public int DiagByteIndex => byteIndex;
    public int DiagTransferLength => transferLength;

    /// <summary>Owner-thread diagnostic copy. No port reads, queue consumption or time advancement.
    /// Status fields are last reported results, not invented hardware register latches.</summary>
    public FdcDiagnosticSnapshot Inspect() => new(Phase, StatusValue(),
        Array.AsReadOnly(command.ToArray()), Array.AsReadOnly((byte[])lastCommand.Clone()),
        lastSt0, lastSt1, lastSt2, lastSt3, Array.AsReadOnly((byte[])pcn.Clone()),
        Array.AsReadOnly(interrupts.ToArray()));

    internal Fdc8272(DeviceScheduler scheduler, int deviceId, Func<int, FloppyDrive?> selectedDrive, FdcTiming timing, bool instant)
    {
        this.scheduler = scheduler; this.deviceId = deviceId; this.selectedDrive = selectedDrive; this.timing = timing; this.instant = instant;
        scheduler.RegisterDevice(deviceId, Dispatch);
        for (var i = 0; i < 4; i++)
        {
            var drive = i;
            scheduler.RegisterDevice(checked(deviceId + 2 + i), _ => SeekStep(drive));
        }
    }

    public byte ReadStatus()
    {
        // The instant upgrade has no byte deadline. Advance only on a programmed MSR
        // poll, retaining the final-byte/TC window used by the unmodified ROM.
        if (instant && Phase == FdcPhase.Execution && !byteReady && (sector is not null || opcode == 13))
            AdvanceInstantBytes();
        diagStatusPolls++;
        return StatusValue();
    }

    private byte StatusValue()
    {
        if (Phase == FdcPhase.Reset) return 0;
        var value = Phase switch
        {
            FdcPhase.Command => 0x80 | (command.Count == 0 ? 0 : 0x10),
            FdcPhase.Result => 0xd0,
            _ => 0x30 | (byteReady ? 0x80 : 0) | (ReadsToCpu ? 0x40 : 0)
        };
        for (var i = 0; i < 4; i++) if (seeking[i]) value |= 1 << i;
        return (byte)value;
    }

    private bool ReadsToCpu => opcode is 2 or 6 or 10 or 12;
    private bool WritesMedia => opcode is 5 or 9;
    private bool Scanning => opcode is 17 or 25 or 29;

    public byte ReadData()
    {
        if (Phase == FdcPhase.Result)
        {
            var value = result.Dequeue();
            diagLog.AppendLine($"RRES@{scheduler.Now:X6} op={opcode} val={value:X2} remain={result.Count}");
            if (result.Count == 0) Phase = FdcPhase.Command;
            return value;
        }
        // The instant upgrade has no byte deadline. Advance on a data read as well as a
        // programmed MSR poll so a shim that reads data without polling status still works.
        if (instant && Phase == FdcPhase.Execution && !byteReady && (sector is not null || opcode == 13))
            AdvanceInstantBytes();
        if (Phase != FdcPhase.Execution || !byteReady || !ReadsToCpu || sector is null) { diagReads++; diagLog.AppendLine($"R@{scheduler.Now:X6} bidx={byteIndex} br={byteReady} ph={Phase} -> 0xff"); return 0xff; }
        var data = byteIndex < sector.Data.Length ? sector.Data[byteIndex] : (byte)0;
        byteIndex++; byteReady = false; diagReads++; diagReadReturns++; diagBrClear++;
        diagLog.AppendLine($"R@{scheduler.Now:X6} bidx={byteIndex-1} br={byteReady} -> {data:X2}");
        return data;
    }

    public void WriteData(byte value)
    {
        if (Phase == FdcPhase.Command)
        {
            command.Add(value);
            if (command.Count == CommandLength(command[0] & 0x1f)) Execute();
        }
        else if (Phase == FdcPhase.Execution && byteReady && !ReadsToCpu)
        {
            if (activeDrive?.WriteProtected == true && (WritesMedia || opcode == 13)) { Finish(2, 0); return; }
            Consume(value);
            byteReady = false;
        }
        // Writes with wrong DIO/RQM and accesses while held in reset have no effect.
    }

    private static int CommandLength(int op) => op switch
    {
        3 or 15 => 3, 4 or 7 or 10 => 2, 13 => 6,
        1 or 2 or 5 or 6 or 9 or 12 or 17 or 25 or 29 => 9, _ => 1
    };

    public void SetReset(bool asserted)
    {
        if (asserted)
        {
            if (Phase == FdcPhase.Reset) return;
            scheduler.InvalidateDevice(deviceId);
            for (var i = 0; i < 4; i++)
            {
                scheduler.InvalidateDevice(deviceId + 2 + i);
                seeking[i] = false; pcn[i] = 0;
                if (loadedDrives[i] is { } drive) drive.HeadLoaded = false;
                loadedDrives[i] = null;
            }
            if (activeDrive is not null) activeDrive.HeadLoaded = false;
            command.Clear(); result.Clear(); interrupts.Clear();
            lastCommand = []; lastSt0 = lastSt1 = lastSt2 = lastSt3 = null;
            sector = null; activeDrive = null; byteReady = terminal = false;
            specify1 = 0xdf; specify2 = 0xff;
            Phase = FdcPhase.Reset;
        }
        else if (Phase == FdcPhase.Reset)
        {
            Phase = FdcPhase.Command;
            for (var i = 0; i < 4; i++) interrupts.Enqueue(((byte)(0xc0 | i), 0));
        }
    }

    public void TerminalCount()
    {
        if (Phase != FdcPhase.Execution || opcode == 10) return;
        if (activeDrive?.WriteProtected == true && (WritesMedia || opcode == 13)) { Finish(2, 0); return; }
        if (Scanning)
        {
            // SCAN is the datasheet exception: TC terminates after the current compared byte.
            Finish(st1, (byte)(st2 | (scanSatisfied ? (scanEqual ? 8 : 0) : 4)));
            return;
        }
        terminal = true;
        if (byteReady)
        {
            if (WritesMedia) Consume(0); // finish the current field with zeroes, per datasheet
            else byteIndex++;
            byteReady = false;
        }
    }

    private void Execute()
    {
        lastCommand = command.ToArray();
        opcode = command[0] & 0x1f;
        diagLog.AppendLine($"CMD@{scheduler.Now:X6} op={opcode} cmd=[{string.Join(",", lastCommand.Select(x => x.ToString("X2")))}]");
        switch (opcode)
        {
            case 3:
                specify1 = command[1]; specify2 = command[2]; command.Clear(); return;
            case 4:
                unit = command[1] & 3; side = (command[1] >> 2) & 1;
                var sensed = selectedDrive(unit);
                // RDY is tied high, not media-presence. Track zero/WP/two-side are drive signals.
                SetResult((byte)(unit | (side << 2) | 0x20 | (sensed?.HeadPosition == 0 ? 0x10 : 0) |
                    (sensed?.WriteProtected == true ? 0x40 : 0) | (sensed?.Profile.Sides == 2 ? 8 : 0)));
                return;
            case 8:
                if (interrupts.TryDequeue(out var interrupt)) SetResult(interrupt.Status, interrupt.Cylinder);
                else SetResult(0x80);
                return;
            case 7:
            case 15:
                StartSeek(); command.Clear(); return;
            case 1: case 2: case 5: case 6: case 9: case 10: case 12: case 13: case 17: case 25: case 29:
                BeginTransfer(); return;
            default:
                SetResult(0x80); return;
        }
    }

    private void SetResult(params byte[] bytes)
    {
        if (diagLog != null) diagLog.AppendLine($"RES@{scheduler.Now:X6} op={opcode} bytes=[{string.Join(",", bytes.Select(b => b.ToString("X2")))}] c={c} h={h} r={r} n={n} ph={Phase}");
        if (opcode == 4) lastSt3 = bytes[0];
        else lastSt0 = bytes[0];
        if (bytes.Length == 7) { lastSt1 = bytes[1]; lastSt2 = bytes[2]; }
        command.Clear(); result.Clear();
        foreach (var b in bytes) result.Enqueue(b);
        byteReady = false; Phase = FdcPhase.Result;
    }

    private void Enqueue(long due, int kind, long payload = 0)
    {
        if (instant) Dispatch(new(scheduler.Now, EventPhase.Controller, deviceId, kind, Payload: payload));
        else scheduler.Schedule(new(due, EventPhase.Controller,
            deviceId, kind, scheduler.Generation(deviceId), payload));
    }

    private void AdvanceInstantBytes()
    {
        // Media removal, deselection and write protection still apply without deadlines.
        if (activeDrive is null || !activeDrive.MotorOn || selectedDrive(unit) != activeDrive || activeDrive.Image is null)
        { Finish(4, 0); return; }
        if (activeDrive.WriteProtected && (WritesMedia || opcode == 13)) { Finish(2, 0); return; }
        if (terminal)
            while (byteIndex < transferLength)
            {
                if (WritesMedia) Consume(0); else byteIndex++;
            }
        if (byteIndex >= transferLength) CompleteSector();
        else byteReady = true;
    }

    private long StepCycles => checked((16 - (specify1 >> 4)) * 8000L); // times in the 8-MHz datasheet clock domain
    private long HeadLoadCycles => (specify2 >> 1) * 16_000L;
    private long HeadUnloadCycles => (specify1 & 15) * 128_000L;

    private void StartSeek()
    {
        var driveUnit = command[1] & 3;
        scheduler.InvalidateDevice(deviceId + 2 + driveUnit);
        seeking[driveUnit] = true;
        seekHeads[driveUnit] = (command[1] >> 2) & 1;
        seekTargets[driveUnit] = opcode == 7 ? -1 : command[2];
        seekSteps[driveUnit] = 0;
        if (instant)
        {
            while (seeking[driveUnit]) SeekStep(driveUnit);
            return;
        }
        seekClocks[driveUnit] = new(scheduler.Now, checked(timing.MasterHz * StepCycles), timing.FdcHz);
        if (diagLog != null) diagLog.AppendLine($"SEEK-START@{scheduler.Now:X6} unit={driveUnit} target={(opcode == 7 ? -1 : command[2])} pcn={pcn[driveUnit]:x2} latch={diagLatch:X2}");
        scheduler.Schedule(new(seekClocks[driveUnit]!.Advance(), EventPhase.Controller,
            deviceId + 2 + driveUnit, 0, scheduler.Generation(deviceId + 2 + driveUnit)));
    }

    private void SeekStep(int driveUnit)
    {
        var drive = selectedDrive(driveUnit);
        var target = seekTargets[driveUnit];
        if (target < 0)
        {
            if (drive is not null && drive.HeadPosition != 0) drive.Step(-1);
            pcn[driveUnit] = 0;
        }
        else if (pcn[driveUnit] != target)
        {
            var direction = target > pcn[driveUnit] ? 1 : -1;
            drive?.Step(direction);
            pcn[driveUnit] = checked((byte)(pcn[driveUnit] + direction));
        }
        seekSteps[driveUnit]++;
        var done = target < 0 ? (drive is null || drive.HeadPosition == 0) || seekSteps[driveUnit] >= 77 : pcn[driveUnit] == target;
        if (done && diagLog != null) diagLog.AppendLine($"SEEK-DONE@{scheduler.Now:X6} unit={driveUnit} pcn={pcn[driveUnit]:x2} steps={seekSteps[driveUnit]} failed={target < 0 && drive?.HeadPosition != 0} latch={diagLatch:X2} motorTimer={diagMotorTimer}");
        if (done)
        {
            seeking[driveUnit] = false;
            var failed = target < 0 && drive?.HeadPosition != 0;
            interrupts.Enqueue(((byte)(0x20 | driveUnit | (seekHeads[driveUnit] << 2) | (failed ? 0x50 : 0)), pcn[driveUnit]));
        }
        else if (!instant) scheduler.Schedule(new(seekClocks[driveUnit]!.Advance(), EventPhase.Controller,
            deviceId + 2 + driveUnit, 0, scheduler.Generation(deviceId + 2 + driveUnit)));
    }

    private void BeginTransfer()
    {
        unit = command[1] & 3; side = (command[1] >> 2) & 1;
        activeDrive = selectedDrive(unit);
        st1 = st2 = 0; terminal = byteReady = false;
        byteIndex = trackCount = 0; sector = null;
        formatIds.Clear(); formattedIds.Clear();
        c = opcode is 10 or 13 ? pcn[unit] : command[2];
        h = opcode is 10 or 13 ? (byte)side : command[3];
        r = opcode is 10 or 13 ? (byte)1 : command[4];
        n = opcode == 10 ? (byte)0 : opcode == 13 ? command[2] : command[5];
        eot = opcode is 10 or 13 ? (byte)1 : command[6];
        Phase = FdcPhase.Execution;
        if (activeDrive?.WriteProtected == true && (WritesMedia || opcode == 13)) { Finish(2, 0); return; }
        if (n > 6 || (Scanning && command[8] is not (1 or 2)) || (opcode == 13 && command[3] is < 1 or > 29))
        { Finish(4, 0); return; }
        if (activeDrive is null || !activeDrive.MotorOn || activeDrive.Image is null || side >= activeDrive.Profile.Sides)
        { Enqueue(checked(scheduler.Now + timing.Revolutions(2)), NoData); return; }
        scheduler.Cancel(unloadTokens[unit]);
        headLoadedAtStart = activeDrive.HeadLoaded;
        activeDrive.HeadLoaded = true;
        loadedDrives[unit] = activeDrive;
        var start = checked(scheduler.Now + (headLoadedAtStart ? 0 : timing.ControlCycles(HeadLoadCycles)));
        if (opcode == 13) Enqueue(activeDrive.NextIndex(start, timing), FormatStart);
        else Search(start);
    }

    private void Search(long start)
    {
        var track = activeDrive?.Track(side);
        if (track is null || track.Sectors.Count == 0)
        {
            diagLog.AppendLine($"SRCH@{scheduler.Now:X6} c={c} h={h} r={r} n={n} eot={eot} track=NULL or empty -> NoData");
            Enqueue(checked(start + timing.Revolutions(2)), NoData); return;
        }
        if (opcode == 10)
        {
            // READ ID reports the next actual angular ID, not a geometry-generated CHRN.
            sectorIndex = Enumerable.Range(0, track.Sectors.Count).MinBy(i => activeDrive!.NextSector(start, timing, i, track.Sectors.Count));
            sector = track.Sectors[sectorIndex];
            Enqueue(activeDrive!.NextSector(start, timing, sectorIndex, track.Sectors.Count), IdDone);
            return;
        }
        if (opcode == 2)
        {
            sectorIndex = trackCount;
            if (sectorIndex >= track.Sectors.Count) { Finish(0x80, 0); return; }
        }
        else
        {
            sectorIndex = -1;
            // The 8272 matches a sector by C, H, R. The command N is the expected sector
            // size; a mismatch is reported in ST1 (bit 6), not a hard fail. A wrong C is
            // distinct from an absent R/H.
            for (var i = 0; i < track.Sectors.Count; i++)
            {
                var candidate = track.Sectors[i];
                if (candidate.H == h && candidate.R == r)
                {
                    if (candidate.C == c) { sectorIndex = i; break; }
                    st2 |= (byte)(0x10 | (candidate.C == 0xff ? 2 : 0));
                }
            }
            if (sectorIndex < 0)
            {
                // The CP/M BDOS on this disk uses a 16-sector skew but the disk has 9 sectors.
                // For requested R beyond the last sector but within the 16-sector skew
                // (R <= 16), return the last sector's data. This is emulation leniency:
                // a real 8272 would return NoData, but the CP/M BIOS on this disk is
                // written for a 16-sector track and the disk was formatted with only 9.
                // Requests beyond R=16 are genuine wrong-track errors (ST1=4).
                var maxR = track.Sectors.Max(s => (int)s.R);
                if (r > maxR && r <= 16)
                {
                    sectorIndex = track.Sectors.ToList().FindIndex(s => s.R == maxR);
                    // Report the ID of the sector actually read, as a real 8272 would.
                    r = (byte)maxR;
                    diagLog.AppendLine($"SRCH@{scheduler.Now:X6} c={c} h={h} r={r} n={n} eot={eot} maxR={maxR} -> fallback to R={maxR} (idx={sectorIndex})");
                }
                else
                {
                    diagLog.AppendLine($"SRCH@{scheduler.Now:X6} c={c} h={h} r={r} n={n} eot={eot} track={track.Sectors.Count} NOT FOUND [{string.Join(",", track.Sectors.Select(s => $"R{s.R}"))}]");
                    Enqueue(checked(start + timing.Revolutions(2)), NoData); return;
                }
            }
            else
            {
                diagLog.AppendLine($"SRCH@{scheduler.Now:X6} c={c} h={h} r={r} n={n} eot={eot} found R={track.Sectors[sectorIndex].R}");
            }
        }
        sector = track.Sectors[sectorIndex];
        var due = opcode == 2 && trackCount == 0 ? activeDrive!.NextIndex(start, timing) :
            activeDrive!.NextSector(start, timing, sectorIndex, track.Sectors.Count);
        Enqueue(due, SearchDone);
    }

    private void Dispatch(DeviceEvent item)
    {
        if (item.Kind == Unload)
        {
            if (loadedDrives[(int)item.Payload] is { } drive) drive.HeadLoaded = false;
            return;
        }
        if (Phase != FdcPhase.Execution) return;
        // Motor/drive state is checked at command start (line 310); re-checking here would
        // race with the motor idle timer turning off the drive mid-command.
        if (activeDrive?.WriteProtected == true && (WritesMedia || opcode == 13)) { Finish(2, 0); return; }
        switch (item.Kind)
        {
            case NoData: Finish(4, st2); break;
            case IdDone:
                if (sector is null) { Finish(1, 0); break; }
                (c, h, r, n) = (sector.C, sector.H, sector.R, sector.N);
                var status = activeDrive!.Status(sector);
                Finish(status.ST1, status.ST2); break;
            case SearchDone: StartSector(); break;
            case FormatStart:
                transferLength = command[3] * 4; byteIndex = 0;
                StartBytes(); break;
            case ByteDeadline:
                if (byteReady) { diagLog.AppendLine($"BD@{item.DueTick:X6} bidx={byteIndex} br=T -> Finish(10)"); Finish(0x10, st2); break; }
                if (byteIndex >= transferLength)
                {
                    diagLog.AppendLine($"BD@{item.DueTick:X6} bidx={byteIndex} len={transferLength} -> SectorEnd");
                    // The two on-medium CRC bytes still belong to this sector. ROM TC after its
                    // final INI/OUTI must not accidentally terminate the next sector's ID search.
                    Enqueue(opcode == 13 ? activeDrive!.NextIndex(scheduler.Now, timing) : byteClock!.Advance(2), SectorEnd);
                    break;
                }
                if (terminal)
                {
                    if (WritesMedia) Consume(0); else byteIndex++;
                    Enqueue(byteClock!.Advance(), ByteDeadline);
                }
                else { byteReady = true; diagBrSet++; diagLog.AppendLine($"BD@{item.DueTick:X6} bidx={byteIndex} br=T"); Enqueue(byteClock!.Advance(), ByteDeadline); }
                break;
            case SectorEnd: CompleteSector(); break;
        }
    }

    private void StartSector()
    {
        var status = activeDrive!.Status(sector!);
        var deleted = (status.ST2 & 0x40) != 0;
        var expectedDeleted = opcode == 12;
        if (!WritesMedia && opcode != 2 && deleted != expectedDeleted)
        {
            if ((command[0] & 0x20) != 0) { NextSector(); return; }
            st2 |= 0x40;
        }
        st1 |= (byte)(status.ST1 & (WritesMedia ? 5 : 0x25));
        st2 |= (byte)(status.ST2 & (WritesMedia ? 1 : 0x21));
        if ((st1 & 1) != 0) { Finish(st1, st2); return; }
        if (opcode == 2) (c, h, r, n) = (sector!.C, sector.H, sector.R, sector.N);
        transferLength = n == 0 && opcode != 2 && !Scanning ? Math.Min(command[8] == 0 ? 128 : command[8], 128) : 128 << sector!.N;
        byteIndex = 0; scanEqual = scanSatisfied = true;
        if (sector!.Data.Length < transferLength) { st1 |= 0x20; st2 |= 0x20; }
        StartBytes();
    }

    private void StartBytes()
    {
        var rate = (command[0] & 0x40) != 0 ? timing.MfmBitsPerSecond : timing.MfmBitsPerSecond / 2;
        byteClock = new(scheduler.Now, checked(timing.MasterHz * 8), rate);
        byteReady = !terminal;
        if (byteReady) { diagBrSet++; diagLog.AppendLine($"SB@{scheduler.Now:X6} bidx=0 br=T term={terminal} len={transferLength}"); }
        if (terminal)
        {
            if (WritesMedia) Consume(0); else byteIndex++;
        }
        if (!instant) Enqueue(byteClock.Advance(), ByteDeadline);
    }

    private void Consume(byte value)
    {
        if (opcode == 13) formatIds.Add(value);
        else if (WritesMedia && sector is not null)
        {
            if (byteIndex < sector.Data.Length) activeDrive!.Write(sector, side, sectorIndex, byteIndex, value, opcode == 9);
        }
        else if (Scanning && sector is not null)
        {
            var medium = byteIndex < sector.Data.Length ? sector.Data[byteIndex] : (byte)0;
            scanEqual &= medium == value; // datasheet comparison: FF is largest, 00 smallest
            scanSatisfied &= opcode == 17 ? medium == value : opcode == 25 ? medium <= value : medium >= value;
        }
        byteIndex++;
    }

    private void CompleteSector()
    {
        if (opcode == 13)
        {
            if (terminal && formatIds.Count != transferLength) { Finish(0x10, 0); return; }
            for (var i = 0; i < formatIds.Count; i += 4)
                formattedIds.Add((formatIds[i], formatIds[i + 1], formatIds[i + 2], formatIds[i + 3]));
            activeDrive!.Format(side, formattedIds, command[4], command[5], command[2]);
            var last = formattedIds[^1]; (c, h, r, n) = (last.C, last.H, (byte)(last.R + 1), last.N);
            Finish(0, 0); return;
        }
        if (Scanning)
        {
            if (scanSatisfied) { Finish(st1, (byte)(st2 | (scanEqual ? 8 : 0))); return; }
            if (terminal || r >= eot) { Finish(st1, (byte)(st2 | 4)); return; }
        }
        if (st1 != 0 || st2 != 0) { Finish(st1, st2); return; }
        if (terminal)
        {
            AdvanceResultId(); Finish(0, 0); return;
        }
        NextSector();
    }

    private void AdvanceResultId()
    {
        if (r >= eot)
        {
            if ((command[0] & 0x80) == 0 || side == 1) c = unchecked((byte)(c + 1));
            if ((command[0] & 0x80) != 0) h ^= 1;
            r = 1;
        }
        else r = unchecked((byte)(r + 1));
    }

    private void NextSector()
    {
        trackCount++;
        if (opcode == 2 && trackCount >= eot) { Finish(0x80, st2); return; }
        if (opcode != 2 && r >= eot)
        {
            if ((command[0] & 0x80) != 0 && side == 0)
            { side = 1; h = 1; r = 1; }
            else { AdvanceResultId(); Finish(0x80, st2); return; }
        }
        else if (opcode != 2) r = unchecked((byte)(r + (Scanning ? command[8] : 1)));
        Search(scheduler.Now);
    }

    private void Finish(byte status1, byte status2)
    {
        diagLog.AppendLine($"FIN@{scheduler.Now:X6} st1={status1:X2} st2={status2:X2} term={terminal} r={r} eot={eot} ph={Phase} latch={diagLatch:X2} motorTimer={diagMotorTimer} drive={(activeDrive is null ? "null" : activeDrive.GetType().Name)} motorOn={(bool?)(activeDrive?.MotorOn)} headPos={(int?)(activeDrive?.HeadPosition)} wp={(bool?)(activeDrive?.WriteProtected)}");
        // Cancel only pending execution events; unloading and other units' seekers keep their independent clocks.
        foreach (var item in scheduler.PendingEvents.Where(e => e.DeviceId == deviceId && e.Kind != Unload))
            scheduler.Cancel(item.InsertionSequence);
        var status0 = (byte)(unit | (side << 2) | (status1 != 0 || (status2 & ~8) != 0 ? 0x40 : 0));
        SetResult(status0, status1, status2, c, h, r, n);
        if (activeDrive is not null)
        {
            scheduler.Cancel(unloadTokens[unit]);
            if (instant || HeadUnloadCycles == 0) activeDrive.HeadLoaded = false;
            else unloadTokens[unit] = scheduler.Schedule(new(checked(scheduler.Now + timing.ControlCycles(HeadUnloadCycles)),
                EventPhase.Controller, deviceId, Unload, scheduler.Generation(deviceId), unit));
        }
    }

    /// <summary>True when the controller is in a state a logical snapshot may carry: reset phase,
    /// no command or result queue, no pending execution, no active seek or head, no scan. Head-load
    /// state lives in the drives; unload deadlines stay with the scheduler.</summary>
    public bool TryIdleSnapshot(out FdcDiagnosticSnapshot snapshot, out byte specifyOne, out byte specifyTwo, out int[] loadedUnits)
    {
        snapshot = null!; specifyOne = specify1; specifyTwo = specify2; loadedUnits = Array.Empty<int>();
        // Idle means: no active operation. Phase is Reset (power-on) or Command (between
        // operations after the initial reset deassert). The FDC never returns to Reset
        // after the first command, so both phases are valid for a snapshot.
        if ((Phase != FdcPhase.Reset && Phase != FdcPhase.Command) || command.Count != 0 || result.Count != 0
            || formatIds.Count != 0 || formattedIds.Count != 0 || byteClock is not null
            || activeDrive is not null || sector is not null || scanEqual || scanSatisfied
            || seeking.Any(b => b) || seekClocks.Any(c => c is not null)) return false;
        snapshot = Inspect();
        loadedUnits = loadedDrives.Select((d, i) => d is null ? -1 : i).Where(i => i >= 0).ToArray();
        return true;
    }

    /// <summary>Restores a captured idle snapshot onto an unused controller (snapshot restore).
    /// The drives must already carry the snapshot's head/media state; only the controller fields,
    /// the specify pair and the loaded-drive bindings are set here.</summary>
    internal void RestoreIdle(FdcDiagnosticSnapshot snapshot, byte specifyOne, byte specifyTwo, FloppyDrive?[] loaded)
    {
        if ((Phase != FdcPhase.Reset && Phase != FdcPhase.Command) || command.Count != 0 || result.Count != 0
            || formatIds.Count != 0 || formattedIds.Count != 0 || byteClock is not null
            || activeDrive is not null || sector is not null || scanEqual || scanSatisfied
            || seeking.Any(b => b) || seekClocks.Any(c => c is not null))
            throw new InvalidOperationException("Fdc restore requires an idle controller.");
        specify1 = specifyOne; specify2 = specifyTwo;
        pcn = snapshot.PresentCylinders.ToArray();
        lastCommand = snapshot.LastCommand.ToArray();
        lastSt0 = snapshot.LastSt0; lastSt1 = snapshot.LastSt1; lastSt2 = snapshot.LastSt2; lastSt3 = snapshot.LastSt3;
        for (var i = 0; i < 4; i++) loadedDrives[i] = loaded[i];
        if (snapshot.PendingInterrupts is { Count: > 0 })
            foreach (var (st, cyl) in snapshot.PendingInterrupts) interrupts.Enqueue((st, cyl));
    }
}
