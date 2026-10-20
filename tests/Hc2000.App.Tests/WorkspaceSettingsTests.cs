using System.Collections.Immutable;
using System.Text.Json;
using Hc2000.App.Configuration;
using Hc2000.App.Settings;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class WorkspaceSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc2000-workspace-" + Guid.NewGuid().ToString("N"));
    private SettingsStore Store => new(directory);

    [Fact]
    public void LegacyImportIsOneDetachedDraftAndDoesNotWrite()
    {
        var legacy = new AppSettings { AudioPreference = "opaque:future", KeyMap = new() { ["X"] = "Y" } };
        Store.Save(legacy);
        var before = File.ReadAllBytes(Store.FilePath);
        var workspace = WorkspaceSettings.Import(Store.Load().Settings);
        var machine = Assert.Single(workspace.Machines);
        Assert.Equal(machine.Id, workspace.SelectedId);
        legacy.KeyMap["X"] = "changed";
        Assert.Equal("Y", machine.KeyMap["X"]);
        Assert.Equal("opaque:future", machine.AudioPreference);
        Assert.Equal(before, File.ReadAllBytes(Store.FilePath));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void WorkspaceRoundTripsAndLegacyEditorPreservesIt(int count)
    {
        var machines = Enumerable.Range(0, count).Select(i => new MachineConfiguration
        {
            Id = "machine-" + i, Name = "Machine " + i, AudioPreference = "future:opaque",
            KeyMap = ImmutableDictionary<string, string>.Empty.Add("Enter", "Return"),
            DiskA = new(Path.GetFullPath("first.dsk")),
            DiskB = new(Path.GetFullPath("second.raw"), Image: new("RawFloppy", 80, 2, 9, 512)),
            Hdd = [new("C", Path.GetFullPath("cold.img"))], LastSnapshot = Path.GetFullPath("saved.hcs")
        }).ToImmutableArray();
        var workspace = new WorkspaceSettings(1, machines[^1].Id, machines);
        Store.Save(new() { Workspace = workspace });
        var editor = new SettingsEditor(Store.Load().Settings) { SpeedPercent = "200" };
        Store.Save(editor.Build());
        var loaded = Store.Load();
        Assert.Null(loaded.Warning);
        Assert.Equal(JsonSerializer.Serialize(workspace), JsonSerializer.Serialize(loaded.Settings.Workspace));
        Assert.Equal(200, loaded.Settings.SpeedPercent);
        Assert.Single(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData("{\"version\":2,\"selectedId\":\"one\",\"machines\":[{\"id\":\"one\"}]}")]
    [InlineData("{\"version\":1,\"version\":1,\"selectedId\":\"one\",\"machines\":[{\"id\":\"one\"}]}")]
    [InlineData("{\"version\":1,\"selectedId\":\"missing\",\"machines\":[{\"id\":\"one\"}]}")]
    [InlineData("{\"version\":1,\"selectedId\":\"one\",\"machines\":[{\"id\":\"one\",\"future\":true}]}")]
    [InlineData("{\"version\":1,\"selectedId\":\"one\",\"machines\":[{\"id\":\"one\"},{\"id\":\"one\"}]}")]
    [InlineData("{\"version\":1,\"selectedId\":\"one\",\"machines\":[]}")]
    [InlineData("{\"version\":1,\"selectedId\":\"one\",\"machines\":[null]}")]
    [InlineData("{\"version\":1,\"selectedId\":\"one\",\"machines\":[{\"id\":\"one\",\"diskA\":{\"path\":\"relative.dsk\"}}]}")]
    [InlineData("{\"version\":1,\"selectedId\":\"one\",\"machines\":[{\"id\":\"one\",\"keyMap\":null}]}")]
    public void InvalidWorkspacePreservesOriginalBytes(string workspace)
    {
        Directory.CreateDirectory(directory);
        var json = "{\"scale\":3,\"workspace\":" + workspace + "}";
        File.WriteAllText(Store.FilePath, json);
        var loaded = Store.Load();
        Assert.NotNull(loaded.Warning);
        Assert.Null(loaded.Settings.Workspace);
        Assert.Equal(json, File.ReadAllText(Store.FilePath));
    }

    [Fact]
    public void CapacityAndNameLimitsRefuseWithoutChangingSavedFile()
    {
        Store.Save(new());
        var original = File.ReadAllBytes(Store.FilePath);
        var machines = Enumerable.Range(0, 5).Select(i => new MachineConfiguration { Id = i.ToString() }).ToImmutableArray();
        Assert.Throws<ArgumentException>(() => Store.Save(new() { Workspace = new(1, "0", machines) }));
        Assert.Throws<ArgumentException>(() => new MachineConfiguration { Name = new string('x', 81) }.Validate());
        Assert.Equal(original, File.ReadAllBytes(Store.FilePath));
    }

    [Fact]
    public void OversizeWorkspaceIsRefusedOnSaveAndLoad()
    {
        var config = new MachineConfiguration { AudioPreference = new string('é', 600000) };
        var settings = new AppSettings { Workspace = new(1, config.Id, [config]) };
        Assert.Throws<ArgumentException>(() => Store.Save(settings));
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(Store.FilePath, json);
        Assert.NotNull(Store.Load().Warning);
        Assert.Equal(json, File.ReadAllText(Store.FilePath));
    }

    [Theory]
    [InlineData("missing-id")]
    [InlineData("unknown-machine-key")]
    [InlineData("unknown-workspace-key")]
    public void SingleSchemaViolationInOtherwiseValidWorkspacePreservesBytes(string violation)
    {
        var machine = new MachineConfiguration { Id = "one" };
        Store.Save(new() { Workspace = new(1, machine.Id, [machine]) });
        Assert.Null(Store.Load().Warning);
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Store.FilePath))!;
        var workspace = root["workspace"]!;
        var entry = workspace["machines"]![0]!;
        if (violation == "missing-id") entry.AsObject().Remove("id");
        else if (violation == "unknown-machine-key") entry["future"] = true;
        else workspace["future"] = true;
        var changed = root.ToJsonString();
        File.WriteAllText(Store.FilePath, changed);
        Assert.NotNull(Store.Load().Warning);
        Assert.Equal(changed, File.ReadAllText(Store.FilePath));
    }

    [Theory]
    [InlineData("future-profile", 2)] [InlineData("hc2000-v2", 9)]
    public void UnsupportedEffectiveSettingsAreRetainedButCannotStart(string profile, int scale)
    {
        var machine = new MachineConfiguration { Profile = profile, Scale = scale };
        machine.Validate();
        Assert.Throws<ArgumentException>(() => machine.ValidateEffective());
    }

    [Fact]
    public void TypedDescriptorUsesNativeValidationAndDoesNotActivateRaw()
    {
        var raw = new ImageConfiguration("RawFloppy", 80, 2, 9, 512);
        Assert.Equal(737280, raw.ToDescriptor().Geometry!.Length);
        var machine = new MachineConfiguration { DiskA = new(Path.GetFullPath("disk.raw"), Image: raw) };
        machine.Validate();
        Assert.Throws<ArgumentException>(() => machine.ValidateEffective());
        Assert.Throws<ArgumentException>(() => new ImageConfiguration("RawFloppy").ToDescriptor());
        Assert.ThrowsAny<Exception>(() => new ImageConfiguration("RawFloppy", int.MaxValue, 2, 9, 512).ToDescriptor());
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
