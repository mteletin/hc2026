using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Hc2000.App.Input;
using Hc2000.App.Library;
using Hc2000.App.Media;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Xunit;
using IOFile = System.IO.File;

namespace Hc2000.App.Tests;

// The media home mount lane (contract hc2000-media-home-scan-admission-v1, section 3): a scanned
// floppy row is mounted as drive A: or B: through the existing disk library lane only -
// DiskLibraryCatalog.RegisterAsync (the existing closed admission, the scanned path as the
// original), the existing working copy lane, and DiskLibraryMount.MountAsync through the existing
// App media path. The scanned bytes are never the writable target and are never written; a refusal
// at any step leaves nothing mounted and the row untouched; eject goes through the existing
// MediaAction.EjectDisk. The witness is the same real surface as the mount tests: a real catalog,
// copy, snapshot, MediaSession on its owner thread and FDC drive, with the lane's dispatch and
// eject seams being the same MediaBridge.
#pragma warning disable CA1416
public sealed class MediaHomeMountLaneTests
{
    private sealed class SessionMachine : IDisposable
    {
        public DeviceScheduler Scheduler { get; } = new();
        public FloppyController Board { get; }
        public MediaBridge Bridge { get; }
        private readonly InputQueue _queue;
        private readonly InputController _controller;
        private readonly KeyboardMatrix _matrix = new();
        private readonly Thread _owner;
        private readonly MediaSession[] _session = [null!];
        private volatile bool _stop;

        public SessionMachine()
        {
            var a = new FloppyDrive(new());
            Board = new FloppyController(Scheduler, 10, a, new FloppyDrive(new()));
            _queue = new InputQueue();
            Bridge = new MediaBridge(_queue);
            _controller = new InputController(_queue, (_, _) => { }, (command, _) => Bridge.Publish(_session[0].Apply(command)));
            _owner = new Thread(() =>
            {
                _session[0] = new MediaSession(Board.DriveA, Board.DriveB);
                while (!_stop)
                {
                    try { _controller.ApplyPending(0, _matrix); }
                    catch { /* the pump only forwards media commands; the tests own every outcome */ }
                    Thread.Sleep(1);
                }
            })
            { IsBackground = true, Name = "media-home-mount-lane-owner" };
            _owner.Start();
        }

        public async Task<MediaReply> InspectAsync() => await Bridge.RequestAsync(new MediaCommand(MediaAction.Inspect));
        public async Task<MediaReply> InsertAsync(DiskSlot slot, ImmutableArray<byte> bytes, string name)
            => await Bridge.RequestAsync(new MediaCommand(MediaAction.InsertDisk, slot, bytes, name));
        public async Task<MediaReply> EjectAsync(DiskSlot slot)
            => await Bridge.RequestAsync(new MediaCommand(MediaAction.EjectDisk, slot, DiscardChanges: true));

        public void Dispose()
        {
            _stop = true;
            _owner.Join(TimeSpan.FromSeconds(2));
            Bridge.Disconnect();
        }
    }

    private sealed class Counts
    {
        public int Inspects;
        public int Dispatches;
        public int Ejects;
    }

    private static MediaHomeMountLane Lane(DiskLibraryCatalog catalog, string root, SessionMachine? machine, Counts counts)
    {
        var mount = new DiskLibraryMount(catalog, root)
        {
            Inspect = machine is null ? null : () =>
            {
                counts.Inspects++;
                return machine.InspectAsync();
            },
            DispatchInsert = machine is null ? null : (slot, bytes, name) =>
            {
                counts.Dispatches++;
                return machine.InsertAsync(slot, bytes, name);
            }
        };
        return new MediaHomeMountLane(catalog, root, mount)
        {
            DispatchEject = machine is null ? null : (slot, _) =>
            {
                counts.Ejects++;
                return machine.EjectAsync(slot);
            }
        };
    }

    private static byte[] ScannedRow(string path, byte fill = 0x77)
    {
        var bytes = Dsk.Create(new(40, 1, 1, 128), fill: fill).Save();
        var array = bytes.AsSpan().ToArray();
        IOFile.WriteAllBytes(path, array);
        return array;
    }

    // ExportMedia is a drive read; the owner pump is busy, so a one-shot thread reads the drive
    // directly between test steps, when the pump is idle.
    private static byte[] BoardExport(SessionMachine machine, DiskSlot slot)
    {
        using var done = new ManualResetEventSlim();
        byte[] exported = null!;
        var thread = new Thread(() =>
        {
            try { exported = (slot == DiskSlot.A ? machine.Board.DriveA : machine.Board.DriveB).ExportMedia(); }
            finally { done.Set(); }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        return exported;
    }

    // ExportMedia always emits an EXTENDED container, so equality is the parsed sector content, not
    // the file bytes: the mounted medium must carry exactly the scanned row's sectors.
    private static void AssertSameSectors(byte[] source, byte[] exported)
    {
        var s = Dsk.Load(source.AsSpan()); var m = Dsk.Load(exported.AsSpan());
        Assert.Equal(s.Tracks.Count, m.Tracks.Count);
        for (var i = 0; i < s.Tracks.Count; i++)
            for (var j = 0; j < s.Tracks[i].Sectors.Count; j++)
                Assert.Equal(s.Tracks[i].Sectors[j].Data.ToArray(), m.Tracks[i].Sectors[j].Data.ToArray());
    }

    // (a) The lane action: a scanned floppy row is registered (provenance media-home-scan, the
    // scanned path as the original), copied and mounted into A: through the existing one-action
    // mount; the board carries the scanned sectors and the scanned bytes never changed.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task AScannedRowMountsIntoADrivesThroughTheExistingLibraryLane()
    {
        using var fixtures = new DiskLibraryWorkingCopyFixtures();
        await fixtures.RegisterAsync();
        var scanned = Path.Combine(fixtures.Root, "scanned.dsk");
        var scannedBytes = ScannedRow(scanned);
        using var machine = new SessionMachine();
        var counts = new Counts();
        var lane = Lane(fixtures.Catalog, fixtures.Root, machine, counts);

        var result = await lane.MountFromScannedRowAsync(scanned, DiskSlot.A);

        Assert.Equal(MediaHomeMountOutcome.Mounted, result.Outcome);
        var entry = Assert.IsType<DiskLibraryEntry>(result.Entry);
        Assert.Equal("media-home-scan", entry.Provenance.Source);
        Assert.Equal(Path.GetFullPath(scanned), entry.SourcePath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(scannedBytes)), entry.Sha256);
        var copy = Assert.IsType<DiskLibraryCopy>(result.Copy);
        Assert.Equal(entry.Id, copy.EntryId);
        var mounted = Assert.IsType<DiskLibraryMountResult>(result.Mount);
        Assert.Equal(DiskLibraryMountOutcome.Mounted, mounted.Outcome);
        Assert.False(mounted.Status!.OriginalDispatchedAsTarget);
        Assert.Equal(1, counts.Dispatches);
        var after = await machine.InspectAsync();
        Assert.True(after.A.Present);
        Assert.Equal(Path.GetFileName(copy.RelativePath), after.A.Name);
        Assert.Equal(1, after.A.Generation);
        Assert.False(after.B.Present);
        AssertSameSectors(scannedBytes, BoardExport(machine, DiskSlot.A));
        // The scanned path was the original and was never a writable target.
        Assert.Equal(scannedBytes, IOFile.ReadAllBytes(scanned));
        fixtures.Preserved(priorCatalog: false);
    }

    // (b) The second lane: the same action into B:; both drives carry their own rows.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task ASecondScannedRowMountsIntoBLeavingAUntouched()
    {
        using var fixtures = new DiskLibraryWorkingCopyFixtures();
        await fixtures.RegisterAsync();
        var first = Path.Combine(fixtures.Root, "first.dsk");
        var second = Path.Combine(fixtures.Root, "second.dsk");
        var secondBytes = ScannedRow(second, fill: 0x99);
        ScannedRow(first);
        using var machine = new SessionMachine();
        var counts = new Counts();
        var lane = Lane(fixtures.Catalog, fixtures.Root, machine, counts);

        var r1 = await lane.MountFromScannedRowAsync(first, DiskSlot.A);
        var r2 = await lane.MountFromScannedRowAsync(second, DiskSlot.B);

        Assert.Equal(MediaHomeMountOutcome.Mounted, r1.Outcome);
        Assert.Equal(MediaHomeMountOutcome.Mounted, r2.Outcome);
        var after = await machine.InspectAsync();
        Assert.True(after.A.Present);
        Assert.True(after.B.Present);
        Assert.Equal(1, after.A.Generation);
        Assert.Equal(1, after.B.Generation);
        Assert.NotEqual(after.A.Name, after.B.Name);
        AssertSameSectors(secondBytes, BoardExport(machine, DiskSlot.B));
        Assert.Equal(secondBytes, IOFile.ReadAllBytes(second));
        fixtures.Preserved(priorCatalog: false);
    }

    // (c) The existing closed admission decides the row's kind: a linked row is refused at
    // registration, nothing is mounted, no candidate, and the row stays as it was.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task ALinkedRowIsRefusedAtRegistrationAndMountsNothing()
    {
        using var fixtures = new DiskLibraryWorkingCopyFixtures();
        await fixtures.RegisterAsync();
        var link = Path.Combine(fixtures.Root, "link.dsk");
        IOFile.CreateSymbolicLink(link, fixtures.Source);
        using var machine = new SessionMachine();
        var counts = new Counts();
        var lane = Lane(fixtures.Catalog, fixtures.Root, machine, counts);

        var result = await lane.MountFromScannedRowAsync(link, DiskSlot.A);

        Assert.Equal(MediaHomeMountOutcome.RegisterRefused, result.Outcome);
        Assert.Equal(DiskLibraryRefusal.FinalLink, result.RegisterRefusal);
        Assert.Null(result.Entry);
        Assert.Null(result.Copy);
        Assert.Null(result.Mount);
        Assert.Equal(0, counts.Inspects);
        Assert.Equal(0, counts.Dispatches);
        fixtures.NoCandidates();
        var after = await machine.InspectAsync();
        Assert.False(after.A.Present);
        Assert.False(after.B.Present);
        Assert.True(IOFile.Exists(link));
    }

    // (d) An 8 MiB raw HDD row gets no Mount: the existing closed admission refuses it by size;
    // no new kind logic, nothing mounted, and the bytes are untouched.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task AnHddSizedRowIsRefusedAtRegistrationAndMountsNothing()
    {
        using var fixtures = new DiskLibraryWorkingCopyFixtures();
        await fixtures.RegisterAsync();
        var raw = Path.Combine(fixtures.Root, "hdd8m.bin");
        var rawBytes = new byte[8 * 1024 * 1024];
        Array.Fill(rawBytes, (byte)0xe5);
        IOFile.WriteAllBytes(raw, rawBytes);
        using var machine = new SessionMachine();
        var counts = new Counts();
        var lane = Lane(fixtures.Catalog, fixtures.Root, machine, counts);

        var result = await lane.MountFromScannedRowAsync(raw, DiskSlot.B);

        Assert.Equal(MediaHomeMountOutcome.RegisterRefused, result.Outcome);
        Assert.Equal(DiskLibraryRefusal.TooLarge, result.RegisterRefusal);
        Assert.Null(result.Entry);
        Assert.Null(result.Copy);
        Assert.Null(result.Mount);
        Assert.Equal(0, counts.Inspects);
        Assert.Equal(0, counts.Dispatches);
        fixtures.NoCandidates();
        var after = await machine.InspectAsync();
        Assert.False(after.A.Present);
        Assert.False(after.B.Present);
        Assert.Equal(rawBytes, IOFile.ReadAllBytes(raw));
    }

    // (e) The same scanned row twice is the existing RepeatOrAlias refusal: nothing remounts, the
    // drive view is unchanged, and there is still exactly one copy and one dispatch.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task TheSameScannedRowTwiceIsRefusedAsRepeat()
    {
        using var fixtures = new DiskLibraryWorkingCopyFixtures();
        await fixtures.RegisterAsync();
        var scanned = Path.Combine(fixtures.Root, "scanned.dsk");
        ScannedRow(scanned);
        using var machine = new SessionMachine();
        var counts = new Counts();
        var lane = Lane(fixtures.Catalog, fixtures.Root, machine, counts);

        var first = await lane.MountFromScannedRowAsync(scanned, DiskSlot.A);
        var afterFirst = await machine.InspectAsync();
        var second = await lane.MountFromScannedRowAsync(scanned, DiskSlot.A);

        Assert.Equal(MediaHomeMountOutcome.Mounted, first.Outcome);
        Assert.Equal(MediaHomeMountOutcome.RegisterRefused, second.Outcome);
        Assert.Equal(DiskLibraryRefusal.RepeatOrAlias, second.RegisterRefusal);
        Assert.Null(second.Copy);
        Assert.Null(second.Mount);
        Assert.Equal(1, counts.Dispatches);
        Assert.Single(fixtures.Catalog.Copies);
        var after = await machine.InspectAsync();
        Assert.Equal(afterFirst.A.Name, after.A.Name);
        Assert.Equal(afterFirst.A.Generation, after.A.Generation);
        fixtures.Preserved(priorCatalog: false);
    }

    // (f) A second mount into the same clean drive is replaced per the existing rule: the drive
    // carries the second row at generation 2.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task ASecondMountIntoTheSameCleanDriveReplacesPerTheExistingRule()
    {
        using var fixtures = new DiskLibraryWorkingCopyFixtures();
        await fixtures.RegisterAsync();
        var first = Path.Combine(fixtures.Root, "first.dsk");
        var second = Path.Combine(fixtures.Root, "second.dsk");
        var secondBytes = ScannedRow(second, fill: 0x99);
        ScannedRow(first);
        using var machine = new SessionMachine();
        var counts = new Counts();
        var lane = Lane(fixtures.Catalog, fixtures.Root, machine, counts);

        var r1 = await lane.MountFromScannedRowAsync(first, DiskSlot.A);
        var r2 = await lane.MountFromScannedRowAsync(second, DiskSlot.A);

        Assert.Equal(MediaHomeMountOutcome.Mounted, r1.Outcome);
        Assert.Equal(MediaHomeMountOutcome.Mounted, r2.Outcome);
        var copy2 = Assert.IsType<DiskLibraryCopy>(r2.Copy);
        var after = await machine.InspectAsync();
        Assert.True(after.A.Present);
        Assert.Equal(Path.GetFileName(copy2.RelativePath), after.A.Name);
        Assert.Equal(2, after.A.Generation);
        Assert.Equal(2, counts.Dispatches);
        Assert.Equal(2, fixtures.Catalog.Copies.Count);
        AssertSameSectors(secondBytes, BoardExport(machine, DiskSlot.A));
        fixtures.Preserved(priorCatalog: false);
    }

    // (g) Eject goes through the existing App media path: the mounted drive is emptied; ejecting an
    // already empty drive is a no-op, not an error.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task EjectEmptiesTheMountedDriveThroughTheExistingMediaPath()
    {
        using var fixtures = new DiskLibraryWorkingCopyFixtures();
        await fixtures.RegisterAsync();
        var scanned = Path.Combine(fixtures.Root, "scanned.dsk");
        ScannedRow(scanned);
        using var machine = new SessionMachine();
        var counts = new Counts();
        var lane = Lane(fixtures.Catalog, fixtures.Root, machine, counts);

        var mounted = await lane.MountFromScannedRowAsync(scanned, DiskSlot.A);
        Assert.Equal(MediaHomeMountOutcome.Mounted, mounted.Outcome);
        var ejected = await lane.EjectAsync(DiskSlot.A);

        Assert.True(ejected.Ejected);
        var reply = Assert.IsType<MediaReply>(ejected.Reply);
        Assert.Null(reply.Error);
        Assert.False(reply.A.Present);
        Assert.Equal(2, reply.A.Generation);
        Assert.False(reply.B.Present);
        Assert.Equal(1, counts.Ejects);
        var after = await machine.InspectAsync();
        Assert.False(after.A.Present);
        Assert.Equal(2, after.A.Generation);
        var again = await lane.EjectAsync(DiskSlot.A);
        Assert.True(again.Ejected);
        Assert.Equal(2, counts.Ejects);
        fixtures.Preserved(priorCatalog: false);
    }

    // (h) Fail-closed: with the existing App media path not wired, eject refuses before dispatching
    // anything; the mounted drive stays mounted.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task AnUnwiredEjectRefusesFailClosed()
    {
        using var fixtures = new DiskLibraryWorkingCopyFixtures();
        await fixtures.RegisterAsync();
        var scanned = Path.Combine(fixtures.Root, "scanned.dsk");
        ScannedRow(scanned);
        using var machine = new SessionMachine();
        var counts = new Counts();
        var lane = Lane(fixtures.Catalog, fixtures.Root, machine, counts);

        var mounted = await lane.MountFromScannedRowAsync(scanned, DiskSlot.A);
        Assert.Equal(MediaHomeMountOutcome.Mounted, mounted.Outcome);
        lane.DispatchEject = null;
        var ejected = await lane.EjectAsync(DiskSlot.A);

        Assert.False(ejected.Ejected);
        Assert.Contains("not wired", ejected.Message);
        Assert.Null(ejected.Reply);
        Assert.Equal(0, counts.Ejects);
        var after = await machine.InspectAsync();
        Assert.True(after.A.Present);
        fixtures.Preserved(priorCatalog: false);
    }
}
