using System.Collections.Immutable;
using System.Runtime.Versioning;
using Hc2000.App.Media;
using Hc2000.App.Input;
using Hc2000.App.Runtime;
using Hc2000.Machine;
using Hc2000.Machine.Floppy;
using Hc2000.HostFiles;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class HddColdAdmissionTests
{
    [Theory, SupportedOSPlatform("linux")]
    [InlineData(false, "busy")] [InlineData(true, "busy")]
    [InlineData(false, "record")] [InlineData(true, "record")]
    [InlineData(false, "domain")] [InlineData(true, "domain")]
    public async Task GuardedHelperRefusalCannotFallThroughToLegacyWriter(bool hdd, string refusal)
    {
        using var domain = new PrivateDomain();
        using var scope = domain.Enter();
        var target = Path.Combine(domain.Root, "image.raw");
        var bytes = ImmutableArray.CreateRange(new byte[hdd ? 8_388_608 : 3]);
        File.WriteAllBytes(target, [41, 42, 43]);
        NativeImageAdmission? holder = null;
        try
        {
            var first = NativeImageAdmission.TryAcquireColdRead(default);
            Assert.Null(first.Refusal); Assert.NotNull(first.Admission);
            if (refusal == "busy") holder = first.Admission;
            else
            {
                first.Admission.Dispose();
                if (refusal == "record") File.WriteAllBytes(Path.Combine(domain.Root, "gate"), [1, 2, 3]);
                else File.SetUnixFileMode(domain.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);
            }
            var ex = await Assert.ThrowsAsync<AdmissionException>(() => hdd ? HddFiles.SaveAsync(target, bytes) : MediaFiles.SaveAsync(target, bytes));
            Assert.Equal(refusal switch { "busy" => ImageAdmissionRefusal.Busy, "record" => ImageAdmissionRefusal.Quarantined, _ => ImageAdmissionRefusal.InvalidDomain }, ex.Refusal);
            Assert.Equal(new byte[] { 41, 42, 43 }, File.ReadAllBytes(target));
            Assert.Equal(new[] { "gate", "image.raw" }, Directory.GetFiles(domain.Root).Select(Path.GetFileName).Order().ToArray());
        }
        finally
        {
            holder?.Dispose();
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(domain.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SimulatedLegacyHelperStillSavesExactBytesWithoutNativeMarker(bool hdd)
    {
        using var domain = new PrivateDomain();
        using var scope = NativeImageAdmission.EnterPrivateTestDomain(domain.Root, ImageAdmissionPlatformMode.LegacyOnly);
        var target = Path.Combine(domain.Root, "image.raw");
        var original = new byte[hdd ? 8_388_608 : 3]; original[0] = 0x69; original[^1] = 0x96;
        var copied = ImmutableArray.CreateRange(original);
        Assert.Equal(ImageAdmissionRefusal.Unsupported, NativeImageAdmission.TryAcquireColdRead(default).Refusal);
        Assert.False(File.Exists(Path.Combine(domain.Root, "gate")));
        if (hdd) await HddFiles.SaveAsync(target, copied); else await MediaFiles.SaveAsync(target, copied);
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.Equal(new[] { "image.raw" }, Directory.GetFiles(domain.Root).Select(Path.GetFileName).ToArray());
        // This proves internal legacy routing on Linux, NOT native Windows execution.
    }

    [Fact]
    public void ActualColdFactoryRetainsFenceUntilOwnerDrainAndCopiesReadOnlyExactImage()
    {
        using var domain = new PrivateDomain();
        using var scope = domain.Enter();
        var path = CreateImage(domain);
        using var cold = HddColdAdmission.Open(path);
        var prepared = Prepare(cold);
        cold.Dispose(); // A prepared candidate is still capable of future adoption.
        AssertBusy(path);
        Hc2000.Machine.Floppy.BlockDiskPort? port = null;
        var observed = false;
        try
        {
            var machine = BasicMachineComposition.CreateColdNewMachine(prepared, new(), new(new()), new(new()), _ => { }, value =>
            {
                observed = true;
                AssertBusy(path); // REAL factory, before its attach; no surrogate owner/token.
                port = value;
            });
            Assert.True(observed); Assert.Equal(0, machine.MasterTick);
            Assert.NotNull(port);
            var image = port.CaptureHdd(2);
            Assert.True(image.Present); Assert.True(image.WriteProtected); Assert.False(image.Dirty);
            Assert.Equal(8_388_608, image.Bytes.Length);
            Assert.Equal(0x52, image.Bytes[0]); Assert.Equal(0xa7, image.Bytes[^1]);
            AssertBusy(path); // Factory return alone is not actual RuntimeContext installation.
            Assert.Throws<InvalidOperationException>(() => cold.CancelBeforeEnqueue());
            Exception? nonOwnerFailure = null;
            var nonOwner = new Thread(() => nonOwnerFailure = Record.Exception(cold.RejectAfterOwnerDrain)) { IsBackground = true };
            nonOwner.Start();
            Assert.True(nonOwner.Join(TimeSpan.FromSeconds(10)), "Non-owner refusal did not drain.");
            Assert.IsType<InvalidOperationException>(nonOwnerFailure);
            AssertBusy(path);
            var unusedSlot = new ComposedFloppySlot();
            Assert.Throws<InvalidOperationException>(() => BasicMachineComposition.CreateColdNewMachine(prepared, unusedSlot, new(new()), new(new()), _ => { }, _ => { }));
            Assert.Null(unusedSlot.Controller);
        }
        finally { cold.RejectAfterOwnerDrain(); } // Synchronous factory has returned; no future enqueue.
        AssertReadable();
        Assert.Equal(0x52, File.ReadAllBytes(path)[0]);
    }

    [Fact]
    public async Task ActualPausedRuntimeBoundaryAcknowledgesInstalledReadOnlyCandidate()
    {
        using var domain = new PrivateDomain();
        using var scope = domain.Enter();
        var path = CreateImage(domain);
        using var cold = HddColdAdmission.Open(path);
        var prepared = Prepare(cold);
        AssertBusy(path);
        var queue = new InputQueue();
        queue.Enqueue(new RuntimeControl(InputControlKind.Pause));
        var bridge = new MediaBridge(queue);
        using var worker = BasicMachineComposition.CreateWorker(prepared, queue, bridge);
        try
        {
            var installed = await bridge.RequestAsync(new(MediaAction.Hdd, Hdd: new(HddAction.Inspect))).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Null(installed.Error); Assert.NotNull(installed.Hdd);
            var drive = installed.Hdd.Drives[0];
            Assert.True(drive.Present); Assert.True(drive.WriteProtected); Assert.False(drive.Dirty);
            Assert.Equal("private-readonly.raw", drive.Name);
            AssertReadable(); // Acknowledgement came through the REAL worker RuntimeContext boundary.
            // Later native publications can enter only after this owner acknowledgement.
            var publication = NativeImageAdmission.TryBeginPublication(path, Guid.NewGuid(), default);
            using var admitted = publication.Admission;
            Assert.NotNull(admitted); Assert.Null(publication.Refusal);
            admitted.CompleteKnownPublicationAfterDrain(KnownImagePublication.Unpublished);
        }
        finally
        {
            worker.Dispose();
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        }
        AssertReadable();
    }

    [Fact]
    public void CancellationBeforeEnqueueReleasesButQueuedCreatorCannotAcknowledgeOwnerDrain()
    {
        using var domain = new PrivateDomain();
        using var scope = domain.Enter();
        var path = CreateImage(domain);
        using (var cancelled = HddColdAdmission.Open(path))
        {
            _ = Prepare(cancelled);
            cancelled.CancelBeforeEnqueue();
            AssertReadable();
        }
        using var enqueued = HddColdAdmission.Open(path);
        var prepared = Prepare(enqueued);
        enqueued.MarkEnqueued(prepared);
        try
        {
            enqueued.Dispose();
            Assert.Throws<InvalidOperationException>(() => enqueued.CancelBeforeEnqueue());
            Assert.Throws<InvalidOperationException>(() => enqueued.RejectAfterOwnerDrain());
            AssertBusy(path);
        }
        finally { enqueued.RejectBeforeOwnerStarted(); } // No worker was constructed: no future owner can start.
        AssertReadable();
    }

    [Fact]
    public void StaleCandidateRejectsWithoutChangingPreviouslyUsableMachineOrOriginalBytes()
    {
        using var domain = new PrivateDomain();
        using var scope = domain.Enter();
        var path = CreateImage(domain);
        var old = path + ".original";
        var usablePrepared = BasicMachineComposition.PrepareHddWholeChipV2(Roms(), [new(2, ImmutableArray.CreateRange(new byte[8_388_608]), "usable", true)]);
        BlockDiskPort? usablePort = null;
        var usable = BasicMachineComposition.CreateColdNewMachine(usablePrepared, new(), new(new()), new(new()), _ => { }, port => usablePort = port);
        Assert.NotNull(usablePort);
        var before = usablePort.CaptureHdd(2);
        using var cold = HddColdAdmission.Open(path);
        var stale = Prepare(cold);
        File.Move(path, old);
        File.WriteAllBytes(path, [9, 8, 7]);
        try
        {
            Assert.Throws<AdmissionException>(() => BasicMachineComposition.CreateColdNewMachine(stale, new(), new(new()), new(new()), _ => { }, _ => { }));
            AssertBusy(path);
            Assert.Equal(0, usable.MasterTick);
            var unchanged = usablePort.CaptureHdd(2);
            Assert.Equal(before.Bytes.ToArray(), unchanged.Bytes.ToArray());
            Assert.Equal(before.Generation, unchanged.Generation); Assert.Equal(before.WriteProtected, unchanged.WriteProtected);
            Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(path));
            var original = File.ReadAllBytes(old);
            Assert.Equal(8_388_608, original.Length); Assert.Equal(0x52, original[0]); Assert.Equal(0xa7, original[^1]);
        }
        finally { cold.RejectAfterOwnerDrain(); }
        AssertReadable();
    }

    private static (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) Roms() =>
        (MachineProfile.Hc2000V2, new byte[16384], new byte[16384], new byte[16384]);
    private static string CreateImage(PrivateDomain domain)
    {
        var path = Path.Combine(domain.Root, "private.raw");
        var bytes = new byte[8_388_608]; bytes[0] = 0x52; bytes[^1] = 0xa7;
        File.WriteAllBytes(path, bytes);
        return path;
    }
    private static PreparedBasicMachine Prepare(HddColdAdmission cold)
    {
        // Authored raw bytes exercise native owner lifetime only. Container selection remains
        // the existing caller's seven tests, not a duplicate parser/selector fixture here.
        var payload = new byte[8_388_608];
        cold.Source.ReadExactly(payload);
        return BasicMachineComposition.PrepareAdmittedHddWholeChipV2(Roms(),
            new(2, ImmutableArray.CreateRange(payload), "private-readonly.raw", true), cold);
    }
    private static void AssertBusy(string path)
    {
        var writer = NativeImageAdmission.TryBeginPublication(path, Guid.NewGuid(), default);
        using var wrong = writer.Admission;
        Assert.Null(wrong); Assert.Equal(ImageAdmissionRefusal.Busy, writer.Refusal);
    }
    private static void AssertReadable()
    {
        var reader = NativeImageAdmission.TryAcquireColdRead(default);
        using var result = reader.Admission;
        Assert.NotNull(result); Assert.Null(reader.Refusal);
    }

    internal sealed class PrivateScopedDomain : IDisposable
    {
        private readonly PrivateDomain domain = new();
        private readonly IDisposable scope;
        public PrivateScopedDomain() => scope = domain.Enter();
        public void Dispose() { scope.Dispose(); domain.Dispose(); }
    }

    internal sealed class PrivateDomain : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hc-app-admission-" + Guid.NewGuid().ToString("N"));
        public PrivateDomain()
        {
            Directory.CreateDirectory(Root);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public IDisposable Enter() => NativeImageAdmission.EnterPrivateTestDomain(Root,
            OperatingSystem.IsLinux() ? ImageAdmissionPlatformMode.GuardedLinux : ImageAdmissionPlatformMode.LegacyOnly);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
