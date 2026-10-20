using System.Collections.Immutable;
using Hc2000.App.Configuration;
using Hc2000.App.Runtime;
using Hc2000.Asm;
using Hc2000.Machine;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class StartupHardwareTests
{
    [Theory]
    [InlineData("hc2000-v2-ram-banks-v1")]
    [InlineData("hc2000-v2-paged-v1")]
    [InlineData("hc2000-v2-paged-m-v1")]
    [InlineData("hc2000-v2-paged-m-v2")]
    [InlineData("hc2000-v2-hdd-v1")]
    public void ImplementedColdProfilesAreEffective(string profile) =>
        (new MachineConfiguration { Profile = profile }).ValidateEffective();

    [Fact]
    public void ActualColdOwnerInstallsBankPortsAndIndependentPhysicalBanks()
    {
        var prepared = BasicMachineComposition.PrepareWholeChipV2(Roms(MachineProfile.Hc2000V2RamBanksV1, """
            IN A,(0x8D)
            CP 0x80
            JP NZ,fail
            LD A,0
            OUT (0x8B),A
            LD A,0x5A
            LD (0xA000),A
            LD A,127
            OUT (0x8B),A
            LD A,0xA5
            LD (0xA000),A
            LD A,0
            OUT (0x8B),A
            LD A,(0xA000)
            CP 0x5A
            JP NZ,fail
            LD A,127
            OUT (0x8B),A
            IN A,(0x8B)
            CP 127
            JP NZ,fail
            LD A,(0xA000)
            CP 0xA5
            JP NZ,fail
            """));
        Assert.Equal(0x110000, prepared.Roms.Profile.Memory.RamBytes);
        AssertOwnerSuccess(prepared);
    }

    internal static (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) Roms(MachineProfile? profile = null, string? proof = null)
    {
        var bytes = new byte[16384];
        if (proof is not null)
        {
            // Authored ROM proof, NOT real firmware/installed guest evidence. Copy to IF1 too so
            // authentic M1 shadow traps do not change this finite test program's bytes.
            var result = Assembler.AssembleText("ORG 0\nDI\n" + proof + "\nLD A,0x10\nOUT (0xFE),A\nHALT\nfail: HALT\n");
            Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
            result.ToBinary().CopyTo(bytes, 0);
        }
        return (profile ?? MachineProfile.Hc2000V2, bytes, bytes.ToArray(), bytes.ToArray());
    }

    internal static void AssertOwnerSuccess(PreparedBasicMachine prepared)
    {
        var machine = BasicMachineComposition.CreateColdNewMachine(prepared, new(), new(new()), new(new()), _ => { }, _ => { });
        Assert.Equal(0, machine.MasterTick);
        Assert.True(machine.TryRunFrame(() => true, out var frame));
        Assert.Contains(frame!.BeeperEdges, edge => edge.Speaker);
    }
}
