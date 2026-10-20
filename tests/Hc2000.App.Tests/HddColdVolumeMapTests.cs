using System.Collections.Immutable;
using System.Security.Cryptography;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.HostFiles;
using Hc2000.Machine;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class HddColdVolumeMapTests
{
    [Fact]
    public void OneAdmissionCanBindTwoExactReadOnlyDriveImages()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        using var admission = HddColdAdmission.Open(fixture.PathName);
        var bytes = new byte[HddMediaAdapter.ImageLength];
        admission.Source.ReadExactly(bytes);
        var prepared = new PreparedBasicMachine(
            (MachineProfile.Hc2000V2HddV1, new byte[16384], new byte[16384], new byte[16384]),
            [new(2, [.. bytes], "VOL1.DSK", true), new(5, [.. bytes], "VOL2.DSK", true)], admission);
        try { admission.BindPrepared(prepared); }
        finally { admission.CancelBeforeEnqueue(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestedMapInstallsAllNamedImagesOnOnePausedNewOwner(bool four)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        if (four) WriteFourVolumeLiteral(fixture.PathName);
        var before = Hash(fixture.PathName);
        ImmutableArray<HddColdVolumeRequest> requests = four
            ? [new(5, "vol1.dsk"), new(2, "VOL2.DSK"), new(4, "VOL3.DSK"), new(3, "VOL4.DSK")]
            : [new(5, "vol1.dsk"), new(2, "VOL2.DSK")];
        using var selected = HddColdVolume.OpenMapped(fixture.PathName, requests);
        var mapped = selected.MappedMedia;
        Assert.Equal(requests.Select(request => request.Drive), mapped.Select(image => image.Drive));
        Assert.Equal(requests.Select(request => request.Name.ToUpperInvariant()), selected.Identities.Select(identity => identity.Name));
        Assert.Throws<InvalidOperationException>(() => selected.Media(3)); // No silent single-image fallback.
        for (int i = 0; i < mapped.Length; i++)
        {
            Assert.True(mapped[i].WriteProtected); // Includes catalogue-writable VOL1 and VOL3.
            Assert.Equal(i % 2 == 0 ? 0x41 : 0x42, mapped[i].Bytes[32768]);
            Assert.Equal(i % 2 == 0
                ? "9D0ABB36878273883169C3756407D0658B4C8EC8E6B31CE35BC70F188BEE987F"
                : "F484C7D32E06CBA31D0DD508A1EBC7C26CC4ECDC18A738E040CC3D6BEF7F1EC3",
                Convert.ToHexString(SHA256.HashData(mapped[i].Bytes.AsSpan())));
        }
        var prepared = BasicMachineComposition.PrepareAdmittedHddMapWholeChipV2(Roms(), selected);
        selected.Dispose();
        Assert.Throws<ObjectDisposedException>(() => selected.MappedMedia);
        var blocked = NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Null(blocked.Admission); Assert.Equal(ImageAdmissionRefusal.Busy, blocked.Refusal);
        var queue = new InputQueue(); queue.Enqueue(new RuntimeControl(InputControlKind.Pause));
        var bridge = new MediaBridge(queue);
        using var worker = BasicMachineComposition.CreateWorker(prepared, queue, bridge);
        try
        {
            var reply = await bridge.RequestAsync(new(MediaAction.Hdd, Hdd: new(HddAction.Inspect))).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Null(reply.Error);
            foreach (var drive in reply.Hdd!.Drives)
            {
                var expected = mapped.SingleOrDefault(image => image.Drive == drive.Drive);
                Assert.Equal(expected is not null, drive.Present);
                if (expected is null) continue;
                Assert.Equal(expected.Name, drive.Name); Assert.True(drive.WriteProtected); Assert.False(drive.Dirty);
            }
            Assert.Null(worker.TakeLatestFrame()); // Owner installation, not guest/BIOS execution.
            AssertReadable();
            Assert.Throws<InvalidOperationException>(() => BasicMachineComposition.CreateWorker(prepared, new InputQueue()));
        }
        finally { worker.Dispose(); await worker.Completion.WaitAsync(TimeSpan.FromSeconds(15)); }
        Assert.Equal(before, Hash(fixture.PathName));
    }

    [Theory]
    [InlineData(2, 100729456UL)] // R=4S, E=2S, M=624, C=6S+65536.
    [InlineData(4, 167838768UL)] // R=4S, E=4S, M=1072, C=12S+65536; exceeds 128 MiB.
    public void AggregateBudgetAcceptsExactBoundaryAndRefusesOneByteLess(int count, ulong required)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        if (count == 4) WriteFourVolumeLiteral(fixture.PathName);
        var requests = Enumerable.Range(0, count).Select(i => new HddColdVolumeRequest((byte)(2 + i), $"VOL{i + 1}.DSK")).ToImmutableArray();
        var before = Hash(fixture.PathName);
        Assert.Throws<InvalidDataException>(() => HddColdVolume.OpenMapped(fixture.PathName, requests, required - 1));
        AssertReadable();
        using (var selected = HddColdVolume.OpenMapped(fixture.PathName, requests, required))
            Assert.Equal(count, selected.MappedMedia.Length);
        AssertReadable(); Assert.Equal(before, Hash(fixture.PathName));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale-store")]
    [InlineData("stale-payload")]
    [InlineData("wrong-name")]
    [InlineData("duplicate-name")]
    public void AnyBadMappingRefusesTheEntireSelectionAndReleasesAdmission(string fault)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        Hc2000.Machine.Storage.HddVolumeIdentity identity;
        using (var selected = HddColdVolume.Open(fixture.PathName, "VOL2.DSK")) identity = selected.Identity;
        var expected = fault switch
        {
            "stale-store" => identity with { Store = identity.Store with { Revision = identity.Store.Revision + 1 } },
            "stale-payload" => identity with { PayloadSha256 = [.. new byte[32]] },
            "wrong-name" => identity with { Name = "VOL1.DSK" },
            _ => identity
        };
        var name = fault switch { "missing" => "MISSING.DSK", "duplicate-name" => "vol1.dsk", _ => "VOL2.DSK" };
        var before = Hash(fixture.PathName);
        Assert.Throws<InvalidDataException>(() => HddColdVolume.OpenMapped(fixture.PathName,
            [new(2, "VOL1.DSK"), new(5, name, expected)]));
        AssertReadable(); Assert.Equal(before, Hash(fixture.PathName));
    }

    [Fact]
    public void InvalidDriveMapsRefuseBeforeOpeningAContainer()
    {
        const string missing = "/not/an/existing/hdd/container";
        Assert.Throws<ArgumentException>(() => HddColdVolume.OpenMapped(missing, default));
        Assert.Throws<ArgumentException>(() => HddColdVolume.OpenMapped(missing, []));
        Assert.Throws<ArgumentException>(() => HddColdVolume.OpenMapped(missing, [new(1, "VOL1.DSK")]));
        Assert.Throws<ArgumentException>(() => HddColdVolume.OpenMapped(missing, [new(6, "VOL1.DSK")]));
        Assert.Throws<ArgumentException>(() => HddColdVolume.OpenMapped(missing, [new(2, "VOL1.DSK"), new(2, "VOL2.DSK")]));
        Assert.Throws<ArgumentException>(() => HddColdVolume.OpenMapped(missing,
            [new(2, "VOL1.DSK"), new(3, "VOL2.DSK"), new(4, "VOL3.DSK"), new(5, "VOL4.DSK"), new(2, "VOL5.DSK")]));
        Assert.Throws<ArgumentOutOfRangeException>(() => HddColdVolume.OpenMapped(missing, [new(2, "VOL1.DSK")], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => HddColdVolume.OpenMapped(missing,
            [new(2, "VOL1.DSK")], HddColdVolume.DefaultMaxTotalLiveBytes + 1));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("writable")]
    [InlineData("duplicate-drive")]
    [InlineData("invalid-drive")]
    [InlineData("short")]
    [InlineData("default")]
    [InlineData("too-many")]
    public void OwnerAdmissionStillRejectsMalformedOrWritableMaps(string fault)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        using var admission = HddColdAdmission.Open(fixture.PathName);
        ImmutableArray<byte> bytes = [.. new byte[HddMediaAdapter.ImageLength]];
        HddInitialMedia first = new(2, bytes, "VOL1.DSK", true);
        HddInitialMedia second = new(5, bytes, "VOL2.DSK", true);
        ImmutableArray<HddInitialMedia> images = fault switch
        {
            "empty" => [],
            "writable" => [first, second with { WriteProtected = false }],
            "duplicate-drive" => [first, second with { Drive = 2 }],
            "invalid-drive" => [first, second with { Drive = 6 }],
            "short" => [first, second with { Bytes = [1] }],
            "default" => [first, second with { Bytes = default }],
            "too-many" => [first, second, first with { Drive = 3 }, first with { Drive = 4 }, second],
            _ => throw new InvalidOperationException()
        };
        var roms = Roms();
        var prepared = new PreparedBasicMachine((MachineProfile.Hc2000V2HddV1, roms.Basic, roms.Cpm, roms.If1), images, admission);
        Assert.Throws<InvalidOperationException>(() => admission.BindPrepared(prepared));
    }

    private static (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) Roms()
        => (MachineProfile.Hc2000V2, new byte[16384], new byte[16384], new byte[16384]);
    private static byte[] Hash(string path)
    {
        using var source = File.OpenRead(path);
        return SHA256.HashData(source);
    }
    private static void AssertReadable()
    {
        var result = NativeImageAdmission.TryAcquireColdRead(default);
        using var admission = result.Admission;
        Assert.NotNull(admission); Assert.Null(result.Refusal);
    }
    internal static void WriteFourVolumeLiteral(string path)
    {
        // Independent literal HCVD/footer (CRC32), not bytes emitted by the production codec.
        var first = new byte[HddMediaAdapter.ImageLength]; var second = new byte[first.Length];
        using (var source = File.OpenRead(path)) { source.ReadExactly(first); source.ReadExactly(second); }
        using var target = File.Create(path);
        target.Write(first); target.Write(second); target.Write(first); target.Write(second);
        target.Write(Convert.FromHexString("4843564401000400564f4c312020202044534b0000008000000000005ba36203564f4c322020202044534b010000800001000000b9eccf25564f4c332020202044534b0000008000020000005ba36203564f4c342020202044534b010000800003000000b9eccf2545e87ec2"));
        target.Write(Convert.FromHexString("4843564d31454e4401000000000000000102030405060708090a0b0c0d0e0f10010000000000000000000002000000006c00000045e87ec200000000a663f0aa"));
    }
}
