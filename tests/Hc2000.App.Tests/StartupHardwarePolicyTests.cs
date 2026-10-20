using System.Collections.Immutable;
using Hc2000.App.Configuration;
using Hc2000.App.Settings;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class StartupHardwarePolicyTests
{
    [Theory]
    [InlineData("hc2000-v2")]
    [InlineData("hc2000-v2-ram-banks-v1")]
    [InlineData("hc2000-v2-paged-v1")]
    [InlineData("hc2000-v2-paged-m-v1")]
    [InlineData("hc2000-v2-paged-m-v2")]
    [InlineData("hc2000-v2-paged-expansion-v1")]
    [InlineData("hc2000-v2-hdd-v1")]
    public void SameAdoptedHardwareAllowsPreferenceApplyWithoutHddRebinding(string profile)
    {
        var current = new MachineConfiguration { Profile = profile };
        if (profile == "hc2000-v2-hdd-v1") current = current with { Hdd = [new("C", Path.GetFullPath("c.raw"), false), new("F", Path.GetFullPath("f.raw"))] };
        var draft = current with { SpeedPercent = 200, Scale = 3, Hdd = current.Hdd.Reverse().ToImmutableArray() };
        Assert.True(ReconfigureApply.TryPlan(current, draft, out var error)); Assert.Null(error);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("rom")]
    [InlineData("path")]
    [InlineData("slot")]
    [InlineData("protection")]
    [InlineData("remove")]
    public void SelectedResetNeverAdoptsChangedHardware(string change)
    {
        var current = new MachineConfiguration { Profile = "hc2000-v2-hdd-v1", Hdd = [new("C", Path.GetFullPath("c.raw"))] };
        var draft = change switch
        {
            "profile" => current with { Profile = "hc2000-v2-ram-banks-v1", Hdd = [] },
            "rom" => current with { RomSet = "other-rom" },
            "path" => current with { Hdd = [current.Hdd[0] with { Path = Path.GetFullPath("other.raw") }] },
            "slot" => current with { Hdd = [current.Hdd[0] with { Slot = "D" }] },
            "protection" => current with { Hdd = [current.Hdd[0] with { WriteProtected = false }] },
            _ => current with { Hdd = [] }
        };
        Assert.False(ReconfigureApply.TryPlan(current, draft, out var error));
        Assert.Contains("new cold machine", error); Assert.Contains("New machine", error);
        Assert.Equal("C", current.Hdd[0].Slot); Assert.True(current.Hdd[0].WriteProtected);
    }

    [Fact]
    public void WorkspaceRoundTripsExactHardwareAndUnrelatedPreferencesWithoutRuntimeOrSnapshotLoading()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hc2000-hardware-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var rawDescriptor = new ImageConfiguration("RawFloppy", 80, 2, 16, 256);
            var ram = new MachineConfiguration { Profile = "hc2000-v2-paged-m-v1", AudioPreference = "retain-opaque", LastSnapshot = Path.GetFullPath("remember-only.hcs"), DiskA = new(Path.GetFullPath("raw.img"), false, rawDescriptor) };
            var hdd = new MachineConfiguration { Profile = "hc2000-v2-hdd-v1", Hdd = [new("C", Path.GetFullPath("c.raw"), false), new("F", Path.GetFullPath("f.raw"), true)] };
            var settings = new AppSettings { AudioPreference = "unrelated", KeyMap = new() { ["opaque-key"] = "opaque-value" }, Workspace = new(1, hdd.Id, [ram, hdd]) };
            var store = new SettingsStore(directory); store.Save(settings);
            var loaded = store.Load(); Assert.Null(loaded.Warning);
            Assert.Equal("unrelated", loaded.Settings.AudioPreference); Assert.Equal("opaque-value", loaded.Settings.KeyMap["opaque-key"]);
            var workspace = loaded.Settings.Workspace!;
            Assert.Equal(hdd.Id, workspace.SelectedId); Assert.Equal(ram.Profile, workspace.Machines[0].Profile);
            Assert.Equal("retain-opaque", workspace.Machines[0].AudioPreference); Assert.Equal(ram.LastSnapshot, workspace.Machines[0].LastSnapshot);
            Assert.Equal(rawDescriptor, workspace.Machines[0].DiskA!.Image);
            Assert.Equal(hdd.Profile, workspace.Machines[1].Profile); Assert.Equal(hdd.Hdd.ToArray(), workspace.Machines[1].Hdd.ToArray());
            foreach (var machine in workspace.Machines) machine.ValidateEffective();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
