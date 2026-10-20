using System.Buffers.Binary;
using System.Collections.Immutable;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Publication = Hc2000.HostFiles.HddContainerPublication;
using Xunit;

namespace Hc2000.App.Tests;

[Collection("HDD bound publication")]
public sealed class HddBoundPublicationTests
{
    private const int S = HddMediaAdapter.ImageLength;
    private sealed class Rig : IDisposable
    {
        internal readonly HddColdVolumeTests Source = new();
        internal readonly HcMemory Memory = new(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        internal readonly BlockDiskPort Port;
        internal readonly HddMediaAdapter Adapter;
        internal readonly DiskWritebackOwnerLedger Ledger = new();
        internal readonly MediaSession Session;
        internal readonly InputQueue Queue = new();
        internal Rig(bool writable = true, bool secondWritable = false, bool restored = false)
        {
            if (secondWritable)
            {
                // Change just the literal fixture's second row flag, with an independent CRC oracle.
                using var file = File.Open(Source.PathName, FileMode.Open, FileAccess.ReadWrite);
                byte[] index = new byte[60], footer = new byte[64];
                file.Position = 2L * S; file.ReadExactly(index); file.ReadExactly(footer);
                index[8 + 24 + 11] = 0;
                uint crc = Crc(index.AsSpan(0, 56));
                BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(56), crc);
                BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(52), crc);
                BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(60), Crc(footer.AsSpan(0, 60)));
                file.Position = 2L * S; file.Write(index); file.Write(footer);
            }
            var fdc = new FloppyController(new DeviceScheduler(), 100, new(new()), new(new()));
            Port = new(Memory, fdc, hddEnabled: true, guestAckEnabled: true);
            if (restored) Port.Restore(null);
            using var selected = HddColdVolume.OpenMapped(Source.PathName,
                [new(2, "VOL1.DSK", Writable: writable), new(3, "VOL2.DSK", Writable: secondWritable)]);
            var media = selected.MappedMedia;
            foreach (var item in media) Port.AttachHdd(item.Drive, item.Bytes.AsSpan(), item.WriteProtected);
            if (restored) Port.RearmRestoredGuestAckSession(Guid.NewGuid());
            Adapter = new(Port, media);
            Session = new(fdc.DriveA, fdc.DriveB, hdd: Adapter);
        }
        internal WritebackOwnerReply Apply(WritebackOwnerOp op, long id = 0, byte drive = 2) =>
            Ledger.Apply(new(op, HddDrive: drive, HddSaveId: id), Session, null, Queue);
        internal long Begin(byte drive = 2)
        {
            var reply = Apply(WritebackOwnerOp.BeginHdd, drive: drive);
            Assert.True(reply.Accepted, reply.Error); Assert.True(reply.SessionFrozen);
            return Assert.IsType<HddSaveCapture>(reply.HddSave).Id;
        }
        internal void Write(byte drive, byte value)
        {
            for (int i = 0; i < 512; i++) Memory.Write((ushort)(0x8200 + i), value);
            Span<byte> command = stackalloc byte[16]; command.Clear();
            command[0] = 2; command[1] = 2; command[2] = drive;
            BinaryPrimitives.WriteUInt32LittleEndian(command[4..], 64); // Existing file data, not directory metadata.
            BinaryPrimitives.WriteUInt16LittleEndian(command[8..], 0x8200);
            BinaryPrimitives.WriteUInt32LittleEndian(command[12..], Port.CaptureHdd(drive).Generation);
            for (int i = 0; i < command.Length; i++) Memory.Write((ushort)(0x8000 + i), command[i]);
            Port.Write(0x81, 0, 0); Port.Write(0x83, 0x80, 0); Port.Write(0x89, 1, 0);
            Assert.Equal(0, Port.Read(0x89, 0)); Assert.True(Port.CaptureHdd(drive).Dirty);
        }
        public void Dispose() => Source.Dispose();
    }
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        return ~crc;
    }

    [Fact]
    public void CapturedGuestBytesPublishThroughExistingOwnerAndOnlySelectedSlotIsAcknowledged()
    {
        using var rig = new Rig();
        var before = File.ReadAllBytes(rig.Source.PathName);
        rig.Write(2, 0x51);
        var state = rig.Port.CaptureHdd(2); var other = rig.Port.CaptureHdd(3);
        long id = rig.Begin();
        Assert.True(rig.Port.CaptureHdd(2).Dirty);
        Assert.False(rig.Apply(WritebackOwnerOp.Complete).Accepted);
        Assert.False(rig.Apply(WritebackOwnerOp.Begin).Accepted);
        var saved = rig.Apply(WritebackOwnerOp.PublishHdd, id);
        Assert.True(saved.Accepted, saved.Error); Assert.False(saved.SessionFrozen);
        Assert.Equal(Publication.OfflineOutcome.KnownNew, saved.HddPublication?.Receipt?.Outcome);
        var current = rig.Port.CaptureHdd(2);
        Assert.False(current.Dirty); Assert.Equal(state.Generation, current.Generation); Assert.Equal(state.Bytes, current.Bytes);
        AssertSlot(other, rig.Port.CaptureHdd(3));
        var after = File.ReadAllBytes(rig.Source.PathName);
        Assert.Equal(state.Bytes, after.AsSpan(0, S).ToArray());
        Assert.Equal(before.AsSpan(S, S).ToArray(), after.AsSpan(S, S).ToArray());
        using var reopened = HddColdVolume.Open(rig.Source.PathName, "VOL1.DSK");
        Assert.Equal(2ul, reopened.Identity.Store.Revision);
        Assert.False(rig.Apply(WritebackOwnerOp.PublishHdd, id).Accepted);
    }

    [Fact]
    public void RestoredUncooperatedBindingCanFlushCapturedBytesWithoutRequalifyingGuestMutation()
    {
        using var rig = new Rig(restored: true);
        rig.Write(2, 0x58);
        Assert.True(rig.Port.Uncooperated);
        var before = rig.Port.CaptureHdd(2);
        var saved = rig.Apply(WritebackOwnerOp.PublishHdd, rig.Begin());
        Assert.True(saved.Accepted, saved.Error); Assert.False(saved.SessionFrozen);
        Assert.Equal(Publication.OfflineOutcome.KnownNew, saved.HddPublication?.Receipt?.Outcome);
        var after = rig.Port.CaptureHdd(2);
        Assert.Equal(before.Generation, after.Generation); Assert.Equal(before.Bytes, after.Bytes);
        Assert.False(after.Dirty); Assert.True(rig.Port.Uncooperated);
        using var reopened = HddColdVolume.Open(rig.Source.PathName, "VOL1.DSK");
        Assert.Equal(0x58, reopened.Media(2).Bytes[32768]);
    }

    [Fact]
    public void TwoDirtySlotsSaveSequentiallyUsingAdvancedStoreTokensAndUnchangedSurvivorBaseline()
    {
        using var rig = new Rig(secondWritable: true);
        rig.Write(2, 0x51); rig.Write(3, 0x52);
        var survivor = rig.Port.CaptureHdd(3);
        var firstSave = rig.Apply(WritebackOwnerOp.PublishHdd, rig.Begin());
        Assert.True(firstSave.Accepted, firstSave.Error);
        AssertSlot(survivor, rig.Port.CaptureHdd(3));
        var secondSave = rig.Apply(WritebackOwnerOp.PublishHdd, rig.Begin(3));
        Assert.True(secondSave.Accepted, secondSave.Error);
        Assert.False(rig.Port.CaptureHdd(2).Dirty); Assert.False(rig.Port.CaptureHdd(3).Dirty);
        using (var first = HddColdVolume.Open(rig.Source.PathName, "VOL1.DSK"))
        {
            Assert.Equal(3ul, first.Identity.Store.Revision);
            Assert.Equal(0x51, first.Media(2).Bytes[32768]);
        }
        using var second = HddColdVolume.Open(rig.Source.PathName, "VOL2.DSK");
        Assert.Equal(3ul, second.Identity.Store.Revision);
        Assert.Equal(0x52, second.Media(3).Bytes[32768]);
    }

    [Theory]
    [InlineData(Publication.OfflineIoPoint.AfterMove)]
    [InlineData(Publication.OfflineIoPoint.TargetReadback)]
    public void UnknownRetainsDirtyFenceAndOnlySameCaptureReconciliationCanReleaseIt(Publication.OfflineIoPoint failure)
    {
        using var rig = new Rig();
        rig.Write(2, 0x53);
        bool fail = true;
        rig.Adapter.PublicationFault = e =>
        {
            if (fail && (e.Point == failure || e.Point == Publication.OfflineIoPoint.TargetReadback))
                throw new IOException("injected unreadable publication outcome");
        };
        long id = rig.Begin();
        var unknown = rig.Apply(WritebackOwnerOp.PublishHdd, id);
        Assert.False(unknown.Accepted); Assert.True(unknown.SessionFrozen, unknown.Error);
        Assert.Equal(Publication.OfflineOutcome.Unknown, unknown.HddPublication?.Receipt?.Outcome);
        Assert.True(rig.Port.CaptureHdd(2).Dirty);
        Assert.False(rig.Apply(WritebackOwnerOp.AbortHdd, id).Accepted);
        Assert.False(rig.Apply(WritebackOwnerOp.CancelCompletion).Accepted);
        Assert.False(rig.Apply(WritebackOwnerOp.ReconcileHdd, id + 1).Accepted);
        Assert.False(rig.Ledger.ShutdownPermitted);
        // Exact-byte CaptureSave remains available even when publication is unknown.
        var raw = rig.Adapter.Apply(new(HddAction.CaptureSave));
        Assert.Null(raw.Error); Assert.Equal(0x53, Assert.IsType<HddSaveCapture>(raw.Save).Bytes[32768]);
        fail = false;
        var reconciled = rig.Apply(WritebackOwnerOp.ReconcileHdd, id);
        Assert.True(reconciled.Accepted, reconciled.Error); Assert.False(reconciled.SessionFrozen);
        Assert.False(rig.Port.CaptureHdd(2).Dirty);
    }

    [Fact]
    public void AChangedNativeSurvivorAfterPublicationCannotManufactureSavedAcknowledgement()
    {
        using var rig = new Rig(secondWritable: true);
        rig.Write(2, 0x54);
        rig.Adapter.PublicationFault = e => { if (e.Point == Publication.OfflineIoPoint.AfterMove) rig.Write(3, 0x55); };
        var result = rig.Apply(WritebackOwnerOp.PublishHdd, rig.Begin());
        Assert.False(result.Accepted); Assert.True(result.SessionFrozen);
        Assert.Equal(Publication.OfflineOutcome.KnownNew, result.HddPublication?.Receipt?.Outcome);
        Assert.True(rig.Port.CaptureHdd(2).Dirty); Assert.True(rig.Port.CaptureHdd(3).Dirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOnlyOrStaleStoreRefusesBeforeWritingAndNeverCleans(bool stale)
    {
        using var rig = new Rig(writable: stale);
        if (stale)
        {
            rig.Write(2, 0x56);
            using var file = File.OpenWrite(rig.Source.PathName);
            file.Position = 2L * S; file.WriteByte(0); // Stale metadata, never repaired by publication.
        }
        var before = File.ReadAllBytes(rig.Source.PathName);
        var native = rig.Port.CaptureHdd(2);
        var result = rig.Apply(WritebackOwnerOp.PublishHdd, rig.Begin());
        Assert.False(result.Accepted); Assert.False(result.SessionFrozen);
        AssertSlot(native, rig.Port.CaptureHdd(2));
        Assert.Equal(before, File.ReadAllBytes(rig.Source.PathName));
    }

    [Fact]
    public void ResourceRefusalStillReturnsRawCaptureAndDoesNotAcquireFence()
    {
        using var rig = new Rig();
        rig.Write(2, 0x57);
        rig.Adapter.PublicationLimits = rig.Adapter.PublicationLimits with { MaxSnapshotBytes = 1 };
        var refused = rig.Apply(WritebackOwnerOp.BeginHdd);
        Assert.False(refused.Accepted); Assert.False(refused.SessionFrozen);
        Assert.Equal(0x57, Assert.IsType<HddSaveCapture>(refused.HddSave).Bytes[32768]);
        Assert.True(rig.Port.CaptureHdd(2).Dirty);
    }

    private static void AssertSlot(HddSlotState expected, HddSlotState actual)
    {
        Assert.Equal(expected.Present, actual.Present); Assert.Equal(expected.Generation, actual.Generation);
        Assert.Equal(expected.WriteProtected, actual.WriteProtected); Assert.Equal(expected.Dirty, actual.Dirty);
        Assert.Equal(expected.Bytes, actual.Bytes);
    }
}
