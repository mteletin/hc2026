using System.Security.Cryptography;
using System.Text.Json;
using Hc2000.App.Configuration;
using Hc2000.App.Library;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.HostFiles;
using Hc2000.Machine;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>The media-home "Select as C:–F:" profile lane: a named hdd-container volume is recorded
/// in the machine draft (effective cold through the existing HddColdVolume/HddColdAdmission path);
/// an hdd-raw row is refused with the container-publication reason; Clear removes the entry.</summary>
public sealed class MediaHomeHddProfileLaneTests
{
    private const string Vol1Sha = "9D0ABB36878273883169C3756407D0658B4C8EC8E6B31CE35BC70F188BEE987F";

    private static MachineConfiguration Draft() => new() { Profile = "hc2000-v2-hdd-v1" };

    private static (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) Roms()
        => (MachineProfile.Hc2000V2, new byte[16384], new byte[16384], new byte[16384]);

    [Fact]
    public async Task SelectingAContainerRecordsTheVolumeAndColdStartPutsItAtC()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        var result = MediaHomeHddProfileLane.Select("hdd-container", fixture.PathName, "VOL1.DSK", "C", Draft());
        Assert.Equal(MediaHomeHddProfileOutcome.Selected, result.Outcome);
        var entry = Assert.Single(result.Draft.Hdd);
        Assert.Equal("C", entry.Slot);
        Assert.Equal(fixture.PathName, entry.Path);
        Assert.Equal("VOL1.DSK", entry.Volume);
        Assert.True(entry.WriteProtected);
        Assert.Equal("VOL1.DSK", result.Identity!.Name);
        // AC: after the cold restart the selected container's volume is at C: (drive 2).
        var prepared = await StartupHardwarePreparation.PrepareAsync(Roms(), result.Draft);
        var image = Assert.Single(prepared.InitialHdd);
        Assert.Equal(2, image.Drive);
        Assert.Equal("VOL1.DSK", image.Name);
        Assert.True(image.WriteProtected);
        Assert.Equal(Vol1Sha, Convert.ToHexString(SHA256.HashData(image.Bytes.AsSpan())));
    }

    [Fact]
    public void SelectingAnotherSlotMapsToThatDrive()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        var result = MediaHomeHddProfileLane.Select("hdd-container", fixture.PathName, "VOL2.DSK", "F", Draft());
        Assert.Equal(MediaHomeHddProfileOutcome.Selected, result.Outcome);
        var entry = Assert.Single(result.Draft.Hdd);
        Assert.Equal("F", entry.Slot);
        Assert.Equal("VOL2.DSK", entry.Volume);
    }

    [Fact]
    public void ReplacingTheSlotDoesNotDuplicateIt()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        var first = MediaHomeHddProfileLane.Select("hdd-container", fixture.PathName, "VOL1.DSK", "D", Draft());
        var replaced = MediaHomeHddProfileLane.Select("hdd-container", fixture.PathName, "VOL2.DSK", "D", first.Draft);
        Assert.Equal(MediaHomeHddProfileOutcome.Selected, replaced.Outcome);
        var entry = Assert.Single(replaced.Draft.Hdd);
        Assert.Equal("D", entry.Slot);
        Assert.Equal("VOL2.DSK", entry.Volume);
    }

    [Fact]
    public void ARawRowIsRefusedWithTheContainerPublicationReason()
    {
        if (!OperatingSystem.IsLinux()) return;
        var raw = WriteRaw8MiB();
        var draft = Draft();
        var result = MediaHomeHddProfileLane.Select("hdd-raw", raw, "VOL1.DSK", "C", draft);
        Assert.Equal(MediaHomeHddProfileOutcome.Refused, result.Outcome);
        Assert.Null(result.Identity); // The refusal must state the reason.
        Assert.Contains("container publication", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(draft, result.Draft); // Refused: the draft is untouched.
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("floppy-dsk")]
    [InlineData("tape")]
    [InlineData("unknown")]
    [InlineData("")]
    public void OtherRowKindsAreRefused(string kind)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = MediaHomeHddProfileLane.Select(kind, "/nonexistent.hdd", "VOL1.DSK", "C", Draft());
        Assert.Equal(MediaHomeHddProfileOutcome.Refused, result.Outcome);
        Assert.NotNull(result.Message);
        Assert.Null(result.Identity);
    }

    [Fact]
    public void AMissingVolumeNameIsRefusedWithTheAdmissionReason()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        var draft = Draft();
        var result = MediaHomeHddProfileLane.Select("hdd-container", fixture.PathName, "NOPE.DSK", "C", draft);
        Assert.Equal(MediaHomeHddProfileOutcome.Refused, result.Outcome);
        Assert.Contains("Named HDD volume not found", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(draft, result.Draft);
    }

    [Fact]
    public void AMultiVolumeContainerNeedsAnExplicitVolumeName()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        var result = MediaHomeHddProfileLane.Select("hdd-container", fixture.PathName, null, "C", Draft());
        Assert.Equal(MediaHomeHddProfileOutcome.Refused, result.Outcome);
        Assert.Contains("VOL1.DSK", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VOL2.DSK", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASingleVolumeContainerIsSelectedWithoutAName()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var single = new SingleVolumeFixture();
        var result = MediaHomeHddProfileLane.Select("hdd-container", single.PathName, null, "D", Draft());
        Assert.Equal(MediaHomeHddProfileOutcome.Selected, result.Outcome);
        var entry = Assert.Single(result.Draft.Hdd);
        Assert.Equal("D", entry.Slot);
        Assert.Equal("ONLY.DSK", entry.Volume);
        var prepared = await StartupHardwarePreparation.PrepareAsync(Roms(), result.Draft);
        var image = Assert.Single(prepared.InitialHdd);
        Assert.Equal(3, image.Drive); // D: is drive 3.
        Assert.Equal("ONLY.DSK", image.Name);
    }

    [Fact]
    public void ClearRemovesTheRecordedSlotAndIsAStableNoOp()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        var selected = MediaHomeHddProfileLane.Select("hdd-container", fixture.PathName, "VOL1.DSK", "C", Draft());
        var cleared = MediaHomeHddProfileLane.Clear(selected.Draft, "C");
        Assert.True(cleared.Hdd.IsEmpty);
        var again = MediaHomeHddProfileLane.Clear(cleared, "C");
        Assert.Same(cleared, again);
        Assert.True(again.Hdd.IsEmpty);
    }

    [Fact]
    public void TheMachineProfileRecordsTheChangeAcrossJson()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        var result = MediaHomeHddProfileLane.Select("hdd-container", fixture.PathName, "VOL1.DSK", "C", Draft());
        var json = JsonSerializer.Serialize(result.Draft);
        var roundTripped = JsonSerializer.Deserialize<MachineConfiguration>(json);
        Assert.NotNull(roundTripped);
        var entry = Assert.Single(roundTripped!.Hdd);
        Assert.Equal("C", entry.Slot);
        Assert.Equal("VOL1.DSK", entry.Volume);
        roundTripped.ValidateEffective();
    }

    [Fact]
    public void SlotsOutsideCtoFAreRefused()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var slot in new[] { "A", "B", "G", "c", "" })
        {
            var result = MediaHomeHddProfileLane.Select("hdd-container", "/nonexistent.hdd", "VOL1.DSK", slot, Draft());
            Assert.Equal(MediaHomeHddProfileOutcome.Refused, result.Outcome);
            Assert.NotNull(result.Message);
        }
    }

    [Fact]
    public async Task NamedVolumesDoNotMixWithRawSlotsOrConfiguredDisks()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        var raw = WriteRaw8MiB();
        var draft = Draft();
        var mixed = draft with
        {
            Hdd = [new("C", fixture.PathName, true, "VOL1.DSK"), new("D", raw, true)]
        };
        var mixedEx = await Assert.ThrowsAsync<IOException>(
            () => StartupHardwarePreparation.PrepareAsync(Roms(), mixed));
        Assert.Contains("raw 8 MiB slots", mixedEx.Message, StringComparison.OrdinalIgnoreCase);
        var withDisks = draft with
        {
            DiskA = new FloppyConfiguration(raw),
            Hdd = [new("C", fixture.PathName, true, "VOL1.DSK")]
        };
        var diskEx = await Assert.ThrowsAsync<IOException>(
            () => StartupHardwarePreparation.PrepareAsync(Roms(), withDisks));
        Assert.Contains("cold-read fence", diskEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string WriteRaw8MiB()
    {
        var directory = Directory.CreateTempSubdirectory("hc-raw").FullName;
        var path = Path.Combine(directory, "raw8mib.img");
        var bytes = new byte[8_388_608];
        bytes.AsSpan().Fill(0xE5);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>A one-volume HCVD container built through the production codec; its own private
    /// admission domain, exactly like <see cref="HddColdVolumeTests"/>.</summary>
    private sealed class SingleVolumeFixture : IDisposable
    {
        private readonly string directory;
        private readonly IDisposable domain;
        public string PathName { get; }
        public SingleVolumeFixture()
        {
            directory = Directory.CreateTempSubdirectory("hc-single").FullName;
            PathName = Path.Combine(directory, "single.hdd");
            var admissionDirectory = Path.Combine(directory, "admission");
            Directory.CreateDirectory(admissionDirectory);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(admissionDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            domain = NativeImageAdmission.EnterPrivateTestDomain(admissionDirectory);
            var payload = new byte[8_388_608];
            payload.AsSpan(0, 32768).Fill(0xE5);
            payload.AsSpan(32768, 4096).Fill(0x43);
            var encoded = HddCatalogCodec.EncodeHdd(
                [0x0A, 0x14, 0x1E, 0x28, 0x32, 0x3C, 0x46, 0x50, 0x5A, 0x64, 0x6E, 0x78, 0x82, 0x8C, 0x96, 0x9F],
                1,
                [new HddCatalogEntry("ONLY.DSK", true, 8_388_608, new HddSlotOrdinal(0), VolumeCatalogMetadata.Crc32(payload))],
                new HddLimits(uint.MaxValue, 511, 12_276, 114_640));
            Assert.True(encoded.Success, encoded.Failure?.Detail ?? "codec refused");
            File.WriteAllBytes(PathName, [.. payload, .. encoded.Value.Index, .. encoded.Value.Footer]);
        }
        public void Dispose()
        {
            domain.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
