using System.Buffers.Binary;
using System.Security.Cryptography;
using Hc2000.HostFiles;
using Hc2000.Machine.Storage;
using Xunit;
using Xunit.Abstractions;
using static Hc2000.Tools.StorageCommand;

namespace Hc2000.Tools.Tests;

[Collection("Offline replacement private files")]
public sealed class HddOfflineReplaceCommandTests(ITestOutputHelper log) : IDisposable
{
    private const int Slot = 8_388_608;
    private const long FileBytes = 16_777_340;
    private const string IndexHex = "4843564401000200564f4c312020202044534b0000008000000000005ba36203564f4c322020202044534b010000800001000000b9eccf25da8245ce";
    private const string FooterHex = "4843564d31454e4401000000000000000102030405060708090a0b0c0d0e0f10010000000000000000000001000000003c000000da8245ce00000000b7cd3ab7";
    private const string ReplacedIndexHex = "4843564401000200564f4c312020202044534b000000800000000000d82b848e564f4c322020202044534b010000800001000000b9eccf254937d2b3";
    private const string ReplacedFooterHex = "4843564d31454e4401000000000000000102030405060708090a0b0c0d0e0f10020000000000000000000001000000003c0000004937d2b300000000001b8767";
    private const string FirstHash = "9D0ABB36878273883169C3756407D0658B4C8EC8E6B31CE35BC70F188BEE987F";
    private const string SecondHash = "F484C7D32E06CBA31D0DD508A1EBC7C26CC4ECDC18A738E040CC3D6BEF7F1EC3";
    private const string InputHash = "CB4AA887F4C3A25FCC9BDE077C2F981B21772BDC9407F7FE6ACF57EC8C1BB398";
    private const string MetadataHash = "AF3406F99F322A33D6AE6A52A5ECB66BA90342A271582CDCD5020273BDB0B5E2";
    private readonly NativeImageAdmissionTests.Domain domain = new();
    private IDisposable? nativeScope;

    private void EnterDomain()
    {
        nativeScope = domain.Enter();
        var admission = NativeImageAdmission.TryAcquireColdRead(default);
        using var probe = admission.Admission;
        Assert.Null(admission.Refusal);
        Assert.NotNull(probe); // unsupported admission is a setup failure, not command RED
    }

    public void Dispose() { nativeScope?.Dispose(); domain.Dispose(); }

    private (int Code, string Output, string Error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = StorageCommand.Run("hdd", args, output, error);
        log.WriteLine($"argv={System.Text.Json.JsonSerializer.Serialize(args)} exit={code} stdout={output} stderr={error}");
        return (code, output.ToString(), error.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ReplacesTheNamedExistingSlotWithIndependentWholeFileReadback(int selected)
    {
        EnterDomain();
        using var files = new PrivateFiles(log, secondWritable: selected == 1);
        // The ordinary raw input may be read-only; replacement must never unprotect or rewrite it.
        File.SetAttributes(files.Input, File.GetAttributes(files.Input) | FileAttributes.ReadOnly);
        var before = files.Snapshot();
        var result = Run("replace-volume", files.Target, selected == 0 ? "vol1.dsk" : "VOL2.DSK", files.Input);
        Assert.True(result.Code == 0, result.Error);
        Assert.Empty(result.Error);
        files.AssertReplaced(selected);
        files.AssertSnapshot(before, includeTarget: false);
        files.AssertNoResidue();
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(5)]
    public void MissingOrExtraPositionalsRefuseBeforeMutation(int count)
    {
        EnterDomain();
        using var files = new PrivateFiles(log);
        var complete = new[] { "replace-volume", files.Target, "VOL1.DSK", files.Input, "extra" };
        AssertRefusedUnchanged(files, complete.Take(count).ToArray());
    }

    [Theory]
    [InlineData("--force")] [InlineData("--unprotect")]
    public void NoForceOrUnprotectEscapeHatch(string option)
    {
        EnterDomain();
        using var files = new PrivateFiles(log);
        AssertRefusedUnchanged(files, "replace-volume", files.Target, "VOL1.DSK", files.Input, option);
    }

    [Theory]
    [InlineData("")] [InlineData("MISSING.DSK")] [InlineData("TOOLONGNAME.DSK")]
    [InlineData("../VOL1.DSK")] [InlineData("VOL?.DSK")]
    public void MissingUnknownOrMalformedNameDoesNotAppendOrPickFirst(string name)
    {
        EnterDomain();
        using var files = new PrivateFiles(log);
        AssertRefusedUnchanged(files, "replace-volume", files.Target, name, files.Input);
    }

    [Theory]
    [InlineData("container")] [InlineData("input")]
    public void MissingFileIsNotCreatedOrRepaired(string missing)
    {
        EnterDomain();
        using var files = new PrivateFiles(log);
        File.Delete(missing == "container" ? files.Target : files.Input);
        AssertRefusedUnchanged(files, "replace-volume", files.Target, "VOL1.DSK", files.Input);
    }

    [Theory]
    [InlineData(0)] [InlineData(Slot - 1)] [InlineData(Slot + 1)]
    public void InputMustBeExactlyEightMiBWithoutPaddingOrTruncation(int length)
    {
        EnterDomain();
        using var files = new PrivateFiles(log);
        using (var input = File.OpenWrite(files.Input)) input.SetLength(length);
        AssertRefusedUnchanged(files, "replace-volume", files.Target, "VOL1.DSK", files.Input);
    }

    [Fact]
    public void ProtectedVolumeDoesNotFallBackToWritableFirstVolume()
    {
        EnterDomain();
        using var files = new PrivateFiles(log);
        AssertRefusedUnchanged(files, "replace-volume", files.Target, "VOL2.DSK", files.Input);
    }

    [Fact]
    public void ReadOnlyContainerIsNotUnprotectedOrReplaced()
    {
        EnterDomain();
        using var files = new PrivateFiles(log);
        File.SetAttributes(files.Target, File.GetAttributes(files.Target) | FileAttributes.ReadOnly);
        AssertRefusedUnchanged(files, "replace-volume", files.Target, "VOL1.DSK", files.Input);
    }

    [Theory]
    [InlineData("short")] [InlineData("footer")] [InlineData("index")] [InlineData("payload")]
    public void MalformedContainerAndCorruptPayloadAreNotSilentlyRepaired(string damage)
    {
        EnterDomain();
        using var files = new PrivateFiles(log);
        using (var target = File.OpenWrite(files.Target))
        {
            if (damage == "short") target.SetLength(75);
            else
            {
                target.Position = damage switch { "footer" => FileBytes - 1, "index" => 2L * Slot + 8, _ => Slot + 32768 };
                target.WriteByte(0x99);
            }
        }
        AssertRefusedUnchanged(files, "replace-volume", files.Target, "VOL1.DSK", files.Input);
    }

    [Fact]
    public void OversizedSparseContainerRefusesWithoutChangingAnyBytes()
    {
        EnterDomain();
        using var files = new PrivateFiles(log);
        using (var target = File.OpenWrite(files.Target))
        {
            target.SetLength((long)uint.MaxValue + 1);
            target.Position = target.Length - 64;
            target.Write(Convert.FromHexString(FooterHex));
        }
        // Full streaming pre/post hashes include the sparse region, not merely sampled endpoints.
        AssertRefusedUnchanged(files, "replace-volume", files.Target, "VOL1.DSK", files.Input);
    }

    [Fact]
    public void HeldNativeColdReadRefusesTheCallerWithoutStealingAdmission()
    {
        EnterDomain();
        using var files = new PrivateFiles(log);
        var held = NativeImageAdmission.TryAcquireColdRead(default);
        using var reader = held.Admission;
        Assert.Null(held.Refusal);
        Assert.NotNull(reader);
        AssertRefusedUnchanged(files, "replace-volume", files.Target, "VOL1.DSK", files.Input);
        var stillHeld = NativeImageAdmission.TryAcquireColdRead(default);
        using var incorrectlyAdmitted = stillHeld.Admission;
        Assert.Null(incorrectlyAdmitted);
        Assert.Equal(ImageAdmissionRefusal.Busy, stillHeld.Refusal);
    }

    [Fact]
    public void ExistingUnknownPublicationIsNotReportedAsCallerSuccessOrBlindlyRetried()
    {
        EnterDomain();
        using var files = new PrivateFiles(log);
        var failReadback = true;
        files.Retain = true;
        using var attempt = ReplaceOffline(files.Request(), Payload(0x43), fault: io =>
        {
            if (io.Point == OfflineIoPoint.TargetReadback && failReadback)
                throw new IOException("owned command-fixture readback fault");
        });
        try
        {
            Assert.Equal(OfflineOutcome.Unknown, attempt.Receipt.Outcome);
            // Run has no fault parameter: this is a real existing Unknown holder followed by a
            // refused CLI invocation, not a claim to have injected Unknown into the CLI's own call.
            AssertRefusedUnchanged(files, "replace-volume", files.Target, "VOL1.DSK", files.Input);
            Assert.Equal(OfflineOutcome.Unknown, attempt.Receipt.Outcome);
        }
        finally
        {
            failReadback = false;
            var drained = attempt.Reconcile();
            files.Retain = drained.Outcome == OfflineOutcome.Unknown;
            Assert.Equal(OfflineOutcome.KnownNew, drained.Outcome);
        }
        files.AssertReplaced(0);
        files.AssertNoResidue();
    }

    private void AssertRefusedUnchanged(PrivateFiles files, params string[] args)
    {
        var before = files.Snapshot();
        var result = Run(args);
        Assert.NotEqual(0, result.Code);
        Assert.False(string.IsNullOrWhiteSpace(result.Error), "Refusal must report a diagnostic, not just a nonzero exit.");
        files.AssertSnapshot(before);
        files.AssertNoResidue();
    }

    private static byte[] Payload(byte value)
    {
        var bytes = new byte[Slot];
        bytes.AsSpan(0, 32768).Fill(0xe5);
        Convert.FromHexString("0053454544202020205458540000000108000000000000000000000000000000").CopyTo(bytes, 0);
        bytes.AsSpan(32768, 4096).Fill(value);
        return bytes;
    }

    // Tiny independent CRC oracle for the literal metadata, never the production codec/writer.
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }

    private static void Link(byte[] index, byte[] footer)
    {
        var crc = Crc(index.AsSpan(0, index.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(index.Length - 4), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(52), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(60), Crc(footer.AsSpan(0, 60)));
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record FileState(bool Exists, long Length, string Sha256, FileAttributes Attributes);

    private sealed class PrivateFiles : IDisposable
    {
        private readonly ITestOutputHelper log;
        private readonly byte[] index = Convert.FromHexString(IndexHex);
        private readonly byte[] footer = Convert.FromHexString(FooterHex);
        private readonly string root = Path.Combine(Path.GetTempPath(), "hc-replace-command-" + Guid.NewGuid().ToString("N"), "caller files");
        public string Target => Path.Combine(root, "candidate.hdd");
        private string Seed => Path.Combine(root, "retained-original.hdd");
        private string Rollback => Path.Combine(root, "rollback.hdd");
        public string Input => Path.Combine(root, "replacement.img");
        private string[] Paths => [Target, Seed, Rollback, Input];
        public bool Retain { get; set; }

        public PrivateFiles(ITestOutputHelper log, bool secondWritable = false)
        {
            this.log = log;
            Directory.CreateDirectory(root);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (secondWritable) { index[43] = 0; Link(index, footer); }
            var first = Payload(0x41); var second = Payload(0x42); var input = Payload(0x43);
            Assert.Equal(FirstHash, Convert.ToHexString(SHA256.HashData(first)));
            Assert.Equal(SecondHash, Convert.ToHexString(SHA256.HashData(second)));
            Assert.Equal(InputHash, Convert.ToHexString(SHA256.HashData(input)));
            using (var stream = File.Create(Seed))
            { stream.Write(first); stream.Write(second); stream.Write(index); stream.Write(footer); }
            File.Copy(Seed, Rollback); File.Copy(Seed, Target); File.WriteAllBytes(Input, input);
            log.WriteLine($"private root={root}; retained seed={Seed}; rollback={Rollback}; seed-sha256={Hash(Seed)}; input-sha256={InputHash}");
        }

        public Dictionary<string, FileState> Snapshot() => Paths.ToDictionary(p => p, p => File.Exists(p)
            ? new FileState(true, new FileInfo(p).Length, Hash(p), File.GetAttributes(p))
            : new FileState(false, 0, "", default));

        public void AssertSnapshot(Dictionary<string, FileState> before, bool includeTarget = true)
        {
            var after = Snapshot();
            foreach (var path in Paths.Where(p => includeTarget || p != Target)) Assert.Equal(before[path], after[path]);
        }

        public void AssertNoResidue() => Assert.Equal(Paths.Where(File.Exists).Select(Path.GetFileName).Order().ToArray(),
            Directory.GetFiles(root).Select(Path.GetFileName).Order().ToArray());

        public OfflineReplaceRequest Request() => new(Target,
            new HddVolumeIdentity(new HddStoreToken([.. Convert.FromHexString("0102030405060708090a0b0c0d0e0f10")], 1,
                [.. Convert.FromHexString(MetadataHash)]), "VOL1.DSK", [.. Convert.FromHexString(FirstHash)]),
            Guid.NewGuid(), Guid.NewGuid(), new(true, true, true, true),
            new((ulong)FileBytes, 2, 60, 624, Slot, 0, 2UL * Slot, 3UL * Slot + 32768,
                64UL * 1024 * 1024, 2UL * (ulong)FileBytes, 512UL * 1024 * 1024), new(1, 0, Slot + 80UL, 0));

        public void AssertReplaced(int selected)
        {
            using var stream = File.OpenRead(Target);
            Assert.Equal(FileBytes, stream.Length);
            for (var ordinal = 0; ordinal < 2; ordinal++)
            {
                var actual = new byte[Slot]; stream.ReadExactly(actual);
                Assert.Equal(Payload(ordinal == selected ? (byte)0x43 : ordinal == 0 ? (byte)0x41 : (byte)0x42), actual);
            }
            var actualIndex = new byte[60]; var actualFooter = new byte[64];
            stream.ReadExactly(actualIndex); stream.ReadExactly(actualFooter);
            var expectedIndex = index.ToArray(); var expectedFooter = footer.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(expectedIndex.AsSpan(8 + selected * 24 + 20), 0x8e842bd8);
            BinaryPrimitives.WriteUInt64LittleEndian(expectedFooter.AsSpan(32), 2);
            Link(expectedIndex, expectedFooter);
            Assert.Equal(expectedIndex, actualIndex); Assert.Equal(expectedFooter, actualFooter);
            Assert.Equal(Crc(actualIndex.AsSpan(0, 56)), BinaryPrimitives.ReadUInt32LittleEndian(actualIndex.AsSpan(56)));
            Assert.Equal(Crc(actualFooter.AsSpan(0, 60)), BinaryPrimitives.ReadUInt32LittleEndian(actualFooter.AsSpan(60)));
            Assert.Equal(footer.AsSpan(16, 16).ToArray(), actualFooter.AsSpan(16, 16).ToArray());
            Assert.Equal(2UL, BinaryPrimitives.ReadUInt64LittleEndian(actualFooter.AsSpan(32)));
            if (selected == 0 && index[43] == 1)
            {
                Assert.Equal(Convert.FromHexString(ReplacedIndexHex), actualIndex);
                Assert.Equal(Convert.FromHexString(ReplacedFooterHex), actualFooter);
            }
            Assert.Equal(FileBytes, stream.Position);
            log.WriteLine($"independent readback: slot={selected}, ContainerId unchanged, revision=2, exact index/footer/CRCs and both payloads; target-sha256={Hash(Target)}");
        }

        public void Dispose()
        {
            if (Retain) { log.WriteLine($"Unresolved owned publication retained at {root}; do not remove or abandon."); return; }
            foreach (var path in Paths.Where(File.Exists)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }
}
