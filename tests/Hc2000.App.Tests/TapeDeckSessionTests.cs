using System.Collections.Immutable;
using Hc2000.App.Media;
using Hc2000.App.Input;
using Hc2000.App.Runtime;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Snapshots;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class TapeDeckSessionTests
{
    private static (TapeTransport Tape, MediaSession Session) Fixture(bool dirty = false)
    {
        var f = new RuntimeTests.Fixture(); var tape = f.Machine.EnableTape(3);
        var session = new MediaSession(new(new()), new(new()), tape, manualDeck: true);
        Assert.Null(session.Apply(new(MediaAction.BlankTape)).Error);
        if (dirty)
        {
            var saved = tape.Capture().Value;
            Assert.True(tape.Restore(saved with { Component = saved.Component with
                { Dirty = [true, true], EditRevision = ["1", "2"], SavedRevision = ["0", "0"] } }).Success);
        }
        return (tape, session);
    }

    [Fact]
    public void CopiedObservationAndInterlocksNeverArmRecorderOrAutoplay()
    {
        var (tape, session) = Fixture();
        var initial = session.Apply(new(MediaAction.Inspect)).Tape!.Deck!;
        // The recorder is in: a blank side with its tab in may record; REC is refused while the tab is out (spec 10).
        Assert.Equal(TapeDeckMode.Stopped, initial.Mode); Assert.True(initial.Capabilities.CanRecord);
        Assert.Null(session.Apply(new(MediaAction.TabTape, Value: false)).Error);
        var tabOut = session.Apply(new(MediaAction.Inspect)).Tape!.Deck!;
        var refused = session.Apply(new(MediaAction.RecordTape));
        Assert.NotNull(refused.TapeFailure); Assert.Equal(tabOut, refused.Tape!.Deck);
        Assert.Null(session.Apply(new(MediaAction.TabTape, Value: true)).Error);
        Assert.Null(session.Apply(new(MediaAction.PlayTape)).Error);
        Assert.Equal(TapeDeckMode.Stopped, initial.Mode); // the old observation is detached
        Assert.Equal(TapeError.Interlock, session.Apply(new(MediaAction.FlipTape)).TapeFailure!.Code);
        Assert.Equal(TapeError.Interlock, session.Apply(new(MediaAction.ExportTape)).TapeFailure!.Code);
        Assert.Null(session.Apply(new(MediaAction.PauseTape)).Error);
        Assert.True(tape.Deck.Observe().Paused);
        Assert.Null(session.Apply(new(MediaAction.StopEjectTape)).Error);
        Assert.True(tape.Deck.Observe().Present); Assert.False(tape.Deck.Observe().Paused);
        Assert.Null(session.Apply(new(MediaAction.TabTape, Value: false)).Error);
        Assert.False(tape.Deck.Observe().A.TabPresent);
        Assert.Null(session.Apply(new(MediaAction.FlipTape)).Error);
        Assert.Equal(TapeSide.B, tape.Deck.Observe().ActiveSide);
        Assert.Null(session.Apply(new(MediaAction.CounterTape)).Error);
        Assert.Equal(0u, tape.Deck.Observe().Counter);
    }

    [Fact]
    public void DirtyEjectCancelAndInvalidImportPreserveBothSidesAndName()
    {
        var (tape, session) = Fixture(dirty: true); var before = tape.Deck.Observe();
        Assert.Equal(TapeError.DirtyConfirmationRequired,
            session.Apply(new(MediaAction.StopEjectTape)).TapeFailure!.Code);
        Assert.Equal(TapeError.DirtyConfirmationRequired,
            session.Apply(new(MediaAction.BlankTape, Capacity: TapeCapacity.C90)).TapeFailure!.Code);
        Assert.NotNull(session.Apply(new(MediaAction.QueueTape, Bytes: ImmutableArray.Create<byte>(2, 0, 1))).Error);
        Assert.Equal(before, tape.Deck.Observe());
        Assert.Equal("Blank C60", session.Apply(new(MediaAction.Inspect)).Tape!.Name);
        Assert.Null(session.Apply(new(MediaAction.StopEjectTape, DiscardChanges: true)).Error);
        Assert.False(tape.Deck.Observe().Present);
    }

    [Fact]
    public async Task DurableExactSaveAcknowledgesOnlyCorrelatedSideAndFailedCopyAcknowledgesNothing()
    {
        using var nativeDomain = new HddColdAdmissionTests.PrivateScopedDomain();
        await using var owner = new OwnedSession();
        var snapshot = (await owner.Request(new(MediaAction.ExportTape))).TapeExport!;
        var registry = new TapeCodecRegistry([new Writer(TapeSignalFidelity.ExactSignal, TapeByteFidelity.Transformed)]);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tzx");
        try
        {
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => TapeFiles.SaveAsync(Path.Combine(path, "no-dir", "failed.tzx"), snapshot, registry, new(TapeFormat.Tzx, TapeSide.A), owner.Request));
            var failed = (await owner.Request(new(MediaAction.Inspect))).Tape!.Deck!;
            Assert.True(failed.A.Dirty); Assert.True(failed.B.Dirty);
            await TapeFiles.SaveAsync(path, snapshot, registry, new(TapeFormat.Tzx, TapeSide.A), owner.Request);
            var saved = (await owner.Request(new(MediaAction.Inspect))).Tape!.Deck!;
            Assert.False(saved.A.Dirty); Assert.True(saved.B.Dirty);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(TapeSignalFidelity.Quantized)]
    [InlineData(TapeSignalFidelity.DataOnly)]
    public async Task QuantizedOrDataOnlyCopyDoesNotClearDirty(TapeSignalFidelity signal)
    {
        using var nativeDomain = new HddColdAdmissionTests.PrivateScopedDomain();
        await using var owner = new OwnedSession();
        var snapshot = (await owner.Request(new(MediaAction.ExportTape))).TapeExport!;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tzx");
        try
        {
            var acks = 0;
            await TapeFiles.SaveAsync(path, snapshot, new TapeCodecRegistry([new Writer(signal, TapeByteFidelity.Transformed)]),
                new(TapeFormat.Tzx, TapeSide.A) { AllowQuantize = true }, command => { acks++; return owner.Request(command); });
            Assert.Equal(0, acks); Assert.True((await owner.Request(new(MediaAction.Inspect))).Tape!.Deck!.A.Dirty);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task StaleSnapshotWriteCannotClearReplacementMediaDirtyState()
    {
        using var nativeDomain = new HddColdAdmissionTests.PrivateScopedDomain();
        await using var owner = new OwnedSession();
        var snapshot = (await owner.Request(new(MediaAction.ExportTape))).TapeExport!;
        await owner.Request(new(MediaAction.BlankTape, DiscardChanges: true, Name: "#seed-dirty"));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tzx");
        try
        {
            await TapeFiles.SaveAsync(path, snapshot, new TapeCodecRegistry([new Writer(TapeSignalFidelity.ExactSignal, TapeByteFidelity.Transformed)]),
                new(TapeFormat.Tzx, TapeSide.A), owner.Request);
            Assert.True((await owner.Request(new(MediaAction.Inspect))).Tape!.Deck!.A.Dirty); Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    private sealed class OwnedSession : IAsyncDisposable
    {
        private readonly MediaBridge bridge;
        private readonly EmulationWorker worker;
        public OwnedSession()
        {
            var queue = new InputQueue(); bridge = new(queue);
            InputController? input = null;
            worker = new(() =>
            {
                var f = new RuntimeTests.Fixture(); var tape = f.Machine.EnableTape(3);
                var session = new MediaSession(new(new()), new(new()), tape, manualDeck: true);
                session.Apply(new(MediaAction.BlankTape)); Seed();
                input = new(queue, media: (command, _) =>
                {
                    var reply = session.Apply(command);
                    if (command.Name == "#seed-dirty") { Seed(); reply = session.Apply(new(MediaAction.Inspect, RequestId: command.RequestId)); }
                    bridge.Publish(reply);
                });
                return f.Machine;
                void Seed()
                {
                    var s = tape.Capture().Value;
                    Assert.True(tape.Restore(s with { Component = s.Component with
                        { Dirty = [true, true], EditRevision = ["1", "2"], SavedRevision = ["0", "0"] } }).Success);
                }
            }, context => { context.Pause(); input!.ApplyPending(context.Machine.MasterTick, context.Machine.Keyboard); }, cleanup: _ => bridge.Disconnect());
        }
        public Task<MediaReply> Request(MediaCommand command) => bridge.RequestAsync(command).WaitAsync(TimeSpan.FromSeconds(10));
        public async ValueTask DisposeAsync() { worker.Dispose(); await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private sealed class Writer(TapeSignalFidelity signal, TapeByteFidelity bytes) : ITapeCodec
    {
        public TapeFormat Format => TapeFormat.Tzx;
        public TapeCodecCapabilities Capabilities => new(false, true, null);
        public TapeResult<TapeReadResult> Read(ReadOnlySpan<byte> bytes, TapeImportOptions options) => throw new NotSupportedException();
        public TapeResult<TapeWriteResult> Write(TapeSideImage image, TapeExportOptions options) => TapeResult<TapeWriteResult>.Ok(new([1, 2, 3],
            new(TapeFormat.Tzx, signal, bytes, TapeAmplitudeFidelity.NotApplicable, null, default, 0, [])));
    }
}
