using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Floppy;

/// <summary>05/07 latch and board wiring. Caller synchronizes the shared scheduler before every port effect.</summary>
public sealed class FloppyController
{
    private readonly DeviceScheduler scheduler;
    private readonly int motorDeviceId;
    /// <summary>The FDC's scheduler device id (the controller's stable base id).</summary>
    public int FdcDeviceId { get; }
    public FloppyDrive DriveA { get; }
    public FloppyDrive DriveB { get; }
    public Fdc8272 Fdc { get; }
    internal long MasterTick => scheduler.Now;
    public byte Latch { get; private set; }
    public bool MotorTimerRunning { get; private set; }
    public bool DriveB80TrackJumper { get; }
    public FdcTiming Timing { get; }
    /// <summary>Opt-in fdc.instant profile upgrade; faithful timing is the default.</summary>
    public bool Instant { get; }

    /// <remarks>Reserves stable IDs base..base+5 (FDC, motor, four seek engines). No decoder or DMA path lives here.</remarks>
    public FloppyController(DeviceScheduler scheduler, int stableDeviceId, FloppyDrive driveA, FloppyDrive driveB,
        FdcTiming? timing = null, bool driveB80TrackJumper = false, bool instant = false)
    {
        this.scheduler = scheduler;
        Instant = instant;
        FdcDeviceId = stableDeviceId;
        DriveA = driveA; DriveB = driveB;
        DriveB80TrackJumper = driveB80TrackJumper;
        Timing = timing ?? new(); Timing.Validate();
        motorDeviceId = checked(stableDeviceId + 1);
        scheduler.RegisterDevice(motorDeviceId, _ => { MotorTimerRunning = false; UpdateMotors(); });
        Fdc = new(scheduler, stableDeviceId, DriveFor, Timing, instant);
    }

    private FloppyDrive? DriveFor(int unit) => unit switch
    {
        0 when (Latch & 2) != 0 => DriveA,
        1 when (Latch & 4) != 0 => DriveB,
        _ => null
    };

    public byte ReadLatch() => (byte)(0xfc | (DriveB80TrackJumper ? 2 : 0) |
        (MotorTimerRunning && (Latch & 6) != 0 ? 0 : 1));

    public void WriteLatch(byte value)
    {
        var previous = Latch;
        Latch = (byte)(value & 0x1f);
        // b3 (D3) through a 7406 triggers the LM555 motor monostable and discharges its capacitor
        // (schematics.md §6). While D3 is held the 555 trigger stays asserted: the capacitor cannot
        // recharge, so the 1.7 s one-shot cannot run and the motor stays on. Only after D3 is released
        // does the capacitor recharge over the RC monostable (the 1.7 s discharge tail).
        bool held = (Latch & 8) != 0, wasHeld = (previous & 8) != 0;
        if (held)
        {
            scheduler.InvalidateDevice(motorDeviceId); // trigger held: discharge, cancel any pending tail
            MotorTimerRunning = true;                  // 555 output high while the trigger is asserted
        }
        else if (wasHeld)
        {
            scheduler.InvalidateDevice(motorDeviceId);
            scheduler.Schedule(new(checked(scheduler.Now + Timing.MotorTicks), EventPhase.Controller,
                motorDeviceId, 0, scheduler.Generation(motorDeviceId))); // discharge tail, due in absolute time
            MotorTimerRunning = true;
        }
        UpdateMotors();
        Fdc.LogLatch((byte)(value & 0x1f));
        Fdc.SetReset((Latch & 0x10) == 0);
        if ((Latch & 1) != 0 && (previous & 1) == 0) Fdc.TerminalCount();
    }

    private void UpdateMotors()
    {
        DriveA.SetMotor(MotorTimerRunning && (Latch & 2) != 0, scheduler.Now);
        DriveB.SetMotor(MotorTimerRunning && (Latch & 4) != 0, scheduler.Now);
    }

    /// <summary>The scheduler id of the common RC motor timer event.</summary>
    public int MotorDeviceId => motorDeviceId;

    /// <summary>Restores the latch and RC-timer mark of a captured idle snapshot onto a fresh
    /// controller (snapshot restore). Drives and the FDC restore around it; no motor update or
    /// reset pulse is raised here, because the restored drives already carry the snapshot state.</summary>
    internal void RestoreIdle(byte latch, bool motorTimerRunning)
    {
        if (Latch != 0 || MotorTimerRunning)
            throw new InvalidOperationException("Controller restore requires a fresh latch.");
        Latch = (byte)(latch & 0x1f);
        MotorTimerRunning = motorTimerRunning;
    }
}
