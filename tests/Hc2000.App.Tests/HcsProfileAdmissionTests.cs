using System.Collections.Immutable;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.App.Snapshots;
using Hc2000.Machine;
using Xunit;

namespace Hc2000.App.Tests;

[Collection("expansion-app")]
public sealed class HcsProfileAdmissionTests
{
    [Fact]
    public async Task PreparedPagedRuntimeAdmitsLargerSnapshotBytesBeforeOwnerValidation()
    {
        var roms = ProfileRomSelection.LoadWholeChipV2(RomSelectionFixture.RepositoryRoot());
        var prepared = BasicMachineComposition.PrepareWholeChipV2(
            (MachineProfile.Hc2000V2PagedExpansionV1, roms.Basic, roms.Cpm, roms.If1));
        var queue = new InputQueue(); var snapshots = new HcsBridge(queue);
        queue.Enqueue(new RuntimeControl(InputControlKind.Pause));
        using var worker = BasicMachineComposition.CreateWorker(prepared, queue, snapshots: snapshots);
        try
        {
            // Deliberately invalid HCS: prove ordinary queue admission, not codec acceptance.
            var bytes = ImmutableArray.CreateRange(new byte[268435457]);
            var reply = await snapshots.RequestAsync(new(HcsAction.Load, bytes)).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(reply.Applied);
            Assert.Contains("InvalidFormat", reply.Error);
        }
        finally
        {
            worker.Dispose();
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}
