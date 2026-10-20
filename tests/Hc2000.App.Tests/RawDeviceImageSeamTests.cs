using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.App.Tests;

// O1-O5 seam evidence (raw device images over the P10 checkpoint): descriptor-driven admission in the
// panel, the one descriptor-aware session seam, and the owner writeback receipt's descriptor kind.
public sealed class RawDeviceImageSessionTests
{
    private static byte[] RawBytes(int length) => new byte[length];
    private static ImmutableArray<byte> Container() =>
        ImmutableArray.CreateRange(Dsk.Create(new(40, 1, 1, 128), fill: 0xe5).Save());

    [Fact]
    public void RawDescriptorLoadsThroughTheNativeAdapterWithDescriptorGeometry()
    {
        var a = new FloppyDrive(new()); var session = new MediaSession(a, new(new()));
        var descriptor = MediaImageDescriptor.RawFloppy(new(80, 2, 9, 512));
        var reply = session.Apply(new(MediaAction.InsertDisk, DiskSlot.A, RawBytes(descriptor.Geometry!.Length).AsSpan().ToImmutableArray(),
            "raw", Descriptor: descriptor));
        Assert.Null(reply.Error); Assert.True(reply.A.Present);
        Assert.Equal(DiskFormat.Raw, a.Image!.Format);
        Assert.Equal(80, a.Image.Cylinders); Assert.Equal(2, a.Image.Sides);
    }

    [Fact]
    public void EqualByteSizeWithDifferentGeometryComesFromTheDescriptorNotInference()
    {
        var a = new FloppyDrive(new()); var session = new MediaSession(a, new(new()));
        // 512 bytes admit (1,1,1,512) and (1,1,2,256); only the stored descriptor supplies the sector layout.
        var reply = session.Apply(new(MediaAction.InsertDisk, DiskSlot.A, RawBytes(512).AsSpan().ToImmutableArray(),
            "raw", Descriptor: MediaImageDescriptor.RawFloppy(new(1, 1, 2, 256))));
        Assert.Null(reply.Error);
        Assert.Equal(DiskFormat.Raw, a.Image!.Format); Assert.Equal(1, a.Image.Cylinders);
        Assert.Equal(2, a.Image.Tracks[0].Sectors.Count);
        Assert.Equal(256, a.Image.Tracks[0].Sectors[0].Data.Length);
    }

    [Fact]
    public void ContainerBytesUnderARawDescriptorAreRefusedAndRetainTheOldDisk()
    {
        var a = new FloppyDrive(new()); var session = new MediaSession(a, new(new()));
        var old = Container();
        var mounted = session.Apply(new(MediaAction.InsertDisk, DiskSlot.A, old, "original"));
        Assert.Null(mounted.Error);
        var before = a.Image; var generation = mounted.A.Generation;
        var refused = session.Apply(new(MediaAction.InsertDisk, DiskSlot.A, old, "original",
            Descriptor: MediaImageDescriptor.RawFloppy(new(1, 1, 1, 512))));
        Assert.NotNull(refused.Error); Assert.Same(before, a.Image); Assert.Equal(generation, refused.A.Generation);
    }

    [Fact]
    public void TruncatedRawBytesAgainstTheStoredGeometryAreRefused()
    {
        var a = new FloppyDrive(new()); var session = new MediaSession(a, new(new()));
        var refused = session.Apply(new(MediaAction.InsertDisk, DiskSlot.A, RawBytes(511).AsSpan().ToImmutableArray(),
            "raw", Descriptor: MediaImageDescriptor.RawFloppy(new(1, 1, 1, 512))));
        Assert.NotNull(refused.Error); Assert.Null(a.Image);
    }

    [Fact]
    public void RawBytesWithoutADescriptorKeepTheLegacyContainerGate()
    {
        var a = new FloppyDrive(new()); var session = new MediaSession(a, new(new()));
        var refused = session.Apply(new(MediaAction.InsertDisk, DiskSlot.A, RawBytes(512).AsSpan().ToImmutableArray(), "raw"));
        Assert.NotNull(refused.Error); Assert.Null(a.Image);
    }

    [Fact]
    public void DskContainerDescriptorKeepsTheLegacyParse()
    {
        var a = new FloppyDrive(new()); var session = new MediaSession(a, new(new()));
        var bytes = Container();
        var reply = session.Apply(new(MediaAction.InsertDisk, DiskSlot.A, bytes, "disk",
            Descriptor: MediaImageDescriptor.DskContainer));
        Assert.Null(reply.Error);
        Assert.Equal(Dsk.Load(bytes.AsSpan()).Format, a.Image!.Format);
    }
}

public sealed class RawDeviceImagePanelTests
{
    private sealed class Harness : IDisposable
    {
        public InputController Controller { get; }
        public MediaSession Session { get; }
        public FloppyDrive DriveA { get; }
        public MediaPanel Panel { get; }
        public Window Window { get; }
        public Harness()
        {
            var queue = new InputQueue(); var bridge = new MediaBridge(queue);
            DriveA = new FloppyDrive(new());
            Session = new MediaSession(DriveA, new FloppyDrive(new()));
            var session = Session;
            Controller = new InputController(queue, media: (command, _) => bridge.Publish(session.Apply(command)));
            Panel = new MediaPanel(); Window = new Window { Content = Panel }; Window.Show();
            Panel.Attach(bridge);
        }
        public async Task Pump(Task task)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!task.IsCompleted)
            {
                deadline.Token.ThrowIfCancellationRequested();
                Controller.ApplyPending(0, new KeyboardMatrix());
                await Task.Delay(1, deadline.Token);
            }
        }
        public async Task<string> InsertAsync(string path, DiskSlot slot)
        {
            var insertion = Panel.InsertDiskAsync(path, slot);
            await Pump(insertion); await insertion;
            return Panel.Notice ?? "";
        }
        public void Dispose() { Panel.Disconnect(); Window.Close(); }
    }

    [AvaloniaFact]
    public async Task RawSlotAdmitsAnyLocalRegularFileNameWithItsStoredDescriptor()
    {
        using var h = new Harness();
        var descriptor = MediaImageDescriptor.RawFloppy(new(1, 1, 1, 512));
        h.Panel.StoredDescriptorFor = _ => descriptor;
        var path = Path.GetTempFileName() + ".bin";
        try
        {
            File.WriteAllBytes(path, new byte[512]);
            var notice = await h.InsertAsync(path, DiskSlot.A);
            Assert.StartsWith("Media command applied", notice);
            Assert.Equal(DiskFormat.Raw, h.DriveA.Image!.Format);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task SuffixAloneDoesNotAdmitWithoutAStoredDescriptor()
    {
        using var h = new Harness(); // No StoredDescriptorFor: no suffix or length infers a raw image.
        var path = Path.GetTempFileName() + ".raw";
        try
        {
            File.WriteAllBytes(path, new byte[512]);
            var notice = await h.InsertAsync(path, DiskSlot.A);
            Assert.StartsWith("Media action failed:", notice);
            Assert.Null(h.DriveA.Image);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task RawSlotRefusesAContainerImageAndRetainsNoDisk()
    {
        using var h = new Harness();
        h.Panel.StoredDescriptorFor = _ => MediaImageDescriptor.RawFloppy(new(1, 1, 1, 512));
        var path = Path.GetTempFileName() + ".dsk";
        try
        {
            File.WriteAllBytes(path, Dsk.Create(new(40, 1, 1, 128), fill: 0xe5).Save());
            var notice = await h.InsertAsync(path, DiskSlot.A);
            Assert.StartsWith("Media action failed:", notice);
            Assert.Null(h.DriveA.Image);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task ContainerSlotKeepsTheLegacyDskGate()
    {
        using var h = new Harness();
        var path = Path.GetTempFileName() + ".dsk";
        try
        {
            // Legacy (Standard) container: Dsk.Create defaults to Extended, so name the format.
            File.WriteAllBytes(path, Dsk.Create(new(40, 1, 1, 128), DiskFormat.Standard, fill: 0xe5).Save());
            var notice = await h.InsertAsync(path, DiskSlot.A);
            Assert.StartsWith("Media command applied", notice);
            Assert.Equal(DiskFormat.Standard, h.DriveA.Image!.Format);
        }
        finally { File.Delete(path); }
    }
}

public sealed class RawDeviceImageWritebackHostTests : IDisposable
{
    private readonly HddColdAdmissionTests.PrivateScopedDomain nativeDomain = new();
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "hc2000-rawseam-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() { Directory.Delete(root, true); nativeDomain.Dispose(); }
    private string FileFor(string name, byte[] bytes)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void EnabledRawDescriptorRefusesTheWritableBindingAndRetainsTheMount()
    {
        var f = new Owner(true);
        var descriptor = MediaImageDescriptor.RawFloppy(new(1, 1, 1, 512));
        var bytes = new byte[512].AsSpan().ToImmutableArray();
        var path = FileFor("raw.bin", bytes.ToArray());
        var reply = f.Request(new(MediaAction.InsertDisk, DiskSlot.A, bytes, "raw.bin", Descriptor: descriptor));
        Assert.Null(reply.Error);
        f.Note(DiskSlot.A, path, bytes, reply, descriptor);
        var observation = Assert.Single(f.Host.CaptureMountObservations());
        Assert.Equal(DiskWritebackHost.MountBindingState.UnboundError, observation.BindingState);
        Assert.NotNull(observation.Error);
        Assert.Contains("raw sector-image writeback", observation.Error);
        Assert.Equal(MediaImageKind.RawFloppy, observation.Descriptor?.Kind);
        Assert.Equal(reply.A.Generation, observation.Generation);
        Assert.True(f.Request(new(MediaAction.Inspect)).A.Present); // Retained mount, refused binding.
        Assert.Equal(bytes.ToArray(), File.ReadAllBytes(path)); // Nothing written.
    }

    [Fact]
    public void DisabledRawDescriptorRecordsTheObservationWithoutAnError()
    {
        var f = new Owner(false);
        var descriptor = MediaImageDescriptor.RawFloppy(new(1, 1, 1, 512));
        var bytes = new byte[512].AsSpan().ToImmutableArray();
        var path = FileFor("raw.bin", bytes.ToArray());
        var reply = f.Request(new(MediaAction.InsertDisk, DiskSlot.A, bytes, "raw.bin", Descriptor: descriptor));
        Assert.Null(reply.Error);
        f.Note(DiskSlot.A, path, bytes, reply, descriptor);
        var observation = Assert.Single(f.Host.CaptureMountObservations());
        Assert.Equal(DiskWritebackHost.MountBindingState.Off, observation.BindingState);
        Assert.Null(observation.Error);
        Assert.Equal(MediaImageKind.RawFloppy, observation.Descriptor?.Kind);
        Assert.False(f.Host.ReportsTarget); // No lastError on the disabled path.
    }

    [Fact]
    public void ContainerDescriptorKeepsTheLegacyBind()
    {
        var f = new Owner(true);
        var bytes = ImmutableArray.CreateRange(Dsk.Create(new(40, 1, 1, 128), fill: 0xe5).Save());
        var path = FileFor("a.dsk", bytes.ToArray());
        var reply = f.Request(new(MediaAction.InsertDisk, DiskSlot.A, bytes, "a.dsk",
            Descriptor: MediaImageDescriptor.DskContainer));
        Assert.Null(reply.Error);
        f.Note(DiskSlot.A, path, bytes, reply, MediaImageDescriptor.DskContainer);
        var observation = Assert.Single(f.Host.CaptureMountObservations());
        Assert.Equal(DiskWritebackHost.MountBindingState.Bound, observation.BindingState);
        Assert.Null(observation.Error);
        Assert.Equal(MediaImageKind.DskContainer, observation.Descriptor?.Kind);
        Assert.Equal(reply.A.Generation, observation.Generation);
    }

    // Same FIFO/bridge/session boundary as the receipt tests; no manufactured replies.
    private sealed class Owner
    {
        private readonly InputQueue queue = new();
        private readonly MediaBridge bridge;
        private readonly InputController input;
        private readonly DeviceScheduler scheduler = new();
        private readonly FloppyDrive a = new(new());
        public FloppyController Board { get; }
        public DiskWritebackOwnerLedger Ledger { get; } = new();
        public DiskWritebackHost Host { get; }
        public Owner(bool enabled)
        {
            Board = new(scheduler, 10, a, new(new()));
            var session = new MediaSession(a, Board.DriveB);
            bridge = new(queue); Host = new(Ledger, bridge, enabled);
            input = new(queue, media: (command, _) => bridge.Publish(session.Apply(command)));
        }
        public MediaReply Request(MediaCommand command) => Finish(bridge.RequestAsync(command));
        public void Note(DiskSlot slot, string path, ImmutableArray<byte> bytes, MediaReply reply, MediaImageDescriptor? descriptor) =>
            Finish(Host.NoteMounted(slot, path, bytes, reply, null, descriptor));
        public T Finish<T>(Task<T> task) { Finish((Task)task); return task.GetAwaiter().GetResult(); }
        public void Finish(Task task)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Fixture FIFO did not settle.");
                input.ApplyPending(0, new KeyboardMatrix());
                Thread.Yield();
            }
            task.GetAwaiter().GetResult();
        }
    }
}
