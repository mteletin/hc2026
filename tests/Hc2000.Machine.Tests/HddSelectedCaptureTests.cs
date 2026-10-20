using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.Machine.Tests;

// Writable bounded capture of one prepared selection into the existing slot: actual bytes land in the
// slot and the pending-Saved (dirty) state persists until the existing TryAcknowledgeHddSaved matches.
// Private copies/literals only (the accepted hdd-fixture-data/wire contract, independently re-derived);
// no originals, host save, Export/Restore or caller activation.
public sealed class HddSelectedCaptureTests
{
    private const int SlotBytes = 8_388_608;
    private const ulong FileBytes = 16_777_340;
    private const string P0Sha = "9D0ABB36878273883169C3756407D0658B4C8EC8E6B31CE35BC70F188BEE987F";
    private const string P1Sha = "F484C7D32E06CBA31D0DD508A1EBC7C26CC4ECDC18A738E040CC3D6BEF7F1EC3";
    private const string StoreSha = "AF3406F99F322A33D6AE6A52A5ECB66BA90342A271582CDCD5020273BDB0B5E2";

    private static byte[] Index() => Convert.FromHexString(
        "4843564401000200564f4c312020202044534b0000008000000000005ba36203564f4c322020202044534b010000800001000000b9eccf25da8245ce");
    private static byte[] Footer() => Convert.FromHexString(
        "4843564d31454e4401000000000000000102030405060708090a0b0c0d0e0f10010000000000000000000001000000003c000000da8245ce00000000b7cd3ab7");

    private static byte[] Payload(byte content)
    {
        var bytes = new byte[SlotBytes];
        bytes.AsSpan(0, 32768).Fill(0xe5);
        Convert.FromHexString("0053454544202020205458540000000108000000000000000000000000000000").CopyTo(bytes, 0);
        bytes.AsSpan(32768, 4096).Fill(content);
        return bytes;
    }

    private static HddSelectionLimits Limits() => new(FileBytes, 2, 60, 624,
        SlotBytes, 4UL * SlotBytes, 0, 3UL * SlotBytes + 65536, 58_786_416, 1, 1);
    private static HddSelectionUsage Usage() => new(1, 4UL * SlotBytes, 0, 65536);

    // Decode and token hash share these exact owned wire arrays, kept stable during Prepare.
    private static (HddContainerDescriptor Descriptor, HddStoreToken Store) PairedStore()
    {
        var index = Index();
        var footer = Footer();
        var decoded = HddCatalogCodec.DecodeHdd(footer, index, FileBytes, new HddLimits(16_777_340, 2, 60, 624));
        Assert.True(decoded.Success, decoded.Failure?.Detail);
        var hash = SHA256.HashData([.. index, .. footer]);
        Assert.Equal(StoreSha, Convert.ToHexString(hash));
        var descriptor = decoded.Value!;
        return (descriptor, new HddStoreToken([.. descriptor.Footer.ContainerId], descriptor.Footer.Revision, [.. hash]));
    }

    // The writable volume (entry flags 00) of the pinned fixture.
    private static HddPreparedSelection PrepareWritable()
    {
        var (descriptor, store) = PairedStore();
        var image = Payload(0x41);
        Assert.Equal(P0Sha, Convert.ToHexString(SHA256.HashData(image)));
        var result = HddVolumeSelection.Prepare(descriptor, store,
            new HddVolumeIdentity(store, "VOL1.DSK", [.. Convert.FromHexString(P0Sha)]), image, false, Limits(), Usage(), []);
        Assert.True(result.Success, result.Detail);
        return result.Selection!;
    }

    // The read-only volume (entry flags 01) of the pinned fixture.
    private static HddPreparedSelection PrepareReadOnly()
    {
        var (descriptor, store) = PairedStore();
        var image = Payload(0x42);
        Assert.Equal(P1Sha, Convert.ToHexString(SHA256.HashData(image)));
        var result = HddVolumeSelection.Prepare(descriptor, store,
            new HddVolumeIdentity(store, "VOL2.DSK", [.. Convert.FromHexString(P1Sha)]), image, false, Limits(), Usage(), []);
        Assert.True(result.Success, result.Detail);
        return result.Selection!;
    }

    // Independent private media only.
    private sealed class Rig
    {
        public HcMemory Memory { get; } = new(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        public DeviceScheduler Scheduler { get; } = new();
        public FloppyController Fdc { get; }
        public BlockDiskPort Port { get; }

        public Rig(bool hdd = true)
        {
            Fdc = new FloppyController(Scheduler, 100,
                new(new(), Dsk.Create(new(40, 2, 9, 512))),
                new(new(), Dsk.Create(new(40, 2, 9, 512))));
            Port = new(Memory, Fdc, hddEnabled: hdd);
        }

        public HddSlotState Snapshot(byte unit) => Port.CaptureHdd(unit);

        public void WriteSector(byte unit, byte value)
        {
            for (var i = 0; i < 512; i++) Memory.Write((ushort)(0x8100 + i), value);
            Span<byte> descriptor = stackalloc byte[16];
            descriptor.Clear(); descriptor[0] = 2; descriptor[1] = 2; descriptor[2] = unit;
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor[8..], 0x8100);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[12..], Port.CaptureHdd(unit).Generation);
            for (var i = 0; i < descriptor.Length; i++) Memory.Write((ushort)(0x8000 + i), descriptor[i]);
            Port.Write(0x81, 0, 0); Port.Write(0x83, 0x80, 0);
            Port.Write(0x89, 1, 0);
            Assert.Equal(0, Port.Read(0x89, 0));
        }

        public Observation Observe() => new(Port.Capture(), Memory.Ram.ToArray(), Fdc.Fdc.Inspect());
    }

    private sealed record Observation(BlockDiskPortState Port, byte[] Ram, FdcDiagnosticSnapshot Fdc);

    private static void AssertUnchanged(Observation before, Observation after)
    {
        Assert.Equal(before.Port.Pointer, after.Port.Pointer);
        Assert.Equal(before.Port.Status, after.Port.Status);
        Assert.Equal(before.Port.Slots.Length, after.Port.Slots.Length);
        for (var i = 0; i < before.Port.Slots.Length; i++)
        {
            var b = before.Port.Slots[i]; var a = after.Port.Slots[i];
            Assert.True(b.Bytes.AsSpan().SequenceEqual(a.Bytes), $"slot {i + 2} image changed");
            Assert.Equal(b.Generation, a.Generation);
            Assert.Equal(b.WriteProtected, a.WriteProtected);
            Assert.Equal(b.Dirty, a.Dirty);
        }
        Assert.Equal(before.Ram, after.Ram);
        Assert.Equal(before.Fdc.Phase, after.Fdc.Phase);
        Assert.Equal(before.Fdc.MainStatus, after.Fdc.MainStatus);
    }

    private static void AssertImageEquals(byte[] expected, HddSlotState state)
    {
        Assert.Equal(SlotBytes, state.Bytes.Length);
        Assert.True(state.Bytes.AsSpan().SequenceEqual(expected), "captured image bytes differ");
    }

    // --- actual byte-level capture ---

    [Fact]
    public void WritableSelectionCapturesActualBytesIntoEmptySlotAndPersistsPendingSaved()
    {
        var r = new Rig();
        var before = r.Observe();
        var image = Payload(0x41);
        var expected = r.Snapshot(3);
        Assert.Empty(expected.Bytes);
        var receipt = r.Port.TryCaptureSelectedHdd(3, PrepareWritable(), expected, false);
        Assert.True(receipt.Success, receipt.Detail);
        Assert.Equal(HddSelectedCaptureError.None, receipt.Error);

        var after = r.Snapshot(3);
        AssertImageEquals(image, after);
        // Actual bytes: the SEED TXE directory and the per-payload region survive the capture.
        Assert.Equal(0x00, after.Bytes[0]); Assert.Equal(0x53, after.Bytes[1]); Assert.Equal(0x45, after.Bytes[2]);
        Assert.Equal(0x41, after.Bytes[32768]); Assert.Equal(0x41, after.Bytes[32768 + 4095]);
        Assert.Equal(0xe5, after.Bytes[512]);
        Assert.Equal(0, after.Bytes[SlotBytes - 1]); // the payload's tail region is zero
        Assert.Equal(expected.Generation + 1, after.Generation);
        Assert.False(after.WriteProtected);
        Assert.True(after.Dirty, "captured media is pending-Saved until a matching acknowledgement");

        // The selector's original owned image is untouched, and the other slots and memory are unchanged.
        Assert.Equal(P0Sha, Convert.ToHexString(SHA256.HashData(image)));
        var observed = r.Observe();
        Assert.Equal(before.Port.Pointer, observed.Port.Pointer);
        Assert.Equal(before.Port.Status, observed.Port.Status);
        for (var unit = 2; unit <= 5; unit++)
        {
            var b = before.Port.Slots[unit - 2]; var a = observed.Port.Slots[unit - 2];
            if (unit == 3) continue;
            Assert.True(b.Bytes.AsSpan().SequenceEqual(a.Bytes), $"slot {unit} image changed");
            Assert.Equal(b.Generation, a.Generation);
            Assert.Equal(b.WriteProtected, a.WriteProtected);
            Assert.Equal(b.Dirty, a.Dirty);
        }
        Assert.Equal(before.Ram, observed.Ram);
    }

    [Fact]
    public void WritableSelectionCapturesActualBytesIntoOccupiedSlot()
    {
        var r = new Rig();
        var initial = new byte[SlotBytes];
        initial[7] = 0x11; initial[^1] = 0x22;
        r.Port.AttachHdd(4, initial);
        var expected = r.Snapshot(4);
        var receipt = r.Port.TryCaptureSelectedHdd(4, PrepareWritable(), expected, false);
        Assert.True(receipt.Success, receipt.Detail);
        var after = r.Snapshot(4);
        AssertImageEquals(Payload(0x41), after);
        Assert.Equal(expected.Generation + 1, after.Generation);
        Assert.False(after.WriteProtected);
        Assert.True(after.Dirty);
        // The replaced image is no longer in the slot.
        Assert.NotEqual(0x11, after.Bytes[7]);
        Assert.NotEqual(0x22, after.Bytes[^1]);
    }

    // --- dirty-state persistence and the existing Saved acknowledgement ---

    [Fact]
    public void PendingSavedPersistsUntilAMatchingAcknowledgeClearsIt()
    {
        var r = new Rig();
        r.Port.TryCaptureSelectedHdd(3, PrepareWritable(), r.Snapshot(3), false);
        var current = r.Snapshot(3);
        Assert.True(current.Dirty);

        // A mismatched expectation is refused and the pending-Saved state persists with the same bytes.
        var stale = (byte[])current.Bytes.Clone();
        stale[12345] ^= 0xff;
        Assert.False(r.Port.TryAcknowledgeHddSaved([(3, current with { Bytes = stale })]));
        var kept = r.Snapshot(3);
        Assert.True(kept.Dirty, "refused acknowledgement must not clear the pending-Saved state");
        Assert.True(kept.Bytes.AsSpan().SequenceEqual(current.Bytes));
        Assert.Equal(current.Generation, kept.Generation);

        // The exact current state acknowledges and clears only the dirty flag.
        Assert.True(r.Port.TryAcknowledgeHddSaved([(3, current)]));
        var cleared = r.Snapshot(3);
        Assert.False(cleared.Dirty);
        Assert.True(cleared.Bytes.AsSpan().SequenceEqual(current.Bytes), "acknowledgement never changes the bytes");
        Assert.Equal(current.Generation, cleared.Generation);
        Assert.False(cleared.WriteProtected);
    }

    [Fact]
    public void GuestWritesAfterCaptureAndOnlyTheNewBytesAcknowledge()
    {
        var r = new Rig();
        var image = Payload(0x41);
        r.Port.TryCaptureSelectedHdd(3, PrepareWritable(), r.Snapshot(3), false);
        var captured = r.Snapshot(3);
        AssertImageEquals(image, captured);

        // A guest write lands on the captured bytes and keeps the slot dirty.
        r.WriteSector(3, 0x77);
        var written = r.Snapshot(3);
        Assert.True(written.Dirty);
        Assert.Equal(captured.Generation, written.Generation);
        for (var i = 0; i < 512; i++) Assert.Equal(0x77, written.Bytes[0 * 512 + i]);
        Assert.True(image[513] == written.Bytes[513], "everything past the written sector is intact");

        // The pre-write baseline no longer matches: refused, bytes and dirty state persist.
        Assert.False(r.Port.TryAcknowledgeHddSaved([(3, captured)]));
        var kept = r.Snapshot(3);
        Assert.True(kept.Dirty);
        Assert.True(kept.Bytes.AsSpan().SequenceEqual(written.Bytes));

        // The actual current bytes acknowledge.
        Assert.True(r.Port.TryAcknowledgeHddSaved([(3, written)]));
        var cleared = r.Snapshot(3);
        Assert.False(cleared.Dirty);
        Assert.True(cleared.Bytes.AsSpan().SequenceEqual(written.Bytes));
    }

    // --- refusal cases: prior bytes and state are preserved ---

    [Theory]
    [InlineData(1)] [InlineData(6)] [InlineData(255)]
    public void InvalidUnitRefusesWithoutMutation(byte unit)
    {
        var r = new Rig();
        var before = r.Observe();
        var receipt = r.Port.TryCaptureSelectedHdd(unit, PrepareWritable(), r.Snapshot(3), false);
        Assert.False(receipt.Success);
        Assert.Equal(HddSelectedCaptureError.InvalidUnit, receipt.Error);
        AssertUnchanged(before, r.Observe());
    }

    [Fact]
    public void DisabledControllerRefusesWithoutChangingDmaOrState()
    {
        var memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var scheduler = new DeviceScheduler();
        var fdc = new FloppyController(scheduler, 100, new(new(), Dsk.Create(new(40, 2, 9, 512))),
            new(new(), Dsk.Create(new(40, 2, 9, 512))));
        var port = new BlockDiskPort(memory, fdc);
        memory.Write(0x9000, 0x5a);
        var ram = memory.Ram.ToArray();
        var receipt = port.TryCaptureSelectedHdd(2, PrepareWritable(), null!, false);
        Assert.False(receipt.Success);
        Assert.Equal(HddSelectedCaptureError.HddDisabled, receipt.Error);
        Assert.Equal(ram, memory.Ram.ToArray());
        Assert.Equal(0, port.Read(0x89, 0));
    }

    [Fact]
    public void NullSelectionAndNullExpectationRefuseWithoutMutation()
    {
        var r = new Rig();
        var before = r.Observe();
        Assert.Equal(HddSelectedCaptureError.NullSelection,
            r.Port.TryCaptureSelectedHdd(3, null!, r.Snapshot(3), false).Error);
        Assert.Equal(HddSelectedCaptureError.NullExpectation,
            r.Port.TryCaptureSelectedHdd(3, PrepareWritable(), null!, false).Error);
        Assert.Equal(HddSelectedCaptureError.NullExpectation,
            r.Port.TryCaptureSelectedHdd(3, PrepareWritable(), r.Snapshot(3) with { Bytes = null! }, false).Error);
        AssertUnchanged(before, r.Observe());
    }

    [Theory]
    [InlineData("stale-generation")]
    [InlineData("protection-mismatch")]
    [InlineData("dirty-flag-mismatch")]
    [InlineData("image-first")] [InlineData("image-last")] [InlineData("image-short")]
    public void StaleOrMismatchedExpectationRefusesAndPreservesSlotBytes(string fault)
    {
        var r = new Rig();
        var other = new byte[SlotBytes]; other[0] = 0x33; other[^1] = 0x44;
        r.Port.AttachHdd(3, other);
        var current = r.Snapshot(3);
        switch (fault)
        {
            case "stale-generation":
                Assert.Equal(HddSelectedCaptureError.StaleGeneration,
                    r.Port.TryCaptureSelectedHdd(3, PrepareWritable(), current with { Generation = checked(current.Generation - 1) }, false).Error);
                break;
            case "protection-mismatch":
                Assert.Equal(HddSelectedCaptureError.StaleState,
                    r.Port.TryCaptureSelectedHdd(3, PrepareWritable(), current with { WriteProtected = true }, false).Error);
                break;
            case "dirty-flag-mismatch":
                Assert.Equal(HddSelectedCaptureError.StaleState,
                    r.Port.TryCaptureSelectedHdd(3, PrepareWritable(), current with { Dirty = true }, false).Error);
                break;
            case "image-first": {
                var bytes = (byte[])current.Bytes.Clone(); bytes[0] ^= 0xff;
                Assert.Equal(HddSelectedCaptureError.StaleState,
                    r.Port.TryCaptureSelectedHdd(3, PrepareWritable(), current with { Bytes = bytes }, false).Error);
                break;
            }
            case "image-last": {
                var bytes = (byte[])current.Bytes.Clone(); bytes[^1] ^= 0xff;
                Assert.Equal(HddSelectedCaptureError.StaleState,
                    r.Port.TryCaptureSelectedHdd(3, PrepareWritable(), current with { Bytes = bytes }, false).Error);
                break;
            }
            case "image-short":
                Assert.Equal(HddSelectedCaptureError.StaleState,
                    r.Port.TryCaptureSelectedHdd(3, PrepareWritable(), current with { Bytes = current.Bytes[..^1] }, false).Error);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        var kept = r.Snapshot(3);
        Assert.True(kept.Bytes.AsSpan().SequenceEqual(other), "the prior slot bytes must survive the refusal");
        Assert.Equal(current.Generation, kept.Generation);
        Assert.Equal(current.WriteProtected, kept.WriteProtected);
        Assert.Equal(current.Dirty, kept.Dirty);
    }

    [Fact]
    public void ReadOnlySelectionRefusesWritableCaptureWithoutMutation()
    {
        var r = new Rig();
        var before = r.Observe();
        var receipt = r.Port.TryCaptureSelectedHdd(3, PrepareReadOnly(), r.Snapshot(3), false);
        Assert.False(receipt.Success);
        Assert.Equal(HddSelectedCaptureError.ReadOnlySelection, receipt.Error);
        AssertUnchanged(before, r.Observe());
    }

    [Fact]
    public void WriteProtectedSlotRefusesCaptureAsAWritingOperation()
    {
        var r = new Rig();
        var media = new byte[SlotBytes]; media[9] = 0x55;
        r.Port.AttachHdd(3, media, writeProtected: true);
        var before = r.Observe();
        var receipt = r.Port.TryCaptureSelectedHdd(3, PrepareWritable(), r.Snapshot(3), false);
        Assert.False(receipt.Success);
        Assert.Equal(HddSelectedCaptureError.WriteProtectedMedia, receipt.Error);
        var kept = r.Snapshot(3);
        Assert.Equal(0x55, kept.Bytes[9]);
        AssertUnchanged(before, r.Observe());
    }

    [Fact]
    public void DirtyMediaRefusesCaptureUnlessExplicitlyDiscarded()
    {
        var r = new Rig();
        r.Port.AttachHdd(3, new byte[SlotBytes]);
        r.WriteSector(3, 0x99);
        var dirty = r.Snapshot(3);
        Assert.True(dirty.Dirty);

        var refused = r.Port.TryCaptureSelectedHdd(3, PrepareWritable(), dirty, false);
        Assert.False(refused.Success);
        Assert.Equal(HddSelectedCaptureError.DirtyMedia, refused.Error);
        var kept = r.Snapshot(3);
        Assert.True(kept.Dirty);
        Assert.True(kept.Bytes.AsSpan().SequenceEqual(dirty.Bytes), "the dirty bytes persist after the refusal");

        // The explicit discard receipt replaces the dirty media with the selection bytes and re-enters pending-Saved.
        var accepted = r.Port.TryCaptureSelectedHdd(3, PrepareWritable(), dirty, true);
        Assert.True(accepted.Success, accepted.Detail);
        var replaced = r.Snapshot(3);
        AssertImageEquals(Payload(0x41), replaced);
        Assert.Equal(dirty.Generation + 1, replaced.Generation);
        Assert.True(replaced.Dirty);
    }

    [Fact]
    public void WrongOwnerRefusesWithoutExceptionOrMutation()
    {
        var r = new Rig();
        var selection = PrepareWritable();
        var expected = r.Snapshot(3);
        var before = r.Observe();
        HddSelectedCaptureReceipt? receipt = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { receipt = r.Port.TryCaptureSelectedHdd(3, selection, expected, false); }
            catch (Exception ex) { error = ex; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(error);
        Assert.NotNull(receipt);
        Assert.False(receipt!.Success);
        Assert.Equal(HddSelectedCaptureError.WrongOwner, receipt.Error);
        AssertUnchanged(before, r.Observe());
    }

    [Fact]
    public void RefusalReceiptLeavesTheSlotCapturableAgain()
    {
        // Explicit rollback: a refused capture leaves a slot that a correct expectation can still capture.
        var r = new Rig();
        var refused = r.Port.TryCaptureSelectedHdd(3, PrepareWritable(), r.Snapshot(3) with { Generation = 9 }, false);
        Assert.False(refused.Success);
        Assert.Equal(HddSelectedCaptureError.StaleGeneration, refused.Error);
        var receipt = r.Port.TryCaptureSelectedHdd(3, PrepareWritable(), r.Snapshot(3), false);
        Assert.True(receipt.Success, receipt.Detail);
        AssertImageEquals(Payload(0x41), r.Snapshot(3));
    }
}
