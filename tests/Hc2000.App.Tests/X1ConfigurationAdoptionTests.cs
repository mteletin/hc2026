using System.IO;
using System.Text.Json;
using Hc2000.App.Configuration;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.Machine;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>X1 leaf A (hc2000-x1-app-config-adoption): the additive MachineConfiguration.TextOverlay field
/// (JSON "textOverlay", camelCase, default off) is adopted at cold startup only — StartupHardwarePreparation
/// passes it to the BasicMachineComposition.Prepare*WholeChipV2 helpers, which derive the profile's video
/// with the overlay on. A live change is refused by ReconfigureApply; the static MachineProfile constants
/// are never mutated.</summary>
public sealed class X1ConfigurationAdoptionTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc2000-x1-config-adoption-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingTextOverlayKeyDeserializesToOff()
    {
        var json = """
            {"id":"m1","name":"Machine","profile":"hc2000-v2","speedPercent":100,"scale":2}
            """;
        var loaded = JsonSerializer.Deserialize<MachineConfiguration>(json, Json)!;
        Assert.False(loaded.TextOverlay);
    }

    [Fact]
    public void TextOverlayRoundTripsThroughJsonUnderCamelCaseKey()
    {
        var on = JsonSerializer.Serialize(new MachineConfiguration { Name = "A", TextOverlay = true }, Json);
        Assert.Contains("\"textOverlay\":true", on);
        Assert.True(JsonSerializer.Deserialize<MachineConfiguration>(on, Json)!.TextOverlay);

        var off = JsonSerializer.Serialize(new MachineConfiguration { Name = "B", TextOverlay = false }, Json);
        Assert.Contains("\"textOverlay\":false", off);
        Assert.False(JsonSerializer.Deserialize<MachineConfiguration>(off, Json)!.TextOverlay);
    }

    [Fact]
    public void TwoIndependentMachinesKeepSeparateOverlayChoices()
    {
        var a = new MachineConfiguration { Name = "A", TextOverlay = true };
        var b = new MachineConfiguration { Name = "B" };
        Assert.True(a.TextOverlay); Assert.False(b.TextOverlay);
        b = b with { TextOverlay = a.TextOverlay };
        Assert.True(b.TextOverlay); Assert.True(a.TextOverlay);
    }

    [Fact]
    public void LiveChangeOfTextOverlayIsRefusedAsColdHardwareChoice()
    {
        var current = new MachineConfiguration { Name = "M", TextOverlay = true };
        var draft = current with { TextOverlay = false };
        Assert.False(ReconfigureApply.TryPlan(current, draft, out var offError));
        Assert.Contains("cold", offError!); Assert.Contains("new machine", offError, StringComparison.OrdinalIgnoreCase);

        var backOn = new MachineConfiguration { Name = "M", TextOverlay = false };
        Assert.False(ReconfigureApply.TryPlan(backOn, backOn with { TextOverlay = true }, out var onError));
        Assert.Contains("cold", onError!); Assert.Contains("new machine", onError, StringComparison.OrdinalIgnoreCase);

        // An unchanged draft still plans, so the refusal is the overlay delta itself.
        Assert.True(ReconfigureApply.TryPlan(current, current with { SpeedPercent = 200 }, out var error), error);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("hc2000-v2")]
    [InlineData("hc2000-v2-ram-banks-v1")]
    [InlineData("hc2000-v2-paged-v1")]
    [InlineData("hc2000-v2-paged-m-v1")]
    public async Task ColdStartupAdoptsOverlayForEveryImplementedRamProfile(string profile)
    {
        var prepared = await StartupHardwarePreparation.PrepareAsync(StartupHardwareTests.Roms(), new()
        {
            Profile = profile, TextOverlay = true
        });
        Assert.True(prepared.Roms.Profile.Video.TextOverlay);
        Assert.Equal(profile, prepared.Roms.Profile.Name);
        prepared.Roms.Profile.Validate();
        StaticProfilesRemainUnmutated();
    }

    [Fact]
    public async Task ColdStartupAdoptsOverlayForHddProfileAndDerivesTheHddProfile()
    {
        var prepared = await StartupHardwarePreparation.PrepareAsync(StartupHardwareTests.Roms(), new()
        {
            Profile = "hc2000-v2-hdd-v1", TextOverlay = true, Hdd = [new("C", Image("c.raw"), false)]
        });
        Assert.True(prepared.Roms.Profile.Video.TextOverlay);
        Assert.Equal("hc2000-v2-hdd-v1", prepared.Roms.Profile.Name);
        prepared.Roms.Profile.Validate();
        StaticProfilesRemainUnmutated();
    }

    [Fact]
    public async Task ColdStartupKeepsOverlayOffWhenTheChoiceIsAbsent()
    {
        var prepared = await StartupHardwarePreparation.PrepareAsync(StartupHardwareTests.Roms(), new() { Profile = "hc2000-v2" });
        Assert.False(prepared.Roms.Profile.Video.TextOverlay);
        StaticProfilesRemainUnmutated();
    }

    private static void StaticProfilesRemainUnmutated()
    {
        // The adopted machines carry derived profiles; the shared statics keep the faithful default off.
        Assert.False(MachineProfile.Hc2000V2.Video.TextOverlay);
        Assert.False(MachineProfile.Hc2000V2RamBanksV1.Video.TextOverlay);
        Assert.False(MachineProfile.Hc2000V2PagedV1.Video.TextOverlay);
        Assert.False(MachineProfile.Hc2000V2PagedMTransferV1.Video.TextOverlay);
        Assert.False(MachineProfile.Hc2000V2HddV1.Video.TextOverlay);
    }

    private string Image(string name, byte first = 0)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var bytes = new byte[HddMediaAdapter.ImageLength]; bytes[0] = first;
        File.WriteAllBytes(path, bytes); return path;
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
