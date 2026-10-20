using System.Collections.Immutable;
using System.Text;
using Hc2000.App.Input;
using Hc2000.App.Snapshots;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Snapshots;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Owner-side snapshot path: the public Machine.Save/Load API (04g) reached through
/// the shared input queue, InputController boundary dispatch, HcsSession and HcsBridge.
/// Load is all-or-nothing onto a fresh machine; a failure leaves the machine untouched.</summary>
public sealed class HcsSessionTests
{
    [Fact]
    public async Task SaveAndLoadTravelThroughTheSharedQueueAtTheBoundary()
    {
        var wiring = HcsMachineFixture.Compose();
        var queue = new InputQueue();
        var bridge = new HcsBridge(queue);
        var session = new HcsSession(wiring.Machine);
        var controller = new InputController(queue, snapshot: (command, _) => bridge.Publish(session.Apply(command)));

        var request = bridge.RequestAsync(new HcsCommand(HcsAction.Save));
        controller.ApplyPending(0, new KeyboardMatrix());
        var reply = await request;
        Assert.True(reply.Applied);
        Assert.Null(reply.Error);
        Assert.False(reply.Snapshot.IsDefaultOrEmpty);

        var fresh = HcsMachineFixture.Compose();
        fresh.Machine.Load(reply.Snapshot.ToArray());
        Assert.Equal(wiring.Machine.MasterTick, fresh.Machine.MasterTick);
        Assert.Equal(wiring.Cpu.Capture(), fresh.Cpu.Capture());
        // Deterministic: a re-save of the fresh machine reproduces the same document.
        Assert.Equal(wiring.Machine.Save(), fresh.Machine.Save());
    }

    [Fact]
    public void SaveCapturesStateAtTheBoundaryAndLoadRestoresItOntoAFreshMachine()
    {
        var a = HcsMachineFixture.Compose();
        a.Machine.RunFrame();
        var sessionA = new HcsSession(a.Machine);
        var savedTick = a.Machine.MasterTick;
        var cpuBefore = a.Cpu.Capture();
        var save = sessionA.Apply(new HcsCommand(HcsAction.Save));
        Assert.True(save.Applied);
        Assert.Null(save.Error);
        a.Machine.RunFrame();

        var b = HcsMachineFixture.Compose();
        var load = new HcsSession(b.Machine).Apply(new HcsCommand(HcsAction.Load, save.Snapshot));
        Assert.True(load.Applied);
        Assert.Null(load.Error);
        Assert.Equal(savedTick, b.Machine.MasterTick); // the frame captured at Save time
        Assert.Equal(cpuBefore, b.Cpu.Capture());
    }

    [Fact]
    public void LoadOfGarbageFailsAndLeavesTheFreshMachineUntouched()
    {
        var b = HcsMachineFixture.Compose();
        var session = new HcsSession(b.Machine);
        var fail = session.Apply(new HcsCommand(HcsAction.Load, ImmutableArray.CreateRange(Encoding.UTF8.GetBytes("not a snapshot"))));
        Assert.False(fail.Applied);
        Assert.NotNull(fail.Error);
        Assert.Equal(0, b.Machine.MasterTick);

        // The machine is still fresh: the next valid Load succeeds.
        var a = HcsMachineFixture.Compose();
        a.Machine.RunFrame();
        var ok = session.Apply(new HcsCommand(HcsAction.Load, ImmutableArray.CreateRange(a.Machine.Save())));
        Assert.True(ok.Applied);
        Assert.Null(ok.Error);
        Assert.Equal(a.Machine.MasterTick, b.Machine.MasterTick);
    }

    [Fact]
    public void LoadOfATamperedFileFailsAndLeavesTheFreshMachineUntouched()
    {
        var a = HcsMachineFixture.Compose();
        a.Machine.RunFrame();
        var file = a.Machine.Save();
        using (var input = new MemoryStream(file))
        {
            var doc = HcsCodec.Read(input, new MachineSchema(a.Profile));
            var sha = Encoding.UTF8.GetBytes(HcsMachineFixture.AssetSha(doc, "ram"));
            var flipped = (byte[])sha.Clone();
            flipped[0] = (byte)(sha[0] == 'a' ? 'b' : 'a'); // valid hex, different value, same length
            file = HcsMachineFixture.PatchAndRehash(file, sha, flipped);
        }

        var b = HcsMachineFixture.Compose();
        var session = new HcsSession(b.Machine);
        var fail = session.Apply(new HcsCommand(HcsAction.Load, ImmutableArray.CreateRange(file)));
        Assert.False(fail.Applied);
        Assert.NotNull(fail.Error);
        Assert.Equal(0, b.Machine.MasterTick);
        var ok = session.Apply(new HcsCommand(HcsAction.Load, ImmutableArray.CreateRange(a.Machine.Save())));
        Assert.True(ok.Applied);
    }

    [Fact]
    public void SaveReportsUnsupportedStateWhenTheFdcIsBusy()
    {
        var a = HcsMachineFixture.Compose();
        a.Machine.RunFrame();
        a.Floppy.WriteLatch(0x1a); // de-assert D4 reset; the FDC is between commands.
        a.Floppy.Fdc.WriteData(3); // begin Specify and stop mid-command: genuinely non-idle FDC state.
        Assert.Equal(FdcPhase.Command, a.Floppy.Fdc.Phase);
        var session = new HcsSession(a.Machine);
        var reply = session.Apply(new HcsCommand(HcsAction.Save));
        Assert.False(reply.Applied);
        Assert.Contains("StateUnsupported", reply.Error);
    }

    [Fact]
    public void TheQueueRejectsInvalidSnapshotCommands()
    {
        var queue = new InputQueue();
        Assert.Throws<ArgumentException>(() => queue.Enqueue(new HcsCommand(HcsAction.Save, Bytes: [1u > 0 ? (byte)1 : (byte)0])));
        Assert.Throws<ArgumentException>(() => queue.Enqueue(new HcsCommand(HcsAction.Load)));
        Assert.Throws<ArgumentException>(() => queue.Enqueue(new HcsCommand((HcsAction)99)));
        Assert.True(queue.Enqueue(new HcsCommand(HcsAction.Save)) > 0);
    }
}
