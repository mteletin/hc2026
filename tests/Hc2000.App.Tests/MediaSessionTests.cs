using System.Collections.Immutable;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class MediaSessionTests(Xunit.ITestOutputHelper output) : IDisposable
{
    private readonly HddColdAdmissionTests.PrivateScopedDomain nativeDomain = new();
    public void Dispose() => nativeDomain.Dispose();
    internal static ImmutableArray<byte> Disk(byte fill = 0xe5) => ImmutableArray.CreateRange(Dsk.Create(new(40, 1, 1, 128), fill: fill).Save());

    [Fact]
    public void BothSlotsInsertProtectExportAndEjectThroughSameFdcDrives()
    {
        var a = new FloppyDrive(new()); var b = new FloppyDrive(new());
        var board = new FloppyController(new(), 10, a, b);
        var media = new MediaSession(board.DriveA, board.DriveB);
        foreach (var slot in Enum.GetValues<DiskSlot>())
        {
            var mounted = media.Apply(new(MediaAction.InsertDisk, slot, Disk(), "synthetic.dsk"));
            Assert.Null(mounted.Error);
            var drive = slot == DiskSlot.A ? board.DriveA : board.DriveB;
            Assert.NotNull(drive.Image);
            Assert.Null(media.Apply(new(MediaAction.WriteProtect, slot, Value: true)).Error);
            Assert.True(drive.WriteProtected);
            var reply = media.Apply(new(MediaAction.ExportDisk, slot));
            Assert.Null(reply.Error);
            Assert.Equal(Enumerable.Repeat((byte)0xe5, 128), Dsk.Load(reply.Export.AsSpan()).Tracks[0].Sectors[0].Data);
            Assert.Null(media.Apply(new(MediaAction.EjectDisk, slot)).Error);
            Assert.Null(drive.Image);
        }
    }

    [Fact]
    public void MalformedAndBusyMountPreservePreviousMediumAndGeneration()
    {
        var a = new FloppyDrive(new()); var board = new FloppyController(new(), 10, a, new(new()));
        var media = new MediaSession(a, board.DriveB);
        Assert.Equal(1, media.Apply(new(MediaAction.InsertDisk, Bytes: Disk(), Name: "original")).A.Generation);
        var old = a.Image;
        var invalid = media.Apply(new(MediaAction.InsertDisk, Bytes: [1, 2, 3], Name: "invalid"));
        Assert.NotNull(invalid.Error); Assert.Same(old, a.Image); Assert.Equal(1, invalid.A.Generation);
        board.WriteLatch(0x1a);
        var busy = media.Apply(new(MediaAction.InsertDisk, Bytes: Disk(0x72)));
        Assert.NotNull(busy.Error); Assert.True(busy.A.Busy); Assert.Same(old, a.Image);
        Assert.Equal("original", busy.A.Name); Assert.Equal(1, busy.A.Generation);
    }

    [Fact]
    public void SharedFifoOrdersMediaBetweenLifecycleAndKeyReleaseWithoutUiMutation()
    {
        var queue = new InputQueue(); var bridge = new MediaBridge(queue);
        var a = new FloppyDrive(new()); var session = new MediaSession(a, new(new()));
        var matrix = new KeyboardMatrix(); var seen = new List<string>();
        var controller = new InputController(queue, (control, _) => seen.Add(control.Kind.ToString()),
            (command, _) => { seen.Add(command.Action.ToString()); bridge.Publish(session.Apply(command)); });
        queue.Enqueue(new KeyTransition("key", new(HcKey.A), true));
        queue.Enqueue(new RuntimeControl(InputControlKind.Pause));
        var request = bridge.RequestAsync(new(MediaAction.InsertDisk, Bytes: Disk()));
        queue.Enqueue(new KeyTransition("key", new(HcKey.A), false));
        Assert.Null(a.Image); Assert.False(request.IsCompleted);
        controller.ApplyPending(0, matrix);
        Assert.Equal(new[] { "Pause", "InsertDisk" }, seen);
        Assert.Equal(4, controller.LastAppliedSequence); Assert.True(request.IsCompletedSuccessfully);
        Assert.NotNull(a.Image); Assert.Equal(0x1f, matrix.Read(0xfd));
    }

    [Fact]
    public async Task OwnerCheckAndDisconnectRejectWrongThreadsAndStaleCompletions()
    {
        var session = new MediaSession(new(new()), new(new()));
        var initial = session.Apply(new(MediaAction.Inspect));
        var checkedOwner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try { Assert.Throws<InvalidOperationException>(() => session.Apply(new(MediaAction.Inspect))); checkedOwner.SetResult(); }
            catch (Exception ex) { checkedOwner.SetException(ex); }
        }).Start();
        await checkedOwner.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var bridge = new MediaBridge(new InputQueue());
        var first = bridge.RequestAsync(new(MediaAction.Inspect));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.RequestAsync(new(MediaAction.Inspect)));
        bridge.Publish(initial with { RequestId = 0 }); Assert.False(first.IsCompleted);
        bridge.Publish(initial with { RequestId = 1 }); await first;
        var second = bridge.RequestAsync(new(MediaAction.Inspect));
        bridge.Publish(initial with { RequestId = 1 }); Assert.False(second.IsCompleted);
        bridge.Disconnect();
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        bridge.Publish(initial with { RequestId = 2 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.RequestAsync(new(MediaAction.Inspect)));
    }

    [Fact]
    public void ExportIncludesGuestWriteAndDirtyEjectRequiresExplicitDiscard()
    {
        var scheduler = new DeviceScheduler(); var a = new FloppyDrive(new());
        var board = new FloppyController(scheduler, 10, a, new(new()));
        var media = new MediaSession(a, board.DriveB);
        media.Apply(new(MediaAction.InsertDisk, Bytes: Disk()));
        board.WriteLatch(0x1a);
        foreach (var value in new byte[] { 3, 0xdf, 1, 0x45, 0, 0, 0, 1, 0, 1, 0x2a, 0x80 }) board.Fdc.WriteData(value);
        Ready(); board.Fdc.WriteData(0x72);
        board.WriteLatch(0x1b); Ready();
        while (board.Fdc.Phase == FdcPhase.Result) board.Fdc.ReadData();
        board.WriteLatch(0x10); // deselect motors; head unload remains scheduler-owned
        int events = 0;
        while (a.HeadLoaded && ++events < 1000) scheduler.AdvanceTo(scheduler.PendingEvents.Min(e => e.DueTick));
        var exported = media.Apply(new(MediaAction.ExportDisk));
        Assert.Null(exported.Error); Assert.Equal(1, exported.A.DirtySectors);
        Assert.Equal(0x72, Dsk.Load(exported.Export.AsSpan()).Tracks[0].Sectors[0].Data[0]);
        var blocked = media.Apply(new(MediaAction.EjectDisk));
        Assert.NotNull(blocked.Error); Assert.NotNull(a.Image);
        Assert.Null(media.Apply(new(MediaAction.EjectDisk, DiscardChanges: true)).Error);
        Assert.Null(a.Image);
        void Ready()
        {
            int count = 0;
            while ((board.Fdc.ReadStatus() & 0x80) == 0 && ++count < 1000)
                scheduler.AdvanceTo(scheduler.PendingEvents.Min(e => e.DueTick));
            Assert.NotEqual(0, board.Fdc.ReadStatus() & 0x80);
        }
    }

    [Fact]
    public async Task BoundedFilesAndAtomicCopyNeverWriteTheInsertedSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "synthetic.dsk"); var saved = Path.Combine(directory, "copy.dsk");
            var original = Disk();
            output.WriteLine("Synthetic 40x1x1x128 EDSK SHA256: " + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(original.AsSpan())));
            await File.WriteAllBytesAsync(source, original.ToArray());
            var payload = await MediaFiles.ReadAsync(source, MediaFiles.MaxDiskBytes);
            var session = new MediaSession(new(new()), new(new()));
            var mounted = session.Apply(new(MediaAction.InsertDisk, Bytes: payload)); Assert.Null(mounted.Error);
            var exported = session.Apply(new(MediaAction.ExportDisk));
            await File.WriteAllBytesAsync(saved, [1, 2, 3]);
            await MediaFiles.SaveAsync(saved, exported.Export);
            Assert.Equal(original.ToArray(), await File.ReadAllBytesAsync(source));
            Assert.Equal(128, Dsk.Load(await File.ReadAllBytesAsync(saved)).Tracks[0].Sectors[0].Data.Length);
            await Assert.ThrowsAsync<InvalidDataException>(() => MediaFiles.ReadAsync(source, 2));
            Assert.Equal(2, Directory.GetFiles(directory).Length);
        }
        finally { Directory.Delete(directory, true); }
    }
}
