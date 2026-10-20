using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Text.Json;
using Hc2000.App.Snapshots;
using Hc2000.Machine.Snapshots;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.Machine;
using Hc2000.Machine.Tape;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class TapeDeckCompositionTests
{
    [Fact]
    public async Task RealCompositionHasOneManualTapeAndResetRetainsItsCursor()
    {
        // Authored NOP ROM exercises production composition, not test CPU boot injection.
        var prepared = BasicMachineComposition.PrepareWholeChipV2((MachineProfile.Hc2000V2,
            new byte[16384], new byte[16384], new byte[16384]));
        var queue = new InputQueue(); var bridge = new MediaBridge(queue);
        using var worker = BasicMachineComposition.CreateWorker(prepared, queue, bridge);
        var initial = await bridge.RequestAsync(new(MediaAction.Inspect)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(initial.Tape);
        Assert.Equal(TapeState.Unloaded, initial.Tape.Cursor.State);
        var inserted = await bridge.RequestAsync(new(MediaAction.QueueTape,
            Bytes: ImmutableArray.Create<byte>(2, 0, 0xff, 0xff), Name: "authored.tap"));
        Assert.Null(inserted.Error);
        Assert.Equal(TapeState.Stopped, inserted.Tape!.Cursor.State);
        var before = inserted.Tape.Cursor;
        queue.Enqueue(new RuntimeControl(InputControlKind.Reset));
        var reset = await bridge.RequestAsync(new(MediaAction.Inspect)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(reset.Error); Assert.NotNull(reset.Tape);
        Assert.Equal("authored.tap", reset.Tape.Name);
        Assert.Equal(before.State, reset.Tape.Cursor.State);
        Assert.Equal(before.MediaIdentity, reset.Tape.Cursor.MediaIdentity);
        Assert.Equal(before.BlockIndex, reset.Tape.Cursor.BlockIndex);
        worker.Dispose(); await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ResetRetainsWindingSideTabCounterAndHeadAtFreshCpuEpoch()
    {
        var prepared = BasicMachineComposition.PrepareWholeChipV2((MachineProfile.Hc2000V2,
            new byte[16384], new byte[16384], new byte[16384]));
        var queue = new InputQueue(); var media = new MediaBridge(queue); var hcs = new HcsBridge(queue);
        using var worker = BasicMachineComposition.CreateWorker(prepared, queue, media, hcs);
        async Task<MediaReply> Apply(MediaCommand command) => await media.RequestAsync(command).WaitAsync(TimeSpan.FromSeconds(10));
        queue.Enqueue(new RuntimeControl(InputControlKind.Pause));
        Assert.Null((await Apply(new(MediaAction.BlankTape))).Error);
        await Apply(new(MediaAction.TabTape, Value: false));
        await Apply(new(MediaAction.FastWindTape, Value: true));
        await Apply(new(MediaAction.FlipTape));
        await Apply(new(MediaAction.CounterTape));
        Assert.Null((await Apply(new(MediaAction.RewindTape))).Error);
        _ = worker.TakeLatestFrame(); // Discard any frame published before winding began.
        queue.Enqueue(new RuntimeControl(InputControlKind.Resume));
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (worker.TakeLatestFrame() is null) await Task.Delay(2, deadline.Token);
        queue.Enqueue(new RuntimeControl(InputControlKind.Pause));
        var before = (await Apply(new(MediaAction.Inspect))).Tape!.Deck!;
        Assert.Equal(TapeDeckMode.Rewinding, before.Mode); Assert.Equal(TapeSide.B, before.ActiveSide);
        Assert.True(before.Position < before.Duration);
        var saved = await hcs.RequestAsync(new(HcsAction.Save)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(saved.Applied, saved.Error); Assert.True(Cpu(saved).TStates > 0);
        queue.Enqueue(new RuntimeControl(InputControlKind.Reset));
        queue.Enqueue(new RuntimeControl(InputControlKind.Pause));
        var after = (await Apply(new(MediaAction.Inspect))).Tape!.Deck!;
        Assert.Equal(before with { MicLevel = false }, after);
        var fresh = await hcs.RequestAsync(new(HcsAction.Save)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fresh.Applied, fresh.Error); Assert.Equal(0, Cpu(fresh).TStates); Assert.Equal((ushort)0, Cpu(fresh).Pc);
        worker.Dispose(); await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ProductionSnapshotLoadAdoptsDetachedRealCompositionAndFailureChangesNothing()
    {
        var prepared = BasicMachineComposition.PrepareWholeChipV2((MachineProfile.Hc2000V2,
            new byte[16384], new byte[16384], new byte[16384]));
        var queue = new InputQueue(); var media = new MediaBridge(queue); var hcs = new HcsBridge(queue);
        using var worker = BasicMachineComposition.CreateWorker(prepared, queue, media, hcs);
        async Task<MediaReply> Apply(MediaCommand command) => await media.RequestAsync(command).WaitAsync(TimeSpan.FromSeconds(10));
        queue.Enqueue(new RuntimeControl(InputControlKind.Pause));
        await Apply(new(MediaAction.BlankTape, Capacity: TapeCapacity.C90));
        var original = (await Apply(new(MediaAction.FlipTape))).Tape!.Deck!;
        var saved = await hcs.RequestAsync(new(HcsAction.Save)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(saved.Applied, saved.Error);
        await Apply(new(MediaAction.FlipTape)); await Apply(new(MediaAction.TabTape, Value: false));
        var before = (await Apply(new(MediaAction.Inspect))).Tape!.Deck!;
        var rejected = await hcs.RequestAsync(new(HcsAction.Load, ImmutableArray.Create<byte>(1, 2, 3))).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(rejected.Applied); Assert.NotNull(rejected.Error);
        Assert.Equal(before, (await Apply(new(MediaAction.Inspect))).Tape!.Deck);
        var loaded = await hcs.RequestAsync(new(HcsAction.Load, saved.Snapshot)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(loaded.Applied, loaded.Error);
        queue.Enqueue(new RuntimeControl(InputControlKind.Pause));
        Assert.Equal(original, (await Apply(new(MediaAction.Inspect))).Tape!.Deck);
        var again = await hcs.RequestAsync(new(HcsAction.Save)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Cpu(saved), Cpu(again));
        worker.Dispose(); await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static CpuLogicalState Cpu(HcsReply reply)
    {
        // Independent framing extraction for assertions on the real adapter, not a CPU boot helper.
        var bytes = reply.Snapshot.ToArray();
        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
        using var manifest = JsonDocument.Parse(bytes.AsMemory(32, length));
        var component = Assert.Single(manifest.RootElement.GetProperty("components").EnumerateArray(),
            c => c.GetProperty("id").GetString() == "tape");
        Assert.Equal(2u, component.GetProperty("modelVersion").GetUInt32());
        return CpuLogicalState.FromJson(manifest.RootElement.GetProperty("cpu"));
    }
}
