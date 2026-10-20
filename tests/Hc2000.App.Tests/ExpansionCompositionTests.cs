using System.Collections.Immutable;
using Hc2000.App.Configuration;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.App.Snapshots;
using Hc2000.Machine;
using Hc2000.Machine.Snapshots;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.App.Tests;

[CollectionDefinition("expansion-app", DisableParallelization = true)]
public sealed class ExpansionAppCollection;

[Collection("expansion-app")]
public sealed class ExpansionCompositionTests
{
    [Fact]
    public void ExpansionIsAnExplicitConfigurationWithoutChangingFaithfulDefaults()
    {
        var baseline = new MachineConfiguration(); baseline.ValidateEffective();
        var expansion = baseline with { Profile = "hc2000-v2-paged-expansion-v1" };
        expansion.ValidateEffective();
        Assert.Equal("hc2000-v2", new MachineConfiguration().Profile);
        Assert.Throws<ArgumentException>(() => (baseline with { Profile = "hc2000-v2-paged-m-transfer-v1" }).ValidateEffective());
        Assert.Throws<ArgumentException>(() => (expansion with { RomSet = "unverified" }).ValidateEffective());
    }

    [Fact]
    public void PolicyIsAdmittedOnceBeforeActivationAndNeverRaisesAnExplicitLowerCap()
    {
        var roms = ProfileRomSelection.LoadWholeChipV2(RomSelectionFixture.RepositoryRoot());
        var expansion = (MachineProfile.Hc2000V2PagedExpansionV1, roms.Basic, roms.Cpm, roms.If1);
        var lower = HcsWavPolicy.Default with { TransientBytes = 16L * 0x44000000 - 1 };
        Assert.Equal(HcsError.LimitExceeded, Assert.Throws<HcsException>(() =>
            BasicMachineComposition.PrepareWholeChipV2(expansion, lower)).Error);
        var explicitPolicy = HcsWavPolicy.Default with { TransientBytes = 16L * 0x44000000 };
        var prepared = BasicMachineComposition.PrepareWholeChipV2(expansion, explicitPolicy);
        Assert.Same(explicitPolicy, prepared.SnapshotPolicy);
        Assert.Same(HcsWavPolicy.Default, BasicMachineComposition.PrepareWholeChipV2(roms).SnapshotPolicy);
        Assert.Equal(256 * 1024 * 1024, prepared.SnapshotPolicy.MaxInputBytes);
        Assert.Equal(4L * 1024 * 1024 * 1024, HcsWavPolicy.Default.TransientBytes);
    }

    [Fact]
    public async Task RealExpansionWorkerRetainsTheOwnerTokenFreezeBeforeSnapshotPreflight()
    {
        var roms = ProfileRomSelection.LoadWholeChipV2(RomSelectionFixture.RepositoryRoot());
        var prepared = BasicMachineComposition.PrepareWholeChipV2((MachineProfile.Hc2000V2PagedExpansionV1, roms.Basic, roms.Cpm, roms.If1));
        var queue = new InputQueue(); var media = new MediaBridge(queue); var snapshots = new HcsBridge(queue);
        var ledger = new DiskWritebackOwnerLedger();
        queue.Enqueue(new RuntimeControl(InputControlKind.Pause));
        using var worker = BasicMachineComposition.CreateWorker(prepared, queue, media, snapshots, false, ledger);
        async Task<MediaReply> Apply(MediaCommand command) => await media.RequestAsync(command).WaitAsync(TimeSpan.FromSeconds(15));
        var disk = ImmutableArray.CreateRange(Dsk.Create(new(40, 1, 1, 128)).Save());
        Assert.Null((await Apply(new(MediaAction.InsertDisk, Bytes: disk, Name: "authored.dsk"))).Error);
        var frozen = (await Apply(new(MediaAction.Writeback, Writeback: new(WritebackOwnerOp.Begin, "A")))).Writeback!;
        Assert.True(frozen.Accepted, frozen.Error); Assert.True(frozen.SessionFrozen);
        try
        {
            foreach (var action in new[] { HcsAction.Save, HcsAction.Load })
            {
                var bytes = action == HcsAction.Load ? ImmutableArray.Create<byte>(1) : default;
                var refused = await snapshots.RequestAsync(new(action, bytes)).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.False(refused.Applied); Assert.Equal("Writeback freeze: snapshot refused.", refused.Error);
                Assert.True(ledger.Frozen);
            }
        }
        finally
        {
            var aborted = (await Apply(new(MediaAction.Writeback,
                Writeback: new(WritebackOwnerOp.Abort, Token: frozen.Snapshot!.Token)))).Writeback!;
            Assert.True(aborted.Accepted, aborted.Error);
            worker.Dispose(); await worker.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    [Fact]
    public async Task ProductionSessionRoundTripUsesTheExistingSaveLoadAndRetainsTheNextRealFrame()
    {
        // Full GiB arena and all production copies: run only on an admitted full-suite host.
        var roms = ProfileRomSelection.LoadWholeChipV2(RomSelectionFixture.RepositoryRoot());
        var prepared = await StartupHardwarePreparation.PrepareAsync(roms, new() { Profile = "hc2000-v2-paged-expansion-v1" });
        var machine = BasicMachineComposition.CreateColdNewMachine(prepared, new(), new(new()), new(new()), _ => { }, _ => { });
        var session = new HcsSession(machine);
        var saved = session.Apply(new(HcsAction.Save));
        Assert.True(saved.Applied, saved.Error); Assert.True(saved.Snapshot.Length > 0x40010000);
        var expected = machine.RunFrame();
        // The existing loader adopts only onto a fresh composition, as the App worker does.
        var candidate = BasicMachineComposition.CreateColdNewMachine(prepared, new(), new(new()), new(new()), _ => { }, _ => { });
        var restored = new HcsSession(candidate).Apply(new(HcsAction.Load, saved.Snapshot));
        Assert.True(restored.Applied, restored.Error); Assert.Equal(0, candidate.MasterTick);
        var actual = candidate.RunFrame();
        Assert.Equal(expected.MasterTick, actual.MasterTick);
        Assert.True(expected.Pixels.Span.SequenceEqual(actual.Pixels.Span));
    }

    [Fact]
    public void ExplicitWholeChipCompositionAdmitsMetadataAndReachesTheExistingHcsSession()
    {
        var roms = ProfileRomSelection.LoadWholeChipV2(RomSelectionFixture.RepositoryRoot());
        var prepared = BasicMachineComposition.PrepareWholeChipV2((MachineProfile.Hc2000V2PagedExpansionV1, roms.Basic, roms.Cpm, roms.If1));
        var machine = BasicMachineComposition.CreateColdNewMachine(prepared, new(), new(new()), new(new()), _ => { }, _ => { });
        machine.ValidateSessionSnapshotSave();
        var rejected = new HcsSession(machine).Apply(new(HcsAction.Load, System.Collections.Immutable.ImmutableArray<byte>.Empty));
        Assert.False(rejected.Applied); Assert.Contains("InvalidFormat", rejected.Error);
        Assert.Equal(0, machine.MasterTick);
        Assert.True(machine.RunFrame().MasterTick > 0);
        machine.ValidateSessionSnapshotSave(); // The real ROM's partial frontier is also persistable.
    }
}
