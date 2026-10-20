using System.Buffers.Binary;
using Hc2000.App.Media;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class HddRunningRemapTests
{
    private sealed class Rig : IDisposable
    {
        internal readonly HddColdVolumeTests Source = new();
        internal readonly HcMemory Memory = new(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        internal readonly BlockDiskPort Port;
        internal readonly HddMediaAdapter Adapter;
        internal Rig(bool two = false)
        {
            Port = new(Memory, new FloppyController(new DeviceScheduler(), 100, new(new()), new(new())),
                hddEnabled: true, guestAckEnabled: true);
            using var first = HddColdVolume.Open(Source.PathName, "VOL1.DSK");
            var media = first.Media(2);
            Port.AttachHdd(2, media.Bytes.AsSpan(), media.WriteProtected);
            first.Dispose();
            if (two)
            {
                using var second = HddColdVolume.Open(Source.PathName, "VOL2.DSK");
                var other = second.Media(3);
                Port.AttachHdd(3, other.Bytes.AsSpan(), other.WriteProtected);
                Adapter = new(Port, [media, other]);
            }
            else Adapter = new(Port, [media]);
        }
        internal HddReply Begin(string name = "VOL2.DSK", uint generation = 1)
        {
            using var selected = HddColdVolume.Open(Source.PathName, name);
            return Adapter.Apply(new(HddAction.BeginSwitch, Target: new(Source.PathName, selected.Identity), ExpectedGeneration: generation));
        }
        internal byte Command(byte operation, uint generation = 0)
        {
            Span<byte> command = stackalloc byte[16]; command.Clear();
            command[0] = 2; command[1] = operation; command[2] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(command[8..], 0x8200);
            BinaryPrimitives.WriteUInt32LittleEndian(command[12..], generation);
            for (int i = 0; i < command.Length; i++) Memory.Write((ushort)(0x8000 + i), command[i]);
            Port.Write(0x81, 0, 0); Port.Write(0x83, 0x80, 0); Port.Write(0x89, 1, 0);
            return Port.Read(0x89, 0);
        }
        internal void Acknowledge()
        {
            Assert.Equal(0, Command(4));
            Assert.Equal(1, Memory.Read(0x8201));
            Assert.Equal(0, Command(5, Port.CaptureHdd(2).Generation));
            Assert.True(Port.GuestSwitchHoldsCpu);
        }
        public void Dispose() => Source.Dispose();
    }

    [Fact]
    public void SelectionWaitsForRealTicketAndAccountsNameBytesAndProtectionBeforeRelease()
    {
        using var rig = new Rig();
        var before = File.ReadAllBytes(rig.Source.PathName);
        var reply = rig.Begin();
        Assert.Null(reply.Error);
        Assert.Equal(HddSwitchState.AwaitingGuest, reply.Switch?.State);
        rig.Adapter.PumpGuestSwitch();
        Assert.Equal(1u, rig.Port.CaptureHdd(2).Generation);
        Assert.Equal(0x41, rig.Port.CaptureHdd(2).Bytes[32768]);
        rig.Acknowledge();
        rig.Adapter.PumpGuestSwitch();
        var after = rig.Adapter.Apply(new(HddAction.Inspect));
        Assert.Equal(HddSwitchState.Committed, after.Switch?.State);
        Assert.False(rig.Port.GuestSwitchHoldsCpu);
        Assert.Equal("VOL2.DSK", after.Drives[0].Name);
        Assert.Equal(2u, after.Drives[0].Generation);
        Assert.True(after.Drives[0].WriteProtected); Assert.False(after.Drives[0].Dirty);
        Assert.Equal(0x42, rig.Port.CaptureHdd(2).Bytes[32768]);
        Assert.Equal(0, rig.Command(4)); Assert.Equal(3, rig.Memory.Read(0x8201));
        Assert.Equal(before, File.ReadAllBytes(rig.Source.PathName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelBeforeOrAfterAckRetainsOldBinding(bool held)
    {
        using var rig = new Rig();
        var begun = rig.Begin(); Assert.NotNull(begun.Switch);
        if (held) rig.Acknowledge();
        var cancelled = rig.Adapter.Apply(new(HddAction.CancelSwitch, SwitchId: begun.Switch.Id));
        Assert.Null(cancelled.Error);
        Assert.Equal(HddSwitchState.Cancelled, cancelled.Switch?.State);
        Assert.False(rig.Port.GuestSwitchHoldsCpu);
        Assert.Equal("VOL1.DSK", cancelled.Drives[0].Name); Assert.Equal(1u, cancelled.Drives[0].Generation);
        Assert.Equal(0x41, rig.Port.CaptureHdd(2).Bytes[32768]);
        Assert.NotNull(rig.Adapter.Apply(new(HddAction.CancelSwitch, SwitchId: begun.Switch.Id)).Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedSourceOrMountedAliasRefusesWithoutAdopting(bool alias)
    {
        using var rig = new Rig(two: alias);
        Assert.NotNull(rig.Begin().Switch);
        if (!alias)
        {
            using var file = File.OpenWrite(rig.Source.PathName); file.Position = 8_388_608 + 32768; file.WriteByte(0x43);
        }
        rig.Acknowledge(); rig.Adapter.PumpGuestSwitch();
        var refused = rig.Adapter.Apply(new(HddAction.Inspect));
        Assert.Equal(HddSwitchState.Cancelled, refused.Switch?.State); Assert.NotNull(refused.Switch?.Error);
        Assert.False(rig.Port.GuestSwitchHoldsCpu);
        Assert.Equal("VOL1.DSK", refused.Drives[0].Name); Assert.Equal(1u, refused.Drives[0].Generation);
        Assert.Equal(0x41, rig.Port.CaptureHdd(2).Bytes[32768]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoredGuestCannotBeRequalifiedByResetOrFreshRequest(bool reset)
    {
        using var rig = new Rig();
        rig.Port.Restore(rig.Port.Capture()); if (reset) rig.Port.Reset();
        var refused = rig.Begin();
        Assert.Contains("Restored", refused.Error);
        Assert.Null(refused.Switch); Assert.False(rig.Port.GuestSwitchHoldsCpu);
        Assert.Equal(1u, rig.Port.CaptureHdd(2).Generation); Assert.Equal(0x41, rig.Port.CaptureHdd(2).Bytes[32768]);
    }

    [Fact]
    public void StaleObservedGenerationCannotBegin()
    {
        using var rig = new Rig();
        var refused = rig.Begin(generation: 2);
        Assert.NotNull(refused.Error); Assert.Null(refused.Switch);
        Assert.Equal(1u, rig.Port.CaptureHdd(2).Generation);
    }
}
