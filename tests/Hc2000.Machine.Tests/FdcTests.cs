using Hc2000.Machine.Floppy;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class FdcTests
{
    [Fact]
    public void ReadDataOffersHalfOpenByteWindowAndExactDeadlineOverruns()
    {
        var scheduler = new DeviceScheduler();
        var drive = new FloppyDrive(new(), Dsk.Create(new(80, 2, 9, 512)));
        var board = new FloppyController(scheduler, 10, drive, new FloppyDrive(new()));
        board.WriteLatch(0x1a);
        Send(board.Fdc, 3, 0xdf, 1); // short head load; non-DMA
        Send(board.Fdc, 0x46, 0, 0, 0, 1, 2, 9, 0x2a, 0xff);
        AdvanceUntilReady(scheduler, board.Fdc);
        Assert.Equal(0xf0, board.Fdc.ReadStatus() & 0xf0);
        var deadline = scheduler.PendingEvents.Single(e => e.DeviceId == 10).DueTick;
        scheduler.AdvanceTo(deadline);
        Assert.Equal(new byte[] { 0x40, 0x10, 0, 0, 0, 1, 2 }, Result(board.Fdc));
    }

    [Fact]
    public void ByteReadBeforeDeadlineSucceedsAndRepeatedPollCannotConsumeAnotherByte()
    {
        var (s, b) = Create();
        Send(b.Fdc, 0x46, 0, 0, 0, 1, 2, 9, 0x2a, 0xff);
        AdvanceUntilReady(s, b.Fdc);
        var first = s.Now;
        var deadline = s.PendingEvents.Single(e => e.DeviceId == 10).DueTick;
        s.AdvanceTo(deadline - 1);
        Assert.Equal(0xe5, b.Fdc.ReadData());
        Assert.Equal(0x70, b.Fdc.ReadStatus());
        Assert.Equal(0xff, b.Fdc.ReadData());
        s.AdvanceTo(deadline);
        Assert.Equal(0xe5, b.Fdc.ReadData());
        Assert.Equal(first + Ceil(17_734_475L * 16, 250_000), s.PendingEvents.Single(e => e.DeviceId == 10).DueTick);
    }

    [Fact]
    public void SpecifyInvalidSenseAndResetHaveDatasheetResultLengthsAndNoExternalIrq()
    {
        var (s, b) = Create();
        Assert.Equal(0xdf, b.Fdc.Specify1); Assert.Equal(1, b.Fdc.Specify2);
        Send(b.Fdc, 8); Assert.Equal(new byte[] { 0xc0, 0 }, Result(b.Fdc));
        for (byte i = 1; i < 4; i++) { Send(b.Fdc, 8); Assert.Equal(new byte[] { (byte)(0xc0 | i), 0 }, Result(b.Fdc)); }
        Send(b.Fdc, 8); Assert.Equal(new byte[] { 0x80 }, Result(b.Fdc));
        Send(b.Fdc, 0); Assert.Equal(new byte[] { 0x80 }, Result(b.Fdc));
        Send(b.Fdc, 4, 0); Assert.Equal(new byte[] { 0x38 }, Result(b.Fdc));
        b.DriveA.WriteProtected = true;
        Send(b.Fdc, 4, 4); Assert.Equal(new byte[] { 0x7c }, Result(b.Fdc));
        Send(b.Fdc, 3, 0x12, 0); Assert.True(b.Fdc.NonDma);
        var now = s.Now;
        b.WriteLatch(2); Assert.Equal(0, b.Fdc.ReadStatus());
        b.Fdc.WriteData(0x46); Assert.Equal(0xff, b.Fdc.ReadData());
        b.WriteLatch(0x12); Assert.Equal(now, s.Now); Assert.Equal(0x80, b.Fdc.ReadStatus());
    }

    [Theory]
    [InlineData(4_000_000, 6)] [InlineData(8_000_000, 3)]
    [Trait("hardware", "unverified")]
    public void SeekClockIsProfileConstantAndSenseReportsPcn(long hz, int milliseconds)
    {
        var (s, b) = Create(timing: new(FdcHz: hz)); DrainReset(b.Fdc);
        Send(b.Fdc, 15, 0, 2);
        Assert.Equal(0x81, b.Fdc.ReadStatus());
        var step = s.PendingEvents.Single(e => e.DeviceId == 12).DueTick;
        Assert.Equal(Ceil(17_734_475L * milliseconds, 1000), step);
        s.AdvanceTo(step); Assert.Equal(1, b.DriveA.HeadPosition);
        var second = s.PendingEvents.Single(e => e.DeviceId == 12).DueTick;
        Assert.Equal(Ceil(17_734_475L * milliseconds * 2, 1000), second);
        s.AdvanceTo(second); Assert.Equal(2, b.DriveA.HeadPosition); Assert.Equal(0x80, b.Fdc.ReadStatus());
        Send(b.Fdc, 8); Assert.Equal(new byte[] { 0x20, 2 }, Result(b.Fdc));
        Send(b.Fdc, 7, 0);
        while ((b.Fdc.ReadStatus() & 1) != 0) s.AdvanceTo(s.PendingEvents.Min(e => e.DueTick));
        Assert.Equal(0, b.DriveA.HeadPosition); Assert.False(b.DriveA.DiskChanged);
        Send(b.Fdc, 8); Assert.Equal(new byte[] { 0x20, 0 }, Result(b.Fdc));
    }

    [Fact]
    public void ParallelSeekAndResetCancelOnlyTheirOwnPendingTransitions()
    {
        var (s, b) = Create(); DrainReset(b.Fdc); b.WriteLatch(0x16);
        Send(b.Fdc, 15, 0, 2); Send(b.Fdc, 15, 1, 3);
        Assert.Equal(0x83, b.Fdc.ReadStatus());
        s.AdvanceTo(Ceil(17_734_475L * 6, 1000));
        Assert.Equal(1, b.DriveA.HeadPosition); Assert.Equal(1, b.DriveB.HeadPosition);
        b.WriteLatch(6); b.WriteLatch(0x16);
        s.AdvanceTo(Ceil(17_734_475L * 30, 1000));
        Assert.Equal(1, b.DriveA.HeadPosition); Assert.Equal(1, b.DriveB.HeadPosition);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, b.Fdc.PresentCylinders);
        Assert.True(b.MotorTimerRunning);
    }

    [Fact]
    public void Unit1SeekThenFormatTrackOneCompletesLikeUnit0()
    {
        var (s, b) = Create();
        b.WriteLatch(0x16); // D1 TC, D2 motor+DriveA select, D3 DriveB select: latch = 0b10110
        DrainReset(b.Fdc); // default Specify (0xdf, 0x01): 2 ms per step (see StartSeek)
        // The 1.7 s RC motor monostable started in the constructor must fire before the seek
        // steps, or the drive is off and the seek completes without moving it.
        // The ROM re-asserts the selection (a D3 rising edge restarts the 1.7 s RC motor
        // monostable); advance past the boot-time timer first so the seek runs with the motor on.
        s.AdvanceTo(b.Timing.MotorTicks);
        b.WriteLatch(0x16); // D1 TC, D2 motor+DriveA select, D3 DriveB select: latch = 0b10110
        Send(b.Fdc, 0x0f, 0x01, 0x01); // SEEK unit1 -> cyl1 (op 15 is a 3-byte command)
        // Unit-1 seek bit set in the status byte (the ROM does not use this; it polls Sense-Interrupt).
        Assert.Equal(0x02, b.Fdc.ReadStatus() & 0x07);
        // The seek-complete interrupt is delivered through SENSE INTERRUPT (op 8): the ROM
        // loops on op 8 until a non-80h status arrives.
        byte[] sense = [];
        for (var i = 0; ; i++)
        {
            if (i >= 5000) { var snap = b.Fdc.Inspect(); throw new InvalidOperationException($"seek-complete interrupt never arrived; sense={b.Fdc.ReadStatus():x2} pending={s.PendingEvents.Count} phase={snap.Phase} pcn=[{string.Join(",", snap.PresentCylinders.Select(c => c.ToString("x2")))}] pendingInterrupts=[{string.Join(",", (snap.PendingInterrupts ?? []).Select(p => p.Item1.ToString("x2")))}] diag={b.Fdc.DiagLog}"); }
            Send(b.Fdc, 0x08);
            sense = Result(b.Fdc);
            if (sense.Length > 0 && sense[0] != 0x80) break;
            // Advance to the seek-step deadline, not to the later motor-timer event; otherwise the
            // seek-complete interrupt would sit in the queue while the 1.7 s timer expires.
            var seekEvent = s.PendingEvents.FirstOrDefault(e => e.DeviceId == b.FdcDeviceId + 3);
            if (seekEvent != default) { s.AdvanceTo(seekEvent.DueTick); continue; }
            long? due = s.PendingEvents.Count > 0 ? (long?)s.PendingEvents.Min(e => e.DueTick) : null;
            if (due is null || due.Value <= s.Now) { s.AdvanceTo(s.Now + 1_000_000); continue; } // no FDC event pending: wait
            s.AdvanceTo(due.Value);
        }
        Assert.Equal(0x21, sense[0]); // no error, unit 1, head 0 loaded
        // The seek consumed the 1.7 s RC motor monostable; the ROM re-asserts the selection
        // before the next FDC operation, restarting the timer with a D3 rising edge.
        b.WriteLatch(0x14); b.WriteLatch(0x1C); // clear bit3 (D3, motor-enable) then re-set it: a real rising edge
        if (!b.MotorTimerRunning || !b.DriveB.MotorOn)
            throw new InvalidOperationException($"re-assert failed: motorTimer={b.MotorTimerRunning} driveB.MotorOn={b.DriveB.MotorOn} latch={b.Latch:X2} now={s.Now:X6}");
        // FORMAT a single track on unit1 after the seek, mirroring the passing unit0 format test
        // (FormatConsumesChrnQuartetsAndPreservesNonSequentialIdsInOverlay): feed the C-H-R-N ID
        // quartets for each sector, then read the 7-byte status. This is the operation that fails
        // with NoData (st1=04) when the 1.7 s motor monostable has expired, which the D3 rising-edge
        // re-assert above restarts.
        Send(b.Fdc, 0x4d, 1, 1, 1, 0x01, 0x2a); // FORMAT unit1 side0 track1, 1 sector, size 2a (512B)
        Transfer(s, b, 4, new byte[] { 1, 0, 1, 1 }, tc: false); // one C-H-R-N quartet
        var format = Result(b.Fdc);
        if (format.Length < 2 || format[1] != 0x00)
            throw new InvalidOperationException($"FORMAT result={string.Join(",", format.Select(x => x.ToString("x2")))} st1={(format.Length>1?format[1]:0):x2} (expected 00); st0={(format.Length>0?format[0]:0):x2} pcn=[{string.Join(",", b.Fdc.PresentCylinders.Select(c => c.ToString("x2")))}] DriveB head={b.DriveB.HeadPosition} motorOn={(object)b.DriveB.MotorOn} fdcMotorOn={b.Fdc.DiagActiveDriveMotorOn} motorTimer={b.MotorTimerRunning} diag={b.Fdc.DiagLog}");
        Assert.Equal(0x00, format[1]);
        Assert.Equal(1, b.DriveB.HeadPosition);
        Assert.Contains(b.DriveB.FormattedTracks, t => t.Cylinder == 1 && t.Side == 0);
    }

    [Fact]
    public void HeldD3KeepsTheMotorOnPastTheMonostableAndReleaseStartsTheDischargeTail()
    {
        var (s, b) = Create();
        // D3 (b3) held: the 555 trigger stays asserted and the capacitor stays discharged, so the
        // 1.7 s one-shot cannot run. The motor (through 7438 NAND(555 out, select)) stays on for the
        // whole time, long past the RC monostable period (schematics.md §6; design-motor-attribution-625784).
        b.WriteLatch(0x14); // D4 not reset (0x10 clear) — use 0x1C for DriveB select + D3 held, reset not forced
        b.WriteLatch(0x1C); // DriveA+DriveB select + D3 held + reset released
        Assert.True(b.MotorTimerRunning); Assert.True(b.DriveB.MotorOn);
        s.AdvanceTo(4 * b.Timing.MotorTicks); // several monostable periods with D3 still held
        Assert.True(b.MotorTimerRunning); Assert.True(b.DriveB.MotorOn); // no expiry while the trigger is held
        // Release D3 (falling edge): the capacitor recharges and the 1.7 s discharge tail starts.
        b.WriteLatch(0x04); // DriveB select, D3 released
        Assert.True(b.DriveB.MotorOn); // tail still running immediately after release
        s.AdvanceTo(s.Now + b.Timing.MotorTicks);
        Assert.False(b.MotorTimerRunning); Assert.False(b.DriveB.MotorOn); // tail expired
        Assert.Equal(0xfd, b.ReadLatch()); // sense b0 reads 1 once the timer has stopped
    }

    [Fact]
    public void RecalibrateStopsAt77PulsesThenSecondCommandCanReachTrackZero()
    {
        var (s, b) = Create(); DrainReset(b.Fdc);
        for (var i = 0; i < 78; i++) b.DriveA.Step(1);
        Send(b.Fdc, 7, 0);
        while ((b.Fdc.ReadStatus() & 1) != 0) s.AdvanceTo(s.PendingEvents.Min(e => e.DueTick));
        Assert.Equal(1, b.DriveA.HeadPosition);
        Send(b.Fdc, 8); Assert.Equal(new byte[] { 0x70, 0 }, Result(b.Fdc));
        Send(b.Fdc, 7, 0);
        while ((b.Fdc.ReadStatus() & 1) != 0) s.AdvanceTo(s.PendingEvents.Min(e => e.DueTick));
        Send(b.Fdc, 8); Assert.Equal(new byte[] { 0x20, 0 }, Result(b.Fdc));
    }

    [Theory]
    [InlineData(40, 16, 256, 1)] [InlineData(80, 9, 512, 2)]
    public void ReadIdUsesImageGeometryAndMechanicalDoubleStepping(int cylinders, int sectors, int size, int n)
    {
        var (s, b) = Create(new(cylinders, 2, sectors, size)); DrainReset(b.Fdc);
        Send(b.Fdc, 15, 0, 2);
        while ((b.Fdc.ReadStatus() & 1) != 0) s.AdvanceTo(s.PendingEvents.Min(e => e.DueTick));
        Send(b.Fdc, 8); Result(b.Fdc);
        Send(b.Fdc, 0x4a, 0); AdvanceUntilReady(s, b.Fdc);
        var id = Result(b.Fdc);
        Assert.Equal(0, id[0]); Assert.Equal(cylinders == 40 ? 1 : 2, id[3]);
        Assert.Equal(0, id[4]); Assert.InRange(id[5], (byte)1, (byte)sectors); Assert.Equal(n, id[6]);
    }

    [Fact]
    public void ArbitraryImageIdsAreReportedAndWrongCylinderIsNotHiddenByGeometry()
    {
        var bytes = Dsk.Create(new(80, 2, 1, 256)).Save();
        bytes[256 + 24] = 0xff; bytes[256 + 26] = 7;
        var (s, b) = Create(image: Dsk.Load(bytes));
        Send(b.Fdc, 0x4a, 0); AdvanceUntilReady(s, b.Fdc);
        Assert.Equal(new byte[] { 0, 0, 0, 0xff, 0, 7, 1 }, Result(b.Fdc));
        Send(b.Fdc, 0x46, 0, 0, 0, 7, 1, 7, 0x2a, 0xff); AdvanceUntilReady(s, b.Fdc);
        Assert.Equal(new byte[] { 0x40, 4, 0x12, 0, 0, 7, 1 }, Result(b.Fdc));
        Send(b.Fdc, 0x46, 0, 0xff, 0, 7, 1, 8, 0x2a, 0xff);
        Assert.Equal(Enumerable.Repeat((byte)0xe5, 256), Transfer(s, b, 256));
        Assert.Equal(new byte[] { 0, 0, 0, 0xff, 0, 8, 1 }, Result(b.Fdc));
    }

    [Fact]
    public void TerminalCountAfterPartialWriteZeroFillsRestOfCurrentSectorOnly()
    {
        var (s, b) = Create();
        Send(b.Fdc, 0x45, 0, 0, 0, 1, 2, 9, 0x2a, 0xff);
        Transfer(s, b, 10, Enumerable.Repeat((byte)0x72, 10).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 2, 2 }, Result(b.Fdc));
        var track = b.DriveA.Track(0)!;
        Assert.All(track.Sectors[0].Data[..10], x => Assert.Equal(0x72, x));
        Assert.All(track.Sectors[0].Data[10..], x => Assert.Equal(0, x));
        Assert.All(track.Sectors[1].Data, x => Assert.Equal(0xe5, x));
        Assert.Equal(new[] { new DirtySector(0, 0, 0) }, b.DriveA.DirtySectors);
    }

    [Theory]
    [InlineData(0x45)] [InlineData(0x49)] [InlineData(0x4d)]
    public void WriteProtectTerminatesWriteDeletedAndFormatWithoutChangingImage(byte op)
    {
        var (s, b) = Create(); var before = b.DriveA.Image!.Save(); b.DriveA.WriteProtected = true;
        if (op == 0x4d) Send(b.Fdc, op, 0, 2, 9, 0x2a, 0xe5);
        else Send(b.Fdc, op, 0, 0, 0, 1, 2, 9, 0x2a, 0xff);
        Assert.Equal(2, Result(b.Fdc)[1]); Assert.Empty(b.DriveA.DirtySectors);
        Assert.Equal(before, b.DriveA.Image.Save()); Assert.Equal(0, s.Now);
    }

    [Fact]
    public void DeletedDataControlMarkAndSkipAreDistinctFromCrcErrors()
    {
        var (s, b) = Create(new(80, 2, 2, 128));
        Send(b.Fdc, 0x49, 0, 0, 0, 1, 0, 2, 0x2a, 128);
        Transfer(s, b, 128, Enumerable.Repeat((byte)0x31, 128).ToArray()); Result(b.Fdc);
        Send(b.Fdc, 0x46, 0, 0, 0, 1, 0, 2, 0x2a, 128);
        Transfer(s, b, 128); Assert.Equal(0x40, Result(b.Fdc)[2]);
        Send(b.Fdc, 0x4c, 0, 0, 0, 1, 0, 2, 0x2a, 128);
        Assert.All(Transfer(s, b, 128), x => Assert.Equal(0x31, x)); Assert.Equal(0, Result(b.Fdc)[0]);
        Send(b.Fdc, 0x66, 0, 0, 0, 1, 0, 2, 0x2a, 128);
        Assert.All(Transfer(s, b, 128), x => Assert.Equal(0xe5, x)); Assert.Equal(0, Result(b.Fdc)[0]);
    }

    [Theory]
    [InlineData(0x51, 0xe5, 8)] [InlineData(0x59, 0xf0, 0)] [InlineData(0x5d, 0xe0, 0)]
    [InlineData(0x51, 0x10, 4)] [InlineData(0x51, 0xff, 4)]
    public void AllThreeScanCommandsCompareAgainstCpuBytes(byte op, byte compare, int status)
    {
        var (s, b) = Create(new(80, 2, 1, 128));
        Send(b.Fdc, op, 0, 0, 0, 1, 0, 1, 0x2a, 1);
        Transfer(s, b, 128, Enumerable.Repeat(compare, 128).ToArray(), tc: false);
        Assert.Equal(status, Result(b.Fdc)[2]);
    }

    [Fact]
    public void ScanLowOrEqualRequiresEveryComparedByteNotLexicographicOrder()
    {
        var (s, b) = Create(new(80, 2, 1, 128));
        var compare = Enumerable.Repeat((byte)0xf0, 128).ToArray(); compare[1] = 0x10;
        Send(b.Fdc, 0x59, 0, 0, 0, 1, 0, 1, 0x2a, 1);
        Transfer(s, b, 128, compare, tc: false); Assert.Equal(4, Result(b.Fdc)[2]);
    }

    [Fact]
    public void FormatConsumesChrnQuartetsAndPreservesNonSequentialIdsInOverlay()
    {
        var (s, b) = Create();
        var original = b.DriveA.Image!.Save();
        Send(b.Fdc, 0x4d, 0, 1, 2, 0x2a, 0x67);
        Transfer(s, b, 8, new byte[] { 4, 0, 9, 1, 4, 0, 5, 1 }, tc: false);
        Assert.Equal(new byte[] { 0, 0, 0, 4, 0, 6, 1 }, Result(b.Fdc));
        Assert.Equal(original, b.DriveA.Image.Save()); // unedited backing image; explicit formatted overlay
        Assert.Equal(new byte[] { 9, 5 }, b.DriveA.Track(0)!.Sectors.Select(x => x.R));
        Assert.All(b.DriveA.Track(0)!.Sectors.SelectMany(x => x.Data), x => Assert.Equal(0x67, x));
        Assert.Single(b.DriveA.FormattedTracks); Assert.Equal(2, b.DriveA.DirtySectors.Count);
        Send(b.Fdc, 0x46, 0, 4, 0, 5, 1, 9, 0x2a, 0xff);
        Assert.All(Transfer(s, b, 256), x => Assert.Equal(0x67, x)); Assert.Equal(0, Result(b.Fdc)[0]);
    }

    [Fact]
    public void MultiTrackAndReadTrackTraverseImageSectorsWithoutInventingDma()
    {
        var (s, b) = Create(new(80, 2, 2, 128));
        b.DriveA.Track(1)!.Sectors[1].Data.AsSpan().Fill(0x37);
        Send(b.Fdc, 0xc6, 0, 0, 0, 1, 0, 2, 0x2a, 128);
        var bytes = Transfer(s, b, 512);
        Assert.All(bytes[..384], x => Assert.Equal(0xe5, x)); Assert.All(bytes[384..], x => Assert.Equal(0x37, x));
        Assert.Equal(0, Result(b.Fdc)[1]);
        Send(b.Fdc, 0x42, 0, 0, 0, 1, 0, 2, 0x2a, 128);
        Assert.Equal(256, Transfer(s, b, 256, tc: false).Length); Assert.Equal(0x80, Result(b.Fdc)[1]);
    }

    [Fact]
    public void MissingMediaHasNoDataNotNotReadyBecauseReadyIsTiedHigh()
    {
        var s = new DeviceScheduler(); var b = new FloppyController(s, 10, new(new()), new(new()));
        b.WriteLatch(0x1a);
        Send(b.Fdc, 4, 0); Assert.Equal(0x38, Result(b.Fdc)[0]);
        Send(b.Fdc, 0x46, 0, 0, 0, 1, 2, 9, 0x2a, 0xff); AdvanceUntilReady(s, b.Fdc);
        var result = Result(b.Fdc); Assert.Equal(0x40, result[0]); Assert.Equal(4, result[1]);
    }

    [Fact]
    [Trait("hardware", "unverified")]
    public void MotorMonostableRetriggerSelectionJumperAndIndexUseAbsoluteTime()
    {
        var (s, b) = Create(); Assert.Equal(0xfc, b.ReadLatch()); Assert.True(b.DriveA.MotorOn);
        Assert.True(b.DriveA.IndexAt(0, b.Timing));
        Assert.False(b.DriveA.IndexAt(Ceil(17_734_475L * 3, 1000), b.Timing));
        Assert.True(b.DriveA.IndexAt(3_546_895, b.Timing));
        s.AdvanceTo(17_734_475); b.WriteLatch(0x12); b.WriteLatch(0x1a);
        s.AdvanceTo(30_148_608); Assert.True(b.MotorTimerRunning); // held D3 keeps the 555 trigger asserted: no expiry
        b.WriteLatch(0x14); Assert.False(b.DriveA.MotorOn); Assert.True(b.DriveB.MotorOn); // held, select B only
        // Release D3 (falling edge): the capacitor recharges and the 1.7 s discharge tail starts, due in absolute time.
        b.WriteLatch(0x04);
        s.AdvanceTo(30_148_608 + b.Timing.MotorTicks);
        Assert.False(b.MotorTimerRunning); Assert.Equal(0xfd, b.ReadLatch()); // tail expired, sense b0 reads 1
        var withJumper = new FloppyController(new(), 0, new(new()), new(new()), driveB80TrackJumper: true);
        Assert.Equal(0xff, withJumper.ReadLatch());
    }

    [Fact]
    public void ResetDuringDataCancelsByteDeadlinesWithoutResettingMasterClockOrMotor()
    {
        var (s, b) = Create();
        Send(b.Fdc, 0x46, 0, 0, 0, 1, 2, 9, 0x2a, 0xff); AdvanceUntilReady(s, b.Fdc);
        var now = s.Now; b.WriteLatch(2); b.WriteLatch(0x12);
        s.AdvanceTo(now + 100_000); Assert.Equal(0x80, b.Fdc.ReadStatus());
        Assert.True(b.DriveA.MotorOn); Assert.False(b.DriveA.HeadLoaded);
        Assert.DoesNotContain(s.PendingEvents, e => e.DeviceId == 10);
    }

    [Fact]
    public void ExportMediaIncludesFormattedTracksDeletedMarksAndUnchangedOtherTracks()
    {
        var (s, b) = Create(new(80, 2, 2, 128));
        Send(b.Fdc, 0x49, 0, 0, 0, 1, 0, 2, 0x2a, 128);
        Transfer(s, b, 128, Enumerable.Repeat((byte)0x31, 128).ToArray()); Result(b.Fdc);
        var exported = Dsk.Load(b.DriveA.ExportMedia());
        Assert.Equal(0x40, exported.Tracks[0].Sectors[0].ST2);
        Assert.All(exported.Tracks[0].Sectors[0].Data, x => Assert.Equal(0x31, x));
        Send(b.Fdc, 0x4d, 0, 1, 2, 0x2a, 0x67);
        Transfer(s, b, 8, new byte[] { 4, 0, 9, 1, 4, 0, 5, 1 }, tc: false); Result(b.Fdc);
        exported = Dsk.Load(b.DriveA.ExportMedia());
        Assert.Equal(new byte[] { 9, 5 }, exported.Tracks[0].Sectors.Select(x => x.R));
        Assert.All(exported.Tracks[0].Sectors.SelectMany(x => x.Data), x => Assert.Equal(0x67, x));
        Assert.All(exported.Tracks.Skip(1).SelectMany(t => t.Sectors).SelectMany(x => x.Data), x => Assert.Equal(0xe5, x));
    }

    [Fact]
    public void HeadUnloadBelongsToLoadedDriveEvenIfLatchSelectionChanges()
    {
        var (s, b) = Create();
        Send(b.Fdc, 0x46, 0, 0, 0, 1, 2, 9, 0x2a, 0xff);
        Transfer(s, b, 512); Result(b.Fdc);
        Assert.True(b.DriveA.HeadLoaded);
        b.WriteLatch(0x14);
        s.AdvanceTo(s.PendingEvents.Single(e => e.DeviceId == 10).DueTick);
        Assert.False(b.DriveA.HeadLoaded); Assert.False(b.DriveB.HeadLoaded);
    }

    [Fact]
    public void RecordedCrcErrorSurvivesDataTransferAndSignalsAbnormalTermination()
    {
        var image = Dsk.Create(new(80, 2, 1, 256)).Save(); image[256 + 28] = 0x20; image[256 + 29] = 0x20;
        var (s, b) = Create(image: Dsk.Load(image));
        Send(b.Fdc, 0x46, 0, 0, 0, 1, 1, 1, 0x2a, 0xff); Transfer(s, b, 256);
        Assert.Equal(new byte[] { 0x40, 0x20, 0x20, 0, 0, 1, 1 }, Result(b.Fdc));
    }

    [Fact]
    public void DiskChangedAndMechanicalBoundsAreIndependentOfFdcLogicalCylinder()
    {
        var drive = new FloppyDrive(new(40, 1), Dsk.Create(new(40, 1, 9, 512)));
        Assert.True(drive.DiskChanged); drive.Step(-1); Assert.Equal(0, drive.HeadPosition);
        Assert.False(drive.DiskChanged);
        for (var i = 0; i < 80; i++) drive.Step(1);
        Assert.Equal(39, drive.HeadPosition); Assert.Null(drive.Track(1));
        drive.Mount(null); Assert.True(drive.DiskChanged); Assert.Null(drive.Track(0));
    }

    [Fact]
    public void ScanTerminalCountStopsAfterComparedByteWithoutRequiringRemainingCpuBytes()
    {
        var (s, b) = Create();
        Send(b.Fdc, 0x51, 0, 0, 0, 1, 2, 9, 0x2a, 1); AdvanceUntilReady(s, b.Fdc);
        b.Fdc.WriteData(0xe5); b.WriteLatch(0x1b);
        Assert.Equal(new byte[] { 0, 0, 8, 0, 0, 1, 2 }, Result(b.Fdc));
        Assert.DoesNotContain(s.PendingEvents, e => e.DeviceId == 10 && e.Kind != 5);
    }

    [Fact]
    public void RepeatedImmediateErrorsReplaceRatherThanAccumulateHeadUnloadTimers()
    {
        var (s, b) = Create();
        Send(b.Fdc, 0x46, 0, 0, 0, 1, 2, 9, 0x2a, 0xff); Transfer(s, b, 512); Result(b.Fdc);
        b.DriveA.WriteProtected = true;
        for (var i = 0; i < 2; i++)
        {
            Send(b.Fdc, 0x45, 0, 0, 0, 1, 2, 9, 0x2a, 0xff); Assert.Equal(2, Result(b.Fdc)[1]);
        }
        Assert.Single(s.PendingEvents, e => e.DeviceId == 10);
    }

    private static long Ceil(long numerator, long denominator) => (numerator + denominator - 1) / denominator;
    internal static (DeviceScheduler Scheduler, FloppyController Board) Create(DiskGeometry? geometry = null,
        Dsk? image = null, FdcTiming? timing = null)
    {
        var s = new DeviceScheduler();
        var b = new FloppyController(s, 10, new(new(), image ?? Dsk.Create(geometry ?? new(80, 2, 9, 512))),
            new(new(), Dsk.Create(new(80, 2, 9, 512))), timing);
        b.WriteLatch(0x1a); Send(b.Fdc, 3, 0xdf, 1);
        return (s, b);
    }
    internal static void DrainReset(Fdc8272 fdc)
    {
        for (var i = 0; i < 4; i++) { Send(fdc, 8); Result(fdc); }
    }
    internal static byte[] Transfer(DeviceScheduler s, FloppyController b, int count, byte[]? input = null, bool tc = true)
    {
        var output = new byte[count];
        for (var i = 0; i < count; i++)
        {
            AdvanceUntilReady(s, b.Fdc); Assert.Equal(FdcPhase.Execution, b.Fdc.Phase);
            if (input is null) output[i] = b.Fdc.ReadData(); else b.Fdc.WriteData(input[i]);
        }
        if (tc) { b.WriteLatch((byte)(b.Latch | 1)); b.WriteLatch((byte)(b.Latch & 0xfe)); }
        AdvanceUntilReady(s, b.Fdc); Assert.Equal(FdcPhase.Result, b.Fdc.Phase);
        return output;
    }

    internal static void Send(Fdc8272 fdc, params byte[] bytes)
    {
        foreach (var b in bytes)
        {
            Assert.Equal(0x80, fdc.ReadStatus() & 0xc0);
            fdc.WriteData(b);
        }
    }
    internal static byte[] Result(Fdc8272 fdc)
    {
        var result = new List<byte>();
        while ((fdc.ReadStatus() & 0xd0) == 0xd0)
        {
            Assert.Equal(0, fdc.ReadStatus() & 0x20);
            result.Add(fdc.ReadData());
        }
        return result.ToArray();
    }
    internal static void AdvanceUntilReady(DeviceScheduler scheduler, Fdc8272 fdc)
    {
        for (var i = 0; (fdc.ReadStatus() & 0x80) == 0 && i < 1000; i++)
        {
            long? due = scheduler.PendingEvents.Count > 0 ? (long?)scheduler.PendingEvents.Min(e => e.DueTick) : null;
            scheduler.AdvanceTo(due ?? scheduler.Now + 1_000_000);
        }
        Assert.NotEqual(0, fdc.ReadStatus() & 0x80);
    }
}
