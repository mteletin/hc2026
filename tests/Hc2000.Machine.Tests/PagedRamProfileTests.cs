using System.Text.Json;
using System.Text.Json.Nodes;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Snapshots;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class PagedRamProfileTests
{
    [Fact] public void OnlyThePinnedUpgradeIdentityInstallsPagedRam()
    {
        var p = MachineProfile.Hc2000V2PagedV1;
        p.Validate();
        Assert.Equal("hc2000-v2-paged-v1", p.Name);
        Assert.Equal("hc2000-ram-paged-16k65536-v1", p.Memory.RamPaged!.PolicyId);
        Assert.Throws<ArgumentException>(() => (p with { Name = "hc2000-v2" }).Validate());
        Assert.Throws<ArgumentException>(() => (p with { Memory = p.Memory with { RamPaged = null } }).Validate());
        Assert.Throws<ArgumentException>(() => (p with { Memory = p.Memory with { RamPaged = new("wrong") } }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (p with { Memory = p.Memory with { RamBytes = 0x10000 } }).Validate());
        Assert.Throws<ArgumentException>(() => (p with { Memory = p.Memory with { RamBanks = new("hc2000-ram-banks-8k128-v1", 128, 8192) } }).Validate());
        Assert.Throws<ArgumentException>(() => (p with { BlockDisk = BlockDiskPolicy.HddV1 }).Validate());
        Assert.Throws<ArgumentException>(() => (p with { CpuTurboMultiplier = 2 }).Validate());
        Assert.Null(MachineProfile.Default.Memory.RamPaged);
        Assert.Null(MachineProfile.Hc2000V2RamBanksV1.Memory.RamPaged);
    }

    [Fact] public void FaithfulSnapshotRejectsPagedObjectRatherThanSilentlyDiscardingIt()
    {
        // A minimal detached logical fixture, without a GiB allocation. The memory table is
        // checked before blob models; null/absent remain admissible in the old identity.
        var profile = MachineProfile.Default;
        var definition = MachineSchema.Definition(profile);
        var state = new JsonObject { ["port7e"] = 0, ["cpm"] = false, ["shadow"] = false };
        var manifest = new JsonObject
        {
            ["profile"] = new JsonObject { ["id"] = profile.Name, ["version"] = 1,
                ["definition"] = JsonNode.Parse(definition.GetRawText()), ["fingerprint"] = HcsCodec.ProfileFingerprint(definition) },
            ["assets"] = new JsonArray(), ["stores"] = new JsonArray(),
            ["components"] = new JsonArray(new JsonObject { ["id"] = "memory", ["modelId"] = "memory", ["modelVersion"] = 1, ["state"] = state }),
            ["cpu"] = new CpuLogicalState(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, false, false, false, false, false, false, false, 1, 0, 0).ToJson(),
            ["clock"] = new JsonObject { ["epoch"] = "0", ["now"] = "0", ["masterHz"] = "17500000",
                ["dividerNumerator"] = "5", ["dividerDenominator"] = "1", ["lastCpuTStates"] = "0",
                ["mapperWhole"] = "0", ["mapperRemainder"] = "0", ["nextSequence"] = "0", ["deviceGenerations"] = new JsonArray() },
            ["events"] = new JsonArray(), ["pendingOutput"] = new JsonArray()
        };
        state["paged"] = new JsonObject { ["v"] = 1, ["bank"] = 0, ["screen"] = false, ["rom"] = false,
            ["locked"] = false, ["ramBytes"] = "1073741824", ["ramSha256"] = new string('0', 64) };
        var schema = new MachineSchema(profile);
        var exception = Assert.Throws<HcsException>(() => HcsCodec.Write(Stream.Null,
            JsonSerializer.SerializeToElement(manifest), [], schema));
        Assert.Equal(HcsError.ProfileMismatch, exception.Error);
    }
}
