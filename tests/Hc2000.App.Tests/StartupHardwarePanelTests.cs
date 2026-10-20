using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Hc2000.App.Configuration;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class StartupHardwarePanelTests
{
    [AvaloniaFact]
    public void DisabledHddGuidanceNamesTheOrdinaryUiRouteNotAnAutomationFlag()
    {
        var panel = new Hc2000.App.Media.HddPanel();
        Assert.Contains("New machine", panel.Notice);
        Assert.Contains("Hardware", panel.Notice);
        Assert.DoesNotContain("--hdd", panel.Notice);
        Assert.False(panel.ColdConfigurationAvailable);
    }

    [AvaloniaFact]
    public async Task UnarmedHddColdStartRefusesWithTheOrdinaryUiRoute()
    {
        var panel = new Hc2000.App.Media.HddPanel();
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(panel.StartColdAsync);
        Assert.Contains("New machine", refusal.Message);
        Assert.Contains("Hardware", refusal.Message);
        Assert.DoesNotContain("--hdd", refusal.Message);
        Assert.False(panel.ColdConfigurationAvailable);
    }

    private static ComboBox Selector(StartupHardwarePanel panel) => panel.GetLogicalDescendants().OfType<ComboBox>().Single();
    private static void Select(StartupHardwarePanel panel, string profile) =>
        Selector(panel).SelectedItem = Selector(panel).Items.OfType<ComboBoxItem>().Single(c => (string?)c.Tag == profile);

    [AvaloniaTheory]
    [InlineData("hc2000-v2")]
    [InlineData("hc2000-v2-ram-banks-v1")]
    [InlineData("hc2000-v2-paged-v1")]
    [InlineData("hc2000-v2-paged-m-v1")]
    [InlineData("hc2000-v2-paged-m-v2")]
    [InlineData("hc2000-v2-hdd-v1")]
    public void ProfileProjectionPreservesAllSiblingDraftChanges(string profile)
    {
        var original = new MachineConfiguration();
        var panel = new StartupHardwarePanel(original);
        Select(panel, profile);
        var edited = new MachineConfiguration
        {
            Name = "Sibling edits", SpeedPercent = 200, Scale = 3, AudioPreference = "opaque",
            DiskA = new(Path.GetFullPath("selected-a.dsk")), DiskB = new(Path.GetFullPath("selected-b.raw"), Image: new("RawFloppy", 80, 2, 16, 256)),
            Hdd = [new("C", Path.GetFullPath("selected-c.raw"), false)], LastSnapshot = Path.GetFullPath("remembered.hcs")
        };
        Assert.Equal(edited with { Profile = profile }, panel.ApplyTo(edited));
        Assert.Equal(profile == "hc2000-v2-hdd-v1", panel.HddEnabled);
        Assert.Equal("hc2000-v2", original.Profile);
    }

    [AvaloniaFact]
    public void UserChangeNotifiesMediaButNeverSilentlyClearsSelectedHdd()
    {
        var original = new MachineConfiguration { Profile = "hc2000-v2-hdd-v1", Hdd = [new("C", Path.GetFullPath("keep.raw"))] };
        var panel = new StartupHardwarePanel(original); var notifications = 0;
        panel.HddEnabledChanged += (_, _) => notifications++;
        Assert.True(panel.HddEnabled); Assert.Equal(0, notifications);
        Select(panel, "hc2000-v2-ram-banks-v1");
        Assert.False(panel.HddEnabled); Assert.Equal(1, notifications);
        var combined = panel.ApplyTo(original);
        Assert.Equal(original.Hdd, combined.Hdd);
        Assert.Throws<ArgumentException>(combined.ValidateEffective);
        Select(panel, "hc2000-v2-hdd-v1");
        Assert.True(panel.HddEnabled); Assert.Equal(2, notifications);
    }

    [AvaloniaFact]
    public void UnsupportedStoredChoiceRemainsVisibleAndRefusedUntilExplicitUserChoice()
    {
        var original = new MachineConfiguration { Profile = "future-card" };
        var panel = new StartupHardwarePanel(original);
        Assert.Equal(original, panel.ApplyTo(original));
        Assert.Contains("Unsupported stored", ((ComboBoxItem)Selector(panel).SelectedItem!).Content!.ToString());
        Assert.Throws<ArgumentException>(() => panel.ApplyTo(original).ValidateEffective());
        Select(panel, "hc2000-v2"); panel.ApplyTo(original).ValidateEffective();
    }

    [AvaloniaTheory]
    [InlineData("hc2000-v2-paged-v1")]
    [InlineData("hc2000-v2-paged-m-v1")]
    [InlineData("hc2000-v2-paged-m-v2")]
    public void HighMemoryCostIsVisibleBeforeStart(string profile)
    {
        var panel = new StartupHardwarePanel(new() { Profile = profile });
        var description = panel.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "PrestartHardwareDescription").Text;
        Assert.Contains("1 GiB", description); Assert.Contains("contiguous", description);
    }
}
