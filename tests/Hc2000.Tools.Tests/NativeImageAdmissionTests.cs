using System.Runtime.Versioning;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hc2000.Machine.Storage;
using Hc2000.HostFiles;
using Xunit;
using static Hc2000.Tools.StorageCommand;
using static Hc2000.Tools.Tests.HddOfflineReplacementFixturesTests;

namespace Hc2000.Tools.Tests;

[Collection("Offline replacement private files")]
public sealed class NativeImageAdmissionTests
{
    // Domain is private tmpfs, not the user's real gate. Image originals/rollback remain
    // in the explicitly selected proof root. Unsupported is a failed precondition, not RED.
    [Theory, SupportedOSPlatform("linux")]
    [InlineData(OfflineIoPoint.SourceOpen)]
    [InlineData(OfflineIoPoint.BeforeMove)]
    [InlineData(OfflineIoPoint.AfterMove)]
    public async Task AdmittedWriterExcludesColdReadAtHeldRealPublicationBoundary(OfflineIoPoint point)
    {
        var domain = Path.Combine("/dev/shm", "hc2000-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(domain);
        File.SetUnixFileMode(domain, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var scope = NativeImageAdmission.EnterPrivateTestDomain(domain);
        try
        {
            var preflight = NativeImageAdmission.TryAcquireColdRead(default);
            Assert.Null(preflight.Refusal);
            Assert.NotNull(preflight.Admission);
            preflight.Admission.Dispose();
            var fixture = new PrivateCase("native-held-" + point);
            using var reached = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var writer = Task.Run(() => fixture.Call(Payload(0x43), io =>
            {
                if (io.Point != point) return;
                reached.Set();
                if (!release.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("Held publication was not drained by its test owner.");
            }));
            OfflineAttempt? completed = null;
            try
            {
                Assert.True(reached.Wait(TimeSpan.FromSeconds(30)), "Real writer did not reach the held boundary.");
                // The assertion happens while the actual writer is stopped, never from final reread.
                Assert.False(writer.IsCompleted);
                var reader = NativeImageAdmission.TryAcquireColdRead(default);
                using var accidentallyAdmitted = reader.Admission;
                Assert.Null(reader.Admission);
                Assert.Equal(ImageAdmissionRefusal.Busy, reader.Refusal);
                using var otherHost = Probe.Start("read", domain);
                var otherReader = await otherHost.Line();
                Assert.Equal("refused", otherReader.GetProperty("status").GetString());
                Assert.Equal("Busy", otherReader.GetProperty("reason").GetString());
                Assert.Equal(2, await otherHost.Exit());
                fixture.CopiesUnchanged();
            }
            finally
            {
                release.Set();
                completed = await writer.WaitAsync(TimeSpan.FromSeconds(30));
                try { fixture.New(completed.Receipt); }
                finally { completed.Dispose(); }
            }
            var afterDrain = NativeImageAdmission.TryAcquireColdRead(default);
            using var released = afterDrain.Admission;
            Assert.Null(afterDrain.Refusal);
            Assert.NotNull(released);
        }
        finally { Directory.Delete(domain, recursive: true); }
    }

    [Fact]
    public void SameThreadIndependentAcquisitionConflictsAndCancelDoesNotCreateGate()
    {
        using var domain = new Domain();
        using var scope = NativeImageAdmission.EnterPrivateTestDomain(domain.Path);
        var cancelled = NativeImageAdmission.TryAcquireColdRead(new CancellationToken(true));
        Assert.Equal(ImageAdmissionRefusal.Cancelled, cancelled.Refusal);
        Assert.Empty(Directory.GetFileSystemEntries(domain.Path));
        using (var first = AcquireRead())
        {
            var nested = NativeImageAdmission.TryAcquireColdRead(default);
            using var wrong = nested.Admission;
            Assert.Null(wrong);
            Assert.Equal(ImageAdmissionRefusal.Busy, nested.Refusal);
        }
        using var next = AcquireRead();
        AssertCleanRecord(domain);
    }

    [Theory]
    [InlineData("length")] [InlineData("crc")] [InlineData("version")]
    [InlineData("reserved")] [InlineData("state")] [InlineData("clean-owner")]
    [InlineData("domain")]
    public void InvalidDurableRecordIsNeverRepaired(string fault)
    {
        using var domain = new Domain();
        using var scope = NativeImageAdmission.EnterPrivateTestDomain(domain.Path);
        AcquireRead().Dispose();
        var record = ReadGate(domain.Gate);
        switch (fault)
        {
            case "length": record = record[..191]; break;
            case "crc": record[191] ^= 1; break;
            case "version": record[8] = 2; break;
            case "reserved": record[128] = 1; break;
            case "state": record[9] = 3; break;
            case "clean-owner": record[48] = 1; break;
            case "domain": record.AsSpan(16, 16).Clear(); break;
        }
        if (fault is not ("length" or "crc"))
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(188), VolumeCatalogMetadata.Crc32(record.AsSpan(0, 188)));
        File.WriteAllBytes(domain.Gate, record);
        for (var i = 0; i < 2; i++)
        {
            var refused = NativeImageAdmission.TryAcquireColdRead(default);
            using var wrong = refused.Admission;
            Assert.Null(wrong);
            Assert.Equal(ImageAdmissionRefusal.Quarantined, refused.Refusal);
            Assert.Equal(record, ReadGate(domain.Gate));
        }
    }

    [Fact]
    public async Task SeparateAppProcessUsesSameGateAndDrainsBeforeWriterCanEnter()
    {
        using var domain = new Domain();
        using var scope = NativeImageAdmission.EnterPrivateTestDomain(domain.Path);
        using (var parent = AcquireRead())
        using (var refused = Probe.Start("read", domain.Path))
        {
            var response = await refused.Line();
            Assert.Equal("refused", response.GetProperty("status").GetString());
            Assert.Equal("Busy", response.GetProperty("reason").GetString());
            Assert.Equal(2, await refused.Exit());
        }
        using var child = Probe.Start("pending-read", domain.Path);
        var held = await child.Line();
        Assert.Equal("held", held.GetProperty("status").GetString());
        using (var gate = OpenObservation(domain.Gate))
        {
            var id = LinuxImageAdmissionNative.Probe(gate).Id;
            Assert.Equal(id.Inode, held.GetProperty("Inode").GetUInt64());
            Assert.Equal(id.DeviceMajor, held.GetProperty("DeviceMajor").GetUInt32());
            Assert.Equal(id.DeviceMinor, held.GetProperty("DeviceMinor").GetUInt32());
        }
        var source = Path.Combine(domain.Path, "source.hdd");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        var writer = NativeImageAdmission.TryBeginPublication(source, Guid.NewGuid(), default);
        using var wrongWriter = writer.Admission;
        Assert.Null(wrongWriter);
        Assert.Equal(ImageAdmissionRefusal.Busy, writer.Refusal);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(source));
        await child.Drain();
        var after = NativeImageAdmission.TryBeginPublication(source, Guid.NewGuid(), default);
        using var admitted = after.Admission;
        Assert.Null(after.Refusal);
        Assert.NotNull(admitted);
        admitted.CompleteKnownPublicationAfterDrain(KnownImagePublication.Unpublished);
        AssertCleanRecord(domain);
    }

    [Theory]
    [InlineData("publication", 1)] [InlineData("unknown", 2)]
    public async Task OwnerProcessLossCannotEraseIntentOrUnknown(string mode, int state)
    {
        using var domain = new Domain();
        using var scope = NativeImageAdmission.EnterPrivateTestDomain(domain.Path);
        var source = Path.Combine(domain.Path, "source.hdd");
        File.WriteAllBytes(source, [11, 12, 13]);
        var attemptRecord = Array.Empty<byte>();
        using (var child = Probe.Start(mode, domain.Path, source))
        {
            var held = await child.Line();
            Assert.Equal("held", held.GetProperty("status").GetString());
            attemptRecord = ReadGate(domain.Gate);
            Assert.Equal(state, attemptRecord[9]);
            Assert.True(attemptRecord.AsSpan(32, 16).ContainsAnyExcept((byte)0));
            Assert.True(attemptRecord.AsSpan(48, 16).ContainsAnyExcept((byte)0));
            await child.KillAndDrain();
        }
        for (var i = 0; i < 2; i++)
        {
            var next = NativeImageAdmission.TryAcquireColdRead(default);
            using var wrong = next.Admission;
            Assert.Null(wrong);
            Assert.Equal(ImageAdmissionRefusal.Quarantined, next.Refusal);
            Assert.Equal(attemptRecord, ReadGate(domain.Gate));
            Assert.Equal(new byte[] { 11, 12, 13 }, File.ReadAllBytes(source));
        }
    }

    [Fact]
    public void PendingColdOwnerDisposalAndWrongCandidateCannotReleaseBeforeDrain()
    {
        using var domain = new Domain();
        using var scope = NativeImageAdmission.EnterPrivateTestDomain(domain.Path);
        var candidate = Guid.NewGuid();
        using var reader = AcquireRead();
        reader.MarkColdOwnerPending(candidate);
        reader.Dispose();
        Assert.Throws<InvalidOperationException>(() => reader.CompleteColdOwnerAfterDrain(Guid.NewGuid()));
        var competing = NativeImageAdmission.TryAcquireColdRead(default);
        using var wrong = competing.Admission;
        Assert.Null(wrong);
        Assert.Equal(ImageAdmissionRefusal.Busy, competing.Refusal);
        reader.CompleteColdOwnerAfterDrain(candidate);
        Assert.Throws<InvalidOperationException>(() => reader.MarkColdOwnerPending(candidate));
        for (var i = 0; i < 32; i++) AcquireRead().Dispose();
        AssertCleanRecord(domain);
        Assert.Single(Directory.GetFileSystemEntries(domain.Path));
    }

    [Fact]
    public void StaleOpenedSourceAndSymlinkRefuseWithoutChangingAnyBytes()
    {
        using var domain = new Domain();
        using var scope = NativeImageAdmission.EnterPrivateTestDomain(domain.Path);
        var path = Path.Combine(domain.Path, "image.hdd");
        var moved = Path.Combine(domain.Path, "old.hdd");
        var link = Path.Combine(domain.Path, "link.hdd");
        File.WriteAllBytes(path, [1, 2, 3]);
        using var reader = AcquireRead();
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var facts = reader.ValidateOpenedSource(source, path);
        File.CreateSymbolicLink(link, path);
        Assert.Throws<AdmissionException>(() => reader.ValidateOpenedSource(source, link));
        File.Move(path, moved);
        File.WriteAllBytes(path, [9, 8, 7]);
        Assert.Throws<AdmissionException>(() => reader.ValidateSourceStillCurrent(source, facts));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(moved));
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void HardLinkAndLexicalAliasCannotEscapeGlobalFenceAndMultiplyLinkedWriterRefuses()
    {
        using var domain = new Domain();
        using var scope = NativeImageAdmission.EnterPrivateTestDomain(domain.Path);
        var path = Path.Combine(domain.Path, "image.hdd");
        var alias = Path.Combine(domain.Path, "alias.hdd");
        File.WriteAllBytes(path, [4, 5, 6]);
        Assert.Equal(0, Link(path, alias));
        using (var reader = AcquireRead())
        {
            using var source = File.OpenRead(path);
            using var linked = File.OpenRead(alias);
            var original = reader.ValidateOpenedSource(source, path);
            Assert.Equal(original.FileId, reader.ValidateOpenedSource(linked, alias).FileId);
            foreach (var name in new[] { path, alias, Path.Combine(domain.Path, ".", "image.hdd") })
            {
                var result = NativeImageAdmission.TryBeginPublication(name, Guid.NewGuid(), default);
                using var wrong = result.Admission;
                Assert.Null(wrong);
                Assert.Equal(ImageAdmissionRefusal.Busy, result.Refusal);
            }
        }
        var unsupported = NativeImageAdmission.TryBeginPublication(alias, Guid.NewGuid(), default);
        using var denied = unsupported.Admission;
        Assert.Null(denied);
        Assert.Equal(ImageAdmissionRefusal.Unsupported, unsupported.Refusal);
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(path));
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(alias));
        AssertCleanRecord(domain);
    }

    [Fact]
    public void SimulatedLegacyPolicyRefusesNewAdmissionButPreservesExistingWriter()
    {
        using var domain = new Domain();
        using var scope = NativeImageAdmission.EnterPrivateTestDomain(domain.Path, ImageAdmissionPlatformMode.LegacyOnly);
        var fixture = new PrivateCase("legacy-policy");
        var cold = NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Null(cold.Admission); Assert.Equal(ImageAdmissionRefusal.Unsupported, cold.Refusal);
        var publication = NativeImageAdmission.TryBeginPublication(fixture.Target, Guid.NewGuid(), default);
        Assert.Null(publication.Admission); Assert.Equal(ImageAdmissionRefusal.Unsupported, publication.Refusal);
        Assert.Empty(Directory.GetFileSystemEntries(domain.Path));
        var moves = 0;
        using var actual = fixture.Call(Payload(0x43), io => { if (io.Point == OfflineIoPoint.BeforeMove) moves++; });
        Assert.Equal(1, moves);
        fixture.New(actual.Receipt);
        Assert.Empty(Directory.GetFileSystemEntries(domain.Path));
        // Simulated immutable policy routing only: this is NOT a native Windows run.
    }

    [Fact]
    public async Task ActualUnreadablePublicationRetainsNativeFenceUntilOwnedReconcileDrains()
    {
        using var domain = new Domain();
        using var scope = NativeImageAdmission.EnterPrivateTestDomain(domain.Path);
        var fixture = new PrivateCase("native-unknown");
        var fail = true;
        using var attempt = fixture.Call(Payload(0x43), io =>
        {
            if (io.Point == OfflineIoPoint.TargetReadback && fail) throw new IOException("Private readback fault.");
        });
        try
        {
            Assert.Equal(OfflineOutcome.Unknown, attempt.Receipt.Outcome);
            attempt.Dispose();
            Assert.Equal(2, ReadGate(domain.Gate)[9]);
            using var otherHost = Probe.Start("read", domain.Path);
            var refused = await otherHost.Line();
            Assert.Equal("refused", refused.GetProperty("status").GetString());
            Assert.Equal("Busy", refused.GetProperty("reason").GetString());
            Assert.Equal(2, await otherHost.Exit());
            fixture.CopiesUnchanged();
        }
        finally
        {
            fail = false;
            fixture.Reconcile(attempt);
        }
        fixture.New(attempt.Receipt);
        using var afterDrain = AcquireRead();
        AssertCleanRecord(domain);
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existing, string alias);

    // .NET File.Open/ReadAllBytes itself takes a flock on Unix and would interfere
    // with the real exclusive gate. Observation uses a distinct plain O_RDONLY FD;
    // it supplies no admission and never writes or clears the native record.
    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenObservation(string path)
    {
        var fd = Open(path, 0x80000 | 0x20000);
        Assert.True(fd >= 0, "Cannot observe private gate.");
        return new((IntPtr)fd, ownsHandle: true);
    }
    private static byte[] ReadGate(string path)
    {
        using var handle = OpenObservation(path);
        using var stream = new FileStream(handle, FileAccess.Read);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    private static NativeImageAdmission AcquireRead()
    {
        var result = NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Null(result.Refusal);
        Assert.NotNull(result.Admission);
        return result.Admission;
    }
    private static void AssertCleanRecord(Domain domain)
    {
        var bytes = ReadGate(domain.Gate);
        Assert.Equal(192, bytes.Length);
        Assert.Equal("HCADMIT1", Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.Equal(1, bytes[8]); Assert.Equal(0, bytes[9]);
        Assert.False(bytes.AsSpan(10, 6).ContainsAnyExcept((byte)0));
        Assert.True(bytes.AsSpan(16, 16).ContainsAnyExcept((byte)0));
        Assert.False(bytes.AsSpan(32, 156).ContainsAnyExcept((byte)0));
        Assert.Equal(VolumeCatalogMetadata.Crc32(bytes.AsSpan(0, 188)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(188)));
    }
    internal sealed class Domain : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hc2000-admission-" + Guid.NewGuid().ToString("N"));
        public string Gate => System.IO.Path.Combine(Path, "gate");
        public Domain()
        {
            Directory.CreateDirectory(Path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public IDisposable Enter() => NativeImageAdmission.EnterPrivateTestDomain(Path, OperatingSystem.IsLinux() ? ImageAdmissionPlatformMode.GuardedLinux : ImageAdmissionPlatformMode.LegacyOnly);
        public void Dispose() => Directory.Delete(Path, true);
    }
    private sealed class Probe : IDisposable
    {
        private readonly Process process;
        private Probe(Process process) => this.process = process;
        public static Probe Start(string mode, string domain, params string[] args)
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var project = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../..", "Hc2000.ImageAdmissionProbe"));
            var dll = System.IO.Path.Combine(project, "bin", configuration, "net10.0", "Hc2000.ImageAdmissionProbe.dll");
            Assert.True(File.Exists(dll), "Build the referenced real process probe first: " + dll);
            var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { dll, mode, domain }.Concat(args)) start.ArgumentList.Add(arg);
            return new(Process.Start(start) ?? throw new IOException("Probe did not start."));
        }
        public async Task<JsonElement> Line()
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(line);
            return JsonDocument.Parse(line).RootElement.Clone();
        }
        public async Task<int> Exit()
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            return process.ExitCode;
        }
        public async Task Drain()
        {
            await process.StandardInput.WriteLineAsync("drain");
            var drained = await Line();
            Assert.Equal("drained", drained.GetProperty("status").GetString());
            Assert.Equal(0, await Exit());
        }
        public async Task KillAndDrain()
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.HasExited);
        }
        public void Dispose()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                Assert.True(process.WaitForExit(30000), "Probe failed verified process drain.");
            }
            process.Dispose();
        }
    }
}
