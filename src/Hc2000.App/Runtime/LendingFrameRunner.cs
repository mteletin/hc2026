using System.Globalization;
using Hc2000.Machine;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Memory;

namespace Hc2000.App.Runtime;

/// <summary>Runs the shipped one-frame lending demo ROM on a cold X5 + X2 machine inside an
/// <see cref="EmulationWorker"/>, holds the final VGA frame on the X2 screen, and reports the row's
/// interrupt receipts as text (the demo reads its screen as text, hc2000-expansion-lending-interrupts).</summary>
public static class LendingFrameRunner
{
    public const string EmbeddedRomResourceName = "Hc2000.App.Examples.LendingFrame.oneframe.bin";
    public const ushort PhaseAddress = 0x9000;
    public const ushort FailOpAddress = 0x9001;
    public const ushort FailCodeAddress = 0x9002;
    public const ushort UnitDoneAddress = 0x9003;
    public const ushort DmaDoneAddress = 0x9004;
    public const ushort FramePresentedAddress = 0x9005;
    public const byte FinishedMarker = 0xff;
    public const byte FailedMarker = 0xee;
    public const int DefaultFrameLimit = 2000;

    public static byte[] LoadEmbeddedRom()
    {
        var assembly = typeof(LendingFrameRunner).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedRomResourceName);
        if (stream is not null)
        {
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        var diskPath = Path.Combine(AppContext.BaseDirectory, "examples", "lending-frame", "oneframe.bin");
        if (File.Exists(diskPath))
            return File.ReadAllBytes(diskPath);
        throw new InvalidOperationException($"Missing embedded lending-frame demo ROM '{EmbeddedRomResourceName}'.");
    }

    /// <summary>Creates an <see cref="EmulationWorker"/> that runs the one-frame lending demo and reports
    /// the result once the ROM reaches its finished marker (or names the failure). After finishing, the
    /// worker holds the machine on the final frame so the four bands stay visible on the X2 VGA screen.</summary>
    public static EmulationWorker CreateWorker(
        Action<LendingFrameRunResult>? onCompleted = null,
        int frameLimit = DefaultFrameLimit,
        SpeedMode initialSpeed = SpeedMode.Normal,
        byte[]? romOverride = null)
    {
        if (frameLimit <= 0) throw new ArgumentOutOfRangeException(nameof(frameLimit));
        var program = romOverride ?? LoadEmbeddedRom();
        if (program.Length is 0 or > 0x4000)
            throw new ArgumentException($"Lending-frame ROM length {program.Length} is outside 1..16384 bytes.", nameof(romOverride));

        var basicRom = new byte[0x4000];
        Array.Copy(program, basicRom, program.Length);
        var prepared = new PreparedBasicMachine(
            (MachineProfile.Hc2000V2PagedExpansionX5V1, basicRom, new byte[0x4000], new byte[0x4000]),
            x2Vga: true);

        HcMemory? memory = null;
        var totalFrames = 0;
        var finishedSeen = false;
        var started = false;
        var stopped = false;

        void Finish(bool completed, string? failureMessage, MachineFrameResult result)
        {
            if (stopped) return;
            stopped = true;
            onCompleted?.Invoke(new LendingFrameRunResult(
                completed,
                totalFrames,
                memory?.Read(UnitDoneAddress) ?? 0,
                memory?.Read(DmaDoneAddress) ?? 0,
                memory?.Read(FramePresentedAddress) ?? 0,
                result.ExpansionVideo?.Width ?? 0,
                result.ExpansionVideo?.Height ?? 0,
                failureMessage));
        }

        return new EmulationWorker(
            factory: () => BasicMachineComposition.CreateColdNewMachine(
                prepared,
                new ComposedFloppySlot(),
                new FloppyDrive(new()),
                new FloppyDrive(new()),
                _ => { },
                _ => { },
                observeCpu: (_, value) => memory = value,
                observePorts: _ => { }),
            boundary: context =>
            {
                if (started) return;
                started = true;
                context.SetSpeed(initialSpeed);
            },
            cleanup: context => context.Machine.Dispose(),
            executionAllowed: () => !stopped,
            afterFrame: (_, result, _, _) =>
            {
                if (stopped || memory is not { } ram) return false;
                totalFrames++;
                var marker = ram.Read(PhaseAddress);
                if (marker == FailedMarker)
                {
                    Finish(false, string.Create(CultureInfo.InvariantCulture,
                        $"Demo failed at op {ram.Read(FailOpAddress)} with code 0x{ram.Read(FailCodeAddress):x2}."), result);
                    return false;
                }

                if (marker == FinishedMarker)
                {
                    // One more frame after the finished marker: the present is committed by then and the card
                    // holds the frame, so the X2 screen keeps the four bands.
                    if (finishedSeen)
                    {
                        var unitDone = ram.Read(UnitDoneAddress);
                        var dmaDone = ram.Read(DmaDoneAddress);
                        var framePresented = ram.Read(FramePresentedAddress);
                        var countsMatch = unitDone == 4 && dmaDone == 8 && framePresented == 1;
                        Finish(countsMatch, countsMatch ? null : string.Create(CultureInfo.InvariantCulture,
                            $"Unexpected interrupt counts: unit-done {unitDone}, dma-complete {dmaDone}, frame-presented {framePresented}."), result);
                    }
                    else
                    {
                        finishedSeen = true;
                    }
                    return false;
                }

                if (totalFrames >= frameLimit)
                {
                    Finish(false, string.Create(CultureInfo.InvariantCulture,
                        $"Demo did not finish in {frameLimit:N0} frames (phase 0x{marker:x2})."), result);
                }
                return false;
            });
    }
}

/// <summary>The finished state of one in-app run of the one-frame lending demo.</summary>
public sealed record LendingFrameRunResult(
    bool Completed,
    int Frames,
    byte UnitDone,
    byte DmaDone,
    byte FramePresented,
    int FrameWidth,
    int FrameHeight,
    string? FailureMessage);
