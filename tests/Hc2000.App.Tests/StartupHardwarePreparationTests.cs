using System.Collections.Immutable;
using Hc2000.App.Configuration;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.Machine;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Memory;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class StartupHardwarePreparationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc2000-startup-hardware-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("hc2000-v2", 0x10000, null)]
    [InlineData("hc2000-v2-ram-banks-v1", BankPolicy.RamBytes, BankPolicy.PolicyId)]
    [InlineData("hc2000-v2-paged-v1", PagedRamPolicy.RamBytes, PagedRamPolicy.PolicyId)]
    [InlineData("hc2000-v2-paged-m-v1", PagedRamPolicy.RamBytes, PagedRamPolicy.MTransferPolicyId)]
    [InlineData("hc2000-v2-paged-m-v2", PagedRamPolicy.RamBytes, PagedRamPolicy.MTransferV2PolicyId)]
    [InlineData("hc2000-v2-paged-expansion-v1", PagedRamPolicy.RamBytes, PagedRamPolicy.MTransferPolicyId)]
    [InlineData("hc2000-v2-hdd-v1", 0x10000, null)]
    public async Task HelperSelectsExactPolicyWithoutChangingRomBytes(string profile, int ramBytes, string? policy)
    {
        var roms = StartupHardwareTests.Roms();
        roms.Basic[123] = 0x5a; roms.Cpm[124] = 0xa5; roms.If1[125] = 0x42;
        var prepared = await StartupHardwarePreparation.PrepareAsync(roms, new() { Profile = profile });
        Assert.Equal(profile, prepared.Roms.Profile.Name);
        prepared.Roms.Profile.Validate();
        Assert.Equal(ramBytes, prepared.Roms.Profile.Memory.RamBytes);
        Assert.Equal(policy, prepared.Roms.Profile.Memory.RamBanks?.PolicyId ?? prepared.Roms.Profile.Memory.RamPaged?.PolicyId);
        Assert.Equal(roms.Basic, prepared.Roms.Basic); Assert.Equal(roms.Cpm, prepared.Roms.Cpm); Assert.Equal(roms.If1, prepared.Roms.If1);
        Assert.Empty(prepared.InitialHdd);
        Assert.Equal(profile == "hc2000-v2-paged-expansion-v1" ? 16L * 0x44000000 : 4L * 1024 * 1024 * 1024,
            prepared.SnapshotPolicy.TransientBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualPagedOwnerUsesFullSixteenBitBankAndExactMTransferPort(bool mTransfer)
    {
        // These cases allocate the real contiguous 0x40010000 RAM array, not a sparse fake or HCS copy.
        var enter = mTransfer ? """
            LD BC,0x00C7
            OUT (C),A
            LD BC,0x7CFD
            LD A,1
            OUT (C),A
            IN A,(C)
            CP 0x41
            JP NZ,fail
            """ : "";
        var address = mTransfer ? "0x8000" : "0xC000";
        var program = enter + "\n" + $$"""
            LD BC,0x7EFD
            LD A,0x34
            OUT (C),A
            IN A,(C)
            CP 0x34
            JP NZ,fail
            LD BC,0x7DFD
            LD A,0x12
            OUT (C),A
            IN A,(C)
            CP 0x12
            JP NZ,fail
            LD A,0x5A
            LD ({{address}}),A
            LD BC,0x7DFD
            XOR A
            OUT (C),A
            LD A,0xA5
            LD ({{address}}),A
            LD A,0x12
            OUT (C),A
            LD A,({{address}})
            CP 0x5A
            JP NZ,fail
            """;
        var prepared = await StartupHardwarePreparation.PrepareAsync(StartupHardwareTests.Roms(proof: program), new()
        {
            Profile = mTransfer ? "hc2000-v2-paged-m-v1" : "hc2000-v2-paged-v1"
        });
        Assert.Equal(PagedRamPolicy.RamBytes, prepared.Roms.Profile.Memory.RamBytes);
        StartupHardwareTests.AssertOwnerSuccess(prepared);
    }

    [Fact]
    public async Task ActualPagedOwnerV2UsesFullSixteenBitBankAndExactMTransferPortV2()
    {
        // v2 identity, same port contract as v1: the 0x00C7/0x7CFD admission read 0x41, C7/7CFD/7DFD access the
        // full 16-bit bank window, and the contiguous 0x40010000 array is the store.
        var program = """
            LD BC,0x00C7
            OUT (C),A
            LD BC,0x7CFD
            LD A,1
            OUT (C),A
            IN A,(C)
            CP 0x41
            JP NZ,fail
            LD BC,0x7EFD
            LD A,0x34
            OUT (C),A
            IN A,(C)
            CP 0x34
            JP NZ,fail
            LD BC,0x7DFD
            LD A,0x12
            OUT (C),A
            IN A,(C)
            CP 0x12
            JP NZ,fail
            LD A,0x5A
            LD (0x8000),A
            LD BC,0x7DFD
            XOR A
            OUT (C),A
            LD A,0xA5
            LD (0x8000),A
            LD A,0x12
            OUT (C),A
            LD A,(0x8000)
            CP 0x5A
            JP NZ,fail
            """;
        var prepared = await StartupHardwarePreparation.PrepareAsync(StartupHardwareTests.Roms(proof: program), new()
        {
            Profile = "hc2000-v2-paged-m-v2"
        });
        Assert.Equal("hc2000-v2-paged-m-v2", prepared.Roms.Profile.Name);
        Assert.Equal(PagedRamPolicy.MTransferV2PolicyId, prepared.Roms.Profile.Memory.RamPaged!.PolicyId);
        Assert.Equal(PagedRamPolicy.RamBytes, prepared.Roms.Profile.Memory.RamBytes);
        StartupHardwareTests.AssertOwnerSuccess(prepared);
    }

    [Fact]
    public async Task HddStartAdoptsPrivateCopiesAndExactSlotProtectionBeforeInstructionZero()
    {
        var c = Image("c.raw", 0x5a); var f = Image("f.raw", 0xa5);
        var prepared = await StartupHardwarePreparation.PrepareAsync(StartupHardwareTests.Roms(), new()
        {
            Profile = "hc2000-v2-hdd-v1", Hdd = [new("C", c, false), new("F", f, true)]
        });
        await File.WriteAllBytesAsync(c, new byte[HddMediaAdapter.ImageLength]);
        BlockDiskPort? controller = null;
        var machine = BasicMachineComposition.CreateColdNewMachine(prepared, new(), new(new()), new(new()), _ => { }, value => controller = value);
        Assert.Equal(0, machine.MasterTick);
        Assert.NotNull(controller); Assert.False(controller.GuestAckEnabled);
        var slotC = controller.CaptureHdd(2); var slotF = controller.CaptureHdd(5);
        Assert.Equal((byte)0x5a, slotC.Bytes[0]); Assert.Equal((byte)0xa5, slotF.Bytes[0]);
        Assert.False(slotC.WriteProtected); Assert.True(slotF.WriteProtected);
        Assert.False(slotC.Dirty); Assert.Equal(1u, slotC.Generation);
        Assert.False(controller.CaptureHdd(3).Present); Assert.False(controller.CaptureHdd(4).Present);
        Assert.Null(prepared.ColdAdmission); // Raw private copy, not an admitted named map or writable host binding.
    }

    [Fact]
    public async Task BadSecondMemberFailsWholePreparationBeforeAnyOwnerExists()
    {
        var good = Image("good.raw", 42); var bad = Path.Combine(directory, "bad.raw");
        await File.WriteAllBytesAsync(bad, [1, 2]);
        await Assert.ThrowsAsync<InvalidDataException>(() => StartupHardwarePreparation.PrepareAsync(StartupHardwareTests.Roms(), new()
        {
            Profile = "hc2000-v2-hdd-v1", Hdd = [new("C", good), new("D", bad)]
        }));
    }

    [Theory]
    [InlineData("hc2000-v2")]
    [InlineData("hc2000-v2-ram-banks-v1")]
    [InlineData("hc2000-v2-paged-v1")]
    [InlineData("hc2000-v2-paged-m-v1")]
    [InlineData("hc2000-v2-paged-expansion-v1")]
    [InlineData("hc2000-v2-hdd-guest-v1")]
    [InlineData("hc2000-v2-paged-tape-v2")]
    [InlineData("hc2000-v2-blockport-v1")]
    [InlineData("invented-ram-hdd")]
    public async Task UnsupportedCombinationsRefuseBeforeReadingEvenMissingMedia(string profile) =>
        await Assert.ThrowsAsync<ArgumentException>(() => StartupHardwarePreparation.PrepareAsync(StartupHardwareTests.Roms(), new()
        {
            Profile = profile, Hdd = [new("C", Path.Combine(directory, "missing.raw"))]
        }));

    [Fact]
    public async Task DuplicateSlotsAndCancellationRefuseBeforeReads()
    {
        var configuration = new MachineConfiguration
        {
            Profile = "hc2000-v2-hdd-v1", Hdd = [new("C", Path.Combine(directory, "missing.raw")), new("C", Path.Combine(directory, "missing2.raw"))]
        };
        await Assert.ThrowsAsync<ArgumentException>(() => StartupHardwarePreparation.PrepareAsync(StartupHardwareTests.Roms(), configuration));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StartupHardwarePreparation.PrepareAsync(StartupHardwareTests.Roms(), configuration, canceled.Token));
    }

    [Fact]
    public async Task ForeignRomMapAndWrongRomLengthRefuseBeforeMediaReads()
    {
        var configuration = new MachineConfiguration { Profile = "hc2000-v2-hdd-v1", Hdd = [new("C", Path.Combine(directory, "missing.raw"))] };
        await Assert.ThrowsAsync<ArgumentException>(() => StartupHardwarePreparation.PrepareAsync(StartupHardwareTests.Roms(MachineProfile.Hc2000V2PagedV1), configuration));
        await Assert.ThrowsAsync<ArgumentException>(() => StartupHardwarePreparation.PrepareAsync((MachineProfile.Hc2000V2, [], [], []), configuration));
    }

    [Fact]
    public async Task LinkIsNotAnEligibleRawImage()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux regular-file guard");
        var target = Image("regular.raw", 0); var link = Path.Combine(directory, "link.raw");
        File.CreateSymbolicLink(link, target);
        await Assert.ThrowsAsync<IOException>(() => StartupHardwarePreparation.PrepareAsync(StartupHardwareTests.Roms(), new()
        {
            Profile = "hc2000-v2-hdd-v1", Hdd = [new("C", link)]
        }));
    }

    private string Image(string name, byte first)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name); var bytes = new byte[HddMediaAdapter.ImageLength]; bytes[0] = first;
        File.WriteAllBytes(path, bytes); return path;
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
