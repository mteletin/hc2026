using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.HostFiles;
using Hc2000.Machine.Floppy;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class ExplorerWriterFenceTests
{
    [AvaloniaTheory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task ActualOwnerMountReceiptRefusesBeforeReadingOrEditing(bool writeback, bool identity, bool otherSession)
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var path = Source(domain.Root);
        var before = File.ReadAllBytes(path);
        var window = new MainWindow(new LaunchOptions(null, null, false, false));
        var mountedSession = window.CurrentSession;
        var host = new DiskWritebackHost(new DiskWritebackOwnerLedger(), new MediaBridge(mountedSession.InputQueue), writeback);
        mountedSession.WritebackHost = host;
        // The real bounded read, real drive owner and real host accounting produce the receipt.
        // No worker is booted in this fixture; it cannot invent any stopped/empty ownership authority.
        var operation = DiskReadReservation.Operation.ClaimStart([DiskSlot.A]);
        try
        {
            var read = await operation.ReadMemberAsync(DiskSlot.A, path);
            read.MarkDispatched();
            var owner = new MediaSession(new FloppyDrive(new()), new FloppyDrive(new()));
            var reply = owner.Apply(new(MediaAction.InsertDisk, Bytes: read.Bytes, RequestId: 1));
            Assert.Null(reply.Error);
            var readIdentity = read.CompleteAfterOwnerOutcome(reply);
            Assert.NotNull(readIdentity);
            await host.NoteMounted(DiskSlot.A, path, read.Bytes, reply, identity ? readIdentity : null);
        }
        finally { await operation.ReleaseOnceAsync("test host receipt accounted"); }
        Assert.Equal(writeback ? DiskWritebackHost.MountBindingState.Bound : DiskWritebackHost.MountBindingState.Off,
            Assert.Single(host.CaptureMountObservations()).BindingState);
        if (otherSession)
        {
            Assert.True(window.TryAddTabView(new(), out var tab, out var refusal), refusal);
            Assert.NotSame(mountedSession, window.CurrentSession);
            Assert.NotNull(tab);
        }
        var called = false;
        var result = window.WriteExplorerImage(path, bytes => { called = true; return bytes.SetItem(0, 42); });
        Assert.False(called);
        Assert.False(result.Published);
        Assert.Equal(writeback && identity ? ExplorerWriteRefusal.Mounted : ExplorerWriteRefusal.OwnershipUnknown, result.Refusal);
        Assert.Equal(before, File.ReadAllBytes(path));
        AssertNoTemporaryFiles(domain.Root);
        AssertGateFree();
    }

    [AvaloniaFact]
    public async Task PreparingRealHostReadMakesPublicationBusyBeforeEdit()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var path = Source(domain.Root);
        var before = File.ReadAllBytes(path);
        var window = new MainWindow(new LaunchOptions(null, null, false, false));
        var operation = DiskReadReservation.Operation.ClaimStart([DiskSlot.A]);
        try
        {
            await operation.ReadMemberAsync(DiskSlot.A, path);
            var called = false;
            var result = window.WriteExplorerImage(path, bytes => { called = true; return bytes.SetItem(0, 42); });
            Assert.False(called);
            Assert.False(result.Published);
            Assert.Equal(ExplorerWriteRefusal.NativeAdmission, result.Refusal);
            Assert.Contains("Busy", result.Message);
            Assert.Equal(before, File.ReadAllBytes(path));
            AssertNoTemporaryFiles(domain.Root);
        }
        finally { await operation.ReleaseOnceAsync("preparing host read test drain"); }
        AssertGateFree();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void HardlinkOrSymlinkTargetCannotBypassAdmission(bool symbolic)
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var path = Source(domain.Root);
        var before = File.ReadAllBytes(path);
        var alias = Path.Combine(domain.Root, "alias.dsk");
        if (symbolic) File.CreateSymbolicLink(alias, path);
        else Assert.Equal(0, Link(path, alias));
        var window = new MainWindow(new LaunchOptions(null, null, false, false));
        var called = false;
        var result = window.WriteExplorerImage(alias, bytes => { called = true; return bytes.SetItem(0, 42); });
        Assert.False(called);
        Assert.False(result.Published);
        Assert.Equal(ExplorerWriteRefusal.NativeAdmission, result.Refusal);
        Assert.Equal(before, File.ReadAllBytes(path));
        AssertNoTemporaryFiles(domain.Root);
        AssertGateFree();
    }

    [AvaloniaFact]
    public void OfflineWriteKeepsAdmissionThroughEditAndPublishesThenReleases()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var path = Source(domain.Root);
        var before = File.ReadAllBytes(path);
        var window = new MainWindow(new LaunchOptions(null, null, false, false));
        var result = window.WriteExplorerImage(path, bytes =>
        {
            var competingRead = NativeImageAdmission.TryAcquireColdRead(default);
            competingRead.Admission?.Dispose();
            Assert.Equal(ImageAdmissionRefusal.Busy, competingRead.Refusal);
            return bytes.SetItem(bytes.Length - 1, 42);
        });
        Assert.True(result.Published, result.Message);
        Assert.Null(result.Refusal);
        Assert.Contains("External processes", result.Message);
        before[^1] = 42;
        Assert.Equal(before, File.ReadAllBytes(path));
        AssertNoTemporaryFiles(domain.Root);
        AssertGateFree();
    }

    private static string Source(string root)
    {
        var path = Path.Combine(root, "source.dsk");
        File.WriteAllBytes(path, DiskLibraryCatalogLiteralFixtures.Standard());
        return path;
    }

    private static void AssertGateFree()
    {
        var acquired = NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Null(acquired.Refusal);
        Assert.NotNull(acquired.Admission);
        acquired.Admission.Dispose();
    }

    private static void AssertNoTemporaryFiles(string root) => Assert.Empty(Directory.GetFiles(root, "*.tmp"));

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existing, string alias);
}
