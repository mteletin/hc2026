using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using Hc2000.App;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class DiskWritebackNativeTests : IDisposable
{
    private readonly HddColdAdmissionTests.PrivateDomain nativeDomain = new();
    private readonly IDisposable nativeScope;
    public DiskWritebackNativeTests() => nativeScope = nativeDomain.Enter();
    public void Dispose() { nativeScope.Dispose(); nativeDomain.Dispose(); }
    [Fact, SupportedOSPlatform("linux")]
    public void LinuxFactsAreRealAndDoNotFollowASymlink()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hc2000-wb-" + Guid.NewGuid().ToString("N")));
        try
        {
            var file = Path.Combine(root.FullName, "disk.dsk");
            File.WriteAllBytes(file, [1, 2, 3, 4]);
            var fact = LinuxFileFacts.Inspect(file);
            Assert.True(fact.Known);
            Assert.True(fact.Regular);
            Assert.False(fact.Link);
            Assert.True(fact.Writable);
            Assert.Matches("^[0-9a-f]+:[0-9a-f]+:[0-9a-f]+$", fact.FileId);
            var link = Path.Combine(root.FullName, "alias.dsk");
            File.CreateSymbolicLink(link, file);
            var linked = LinuxFileFacts.Inspect(link);
            Assert.True(linked.Known);
            Assert.True(linked.Link);
            Assert.False(linked.Regular);
            Assert.Null(linked.FileId);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void SessionFileIsOverriddenByTheCommandLineAndHelpDoesNotRead()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hc2000-wb-" + Guid.NewGuid().ToString("N")));
        try
        {
            var session = Path.Combine(root.FullName, "session.json");
            File.WriteAllText(session, """{"version":1,"disks":{"B":"from-session.dsk"},"writeback":true}""");
            var loader = new SessionLaunchConfigLoader(_ => File.ReadAllBytes(session), () => [root.FullName]);
            var parsed = LaunchOptions.Parse(["--disk", "B:from-cli.dsk", "--no-writeback", "--session", session]);
            Assert.True(parsed.Ok);
            var applied = LaunchStartup.ApplySession(parsed.Options!, loader);
            Assert.True(applied.Ok);
            Assert.Equal("B:from-cli.dsk", applied.Options!.DiskB);
            Assert.False(applied.Options.Writeback);
            var help = LaunchStartup.ApplySession(new(null, null, false, true),
                new SessionLaunchConfigLoader(_ => throw new InvalidOperationException("help read"), () => [root.FullName]));
            Assert.True(help.Ok);
            Assert.True(help.Options!.Help);
        }
        finally { root.Delete(true); }
    }

    [Fact, SupportedOSPlatform("linux")]
    public async Task WriteOnBLandsAfterEjectOnlyWhenWritebackIsEnabled()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hc2000-wb-" + Guid.NewGuid().ToString("N")));
        try
        {
            var path = Path.Combine(root.FullName, "b.dsk");
            var original = MediaSessionTests.Disk();
            await File.WriteAllBytesAsync(path, original.ToArray());
            await using var enabled = await NativeLoop.Start(path, writeback: true);
            await enabled.WriteSectorAsync();
            await enabled.Host.ShortcutAsync();
            Assert.Equal(0x72, Dsk.Load(await File.ReadAllBytesAsync(path)).Tracks[0].Sectors[0].Data[0]);
            Assert.Null(await enabled.Host.BeforeCloseAsync());
            Assert.True(await enabled.Host.TryEjectAsync(DiskSlot.B));
            Assert.Equal(0x72, Dsk.Load(await File.ReadAllBytesAsync(path)).Tracks[0].Sectors[0].Data[0]);

            await File.WriteAllBytesAsync(path, original.ToArray());
            await using var disabled = await NativeLoop.Start(path, writeback: false);
            await disabled.WriteSectorAsync();
            Assert.False(await disabled.Host.TryEjectAsync(DiskSlot.B));
            Assert.Equal(original, ImmutableArray.CreateRange(await File.ReadAllBytesAsync(path)));
        }
        finally { root.Delete(true); }
    }

    [Fact, SupportedOSPlatform("linux")]
    public async Task UnwritableTargetIsReportedAndThePreviousBytesRemain()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hc2000-wb-" + Guid.NewGuid().ToString("N")));
        try
        {
            var path = Path.Combine(root.FullName, "b.dsk");
            var original = MediaSessionTests.Disk();
            await File.WriteAllBytesAsync(path, original.ToArray());
            await using var loop = await NativeLoop.Start(path, writeback: true);
            File.SetUnixFileMode(path, UnixFileMode.UserRead);
            await loop.WriteSectorAsync();
            Assert.True(await loop.Host.TryEjectAsync(DiskSlot.B));
            Assert.Contains("unwritable", loop.Host.StatusLine(), StringComparison.OrdinalIgnoreCase);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal(original, ImmutableArray.CreateRange(await File.ReadAllBytesAsync(path)));
        }
        finally { root.Delete(true); }
    }

    private sealed class NativeLoop : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop;
        private readonly Task pump;
        private readonly ConcurrentQueue<Action> ownerWork;
        private readonly FloppyController board;
        private readonly DeviceScheduler scheduler;
        private readonly FloppyDrive driveB;
        public DiskWritebackHost Host { get; }

        private NativeLoop(Task pump, DiskWritebackHost host, CancellationTokenSource stop, ConcurrentQueue<Action> ownerWork,
            FloppyController board, DeviceScheduler scheduler, FloppyDrive driveB)
        {
            this.pump = pump;
            Host = host;
            this.stop = stop;
            this.ownerWork = ownerWork;
            this.board = board;
            this.scheduler = scheduler;
            this.driveB = driveB;
        }

        public static async Task<NativeLoop> Start(string path, bool writeback)
        {
            var ready = new TaskCompletionSource<NativeLoop>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pump = Task.Run(() =>
            {
                var stop = new CancellationTokenSource();
                var ownerWork = new ConcurrentQueue<Action>();
                var scheduler = new DeviceScheduler();
                var driveB = new FloppyDrive(new());
                var board = new FloppyController(scheduler, 10, new FloppyDrive(new()), driveB);
                var session = new MediaSession(board.DriveA, driveB);
                var queue = new InputQueue();
                var bridge = new MediaBridge(queue);
                var ledger = new DiskWritebackOwnerLedger();
                var host = new DiskWritebackHost(ledger, bridge, writeback);
                var loop = new NativeLoop(Task.CompletedTask, host, stop, ownerWork, board, scheduler, driveB);
                var input = new InputController(queue, media: (command, _) =>
                {
                    if (command.Action == MediaAction.Writeback || ledger.Frozen && command.Action != MediaAction.Inspect)
                    {
                        var viewed = session.Apply(new(MediaAction.Inspect));
                        var owner = command.Action == MediaAction.Writeback
                            ? ledger.Apply(command.Writeback, session, null, queue)
                            : new WritebackOwnerReply(false, true, Error: "Writeback freeze: media change refused.");
                        bridge.Publish(viewed with { RequestId = command.RequestId, Error = owner.Error, Writeback = owner });
                        return;
                    }
                    bridge.Publish(session.Apply(command) with { RequestId = command.RequestId });
                });
                var bytes = ImmutableArray.CreateRange(File.ReadAllBytes(path));
                var mounted = session.Apply(new(MediaAction.InsertDisk, DiskSlot.B, bytes, "b.dsk"));
                host.NoteMounted(DiskSlot.B, path, bytes, mounted).GetAwaiter().GetResult();
                ready.TrySetResult(loop);
                while (!stop.IsCancellationRequested)
                {
                    input.ApplyPending(0, new KeyboardMatrix());
                    while (ownerWork.TryDequeue(out var work)) work();
                    Thread.Sleep(1);
                }
            });
            var started = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return new NativeLoop(pump, started.Host, started.stop, started.ownerWork, started.board, started.scheduler, started.driveB);
        }

        public async Task WriteSectorAsync()
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ownerWork.Enqueue(() =>
            {
                try
                {
                    board.WriteLatch(0x1c); // select B, motor held, not reset
                    foreach (var value in new byte[] { 3, 0xdf, 1, 0x45, 1, 0, 0, 1, 0, 1, 0x2a, 0x80 })
                        board.Fdc.WriteData(value);
                    Ready();
                    board.Fdc.WriteData(0x72);
                    board.WriteLatch(0x1d);
                    Ready();
                    while (board.Fdc.Phase == FdcPhase.Result) board.Fdc.ReadData();
                    board.WriteLatch(0x10);
                    var events = 0;
                    while (driveB.HeadLoaded && ++events < 1000)
                        scheduler.AdvanceTo(scheduler.PendingEvents.Min(e => e.DueTick));
                    done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
            });
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        private void Ready()
        {
            var count = 0;
            while ((board.Fdc.ReadStatus() & 0x80) == 0 && ++count < 1000)
                scheduler.AdvanceTo(scheduler.PendingEvents.Min(e => e.DueTick));
            if ((board.Fdc.ReadStatus() & 0x80) == 0) throw new TimeoutException("FDC data not ready.");
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            try { await pump.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { }
        }
    }
}
