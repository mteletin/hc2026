using System.Collections.Immutable;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.App.Snapshots;
using Hc2000.Machine;
using Hc2000.Machine.Floppy;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class HddCompositionTests
{
    // Authored NOP ROM: production composition regression, NOT BIOS/guest HDD proof.
    private static (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) Roms() =>
        (MachineProfile.Hc2000V2, new byte[16384], new byte[16384], new byte[16384]);
    private static HddInitialMedia Image(byte drive = 2) => new(drive,
        ImmutableArray.CreateRange(new byte[HddMediaAdapter.ImageLength]), "authored.raw", WriteProtected: true);

    [Fact]
    public void ColdFactoryOwnsCopiesAndPopulatesAllSlotsBeforeAnyInstruction()
    {
        var source = new byte[HddMediaAdapter.ImageLength]; source[0] = 0x5a;
        var prepared = BasicMachineComposition.PrepareHddWholeChipV2(Roms(),
            [new(2, ImmutableArray.CreateRange(source)), Image(3), Image(4), Image(5)]);
        source[0] = 0xa5;
        BlockDiskPort? hdd = null;
        var machine = BasicMachineComposition.CreateColdNewMachine(prepared, new(), new(new()), new(new()), _ => { }, value => hdd = value);
        Assert.Equal(0, machine.MasterTick);
        Assert.NotNull(hdd);
        Assert.Equal((byte)0x5a, hdd.CaptureHdd(2).Bytes[0]);
        for (byte drive = 2; drive <= 5; drive++)
        {
            var state = hdd.CaptureHdd(drive);
            Assert.True(state.Present); Assert.Equal(1u, state.Generation); Assert.False(state.Dirty);
            Assert.Equal(drive != 2, state.WriteProtected);
        }
    }

    [Fact]
    public void InvalidInitialInputsRejectedBeforeWorkerConstruction()
    {
        Assert.Throws<ArgumentException>(() => BasicMachineComposition.PrepareHddWholeChipV2(Roms(), [new(2, [])]));
        Assert.Throws<ArgumentException>(() => BasicMachineComposition.PrepareHddWholeChipV2(Roms(), [Image(1)]));
        Assert.Throws<ArgumentException>(() => BasicMachineComposition.PrepareHddWholeChipV2(Roms(), [Image(), Image()]));
        Assert.False(LaunchOptions.Parse([]).Options!.HddEnabled);
        Assert.True(LaunchOptions.Parse(["--hdd"]).Options!.HddEnabled);
        Assert.False(LaunchOptions.Parse(["--hdd", "--start"]).Ok);
    }

    [Fact]
    public void ExplicitEmptySavePayloadIsRejectedBeforeOwnerBoundary()
    {
        var queue = new InputQueue();
        Assert.Throws<ArgumentException>(() => queue.Enqueue(new HcsCommand(HcsAction.Save, ImmutableArray<byte>.Empty)));
        Assert.Equal(0, queue.BoundaryCount);
    }

    [Fact]
    public async Task WorkerResetRetainsIdentityAndHcsAndLiveAttachFailClosed()
    {
        var prepared = BasicMachineComposition.PrepareHddWholeChipV2(Roms(), [Image()]);
        var queue = new InputQueue(); var media = new MediaBridge(queue); var hcs = new HcsBridge(queue);
        using var worker = BasicMachineComposition.CreateWorker(prepared, queue, media, hcs);
        async Task<MediaReply> Apply(HddRequest request) =>
            await media.RequestAsync(new(MediaAction.Hdd, Hdd: request)).WaitAsync(TimeSpan.FromSeconds(10));
        var before = (await Apply(new(HddAction.Inspect))).Hdd!;
        Assert.Null(before.Error); Assert.True(before.Drives[0].WriteProtected);
        queue.Enqueue(new RuntimeControl(InputControlKind.Reset));
        var reset = (await Apply(new(HddAction.Inspect))).Hdd!;
        Assert.Equal(before.Drives.ToArray(), reset.Drives.ToArray()); // Reset retained logical media; initial attach was NOT replayed.
        Assert.Equal(HddMediaAdapter.SwitchUnavailable, (await Apply(new(HddAction.Attach))).Error);
        // Detached, authored non-HDD snapshot: Load must be a valid request to reach
        // the HDD owner's refusal boundary, rather than fail host queue validation.
        var loadBytes = ImmutableArray.CreateRange(HcsMachineFixture.Compose().Machine.Save());
        foreach (var action in new[] { HcsAction.Save, HcsAction.Load })
        {
            var bytes = action == HcsAction.Load ? loadBytes : default;
            var refused = await hcs.RequestAsync(new(action, bytes, DiscardChanges: true)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(refused.Applied); Assert.Contains("HDD logical media", refused.Error);
            var retained = (await Apply(new(HddAction.Inspect))).Hdd!;
            Assert.Null(retained.Error);
            Assert.Equal(reset.Drives.ToArray(), retained.Drives.ToArray());
        }
        worker.Dispose(); await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
