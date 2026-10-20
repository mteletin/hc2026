using Hc2000.Machine.Memory;
using Hc2000.Z80;
using Xunit;

namespace Hc2000.App.Tests;

public sealed partial class HeadlessOracle
{
    // Any CPU bus operation is a fixture failure: these tests never execute guest instructions.
    private sealed class BaudUnusedBus : IBus
    {
        public byte Read(ushort addr) => throw new InvalidOperationException("Unexpected bus read");
        public void Write(ushort addr, byte value) => throw new InvalidOperationException("Unexpected bus write");
        public byte In(ushort port) => throw new InvalidOperationException("Unexpected port read");
        public void Out(ushort port, byte value) => throw new InvalidOperationException("Unexpected port write");
        public void Contend(ushort addr, int tstates) => throw new InvalidOperationException("Unexpected contention");
        public byte IntVector() => throw new InvalidOperationException("Unexpected interrupt");
    }

    private static (Hc2000.Z80.Z80 Cpu, HcMemory Memory) BaudFixture()
    {
        var memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        memory.Ram.Span[0xdf0c] = 0x6b;
        var cpu = new Hc2000.Z80.Z80(new BaudUnusedBus());
        cpu.R.PC = 0x2345;
        cpu.TStates = 123;
        return (cpu, memory);
    }

    private static OwnerCopySlots<BaudOwnerReceipt> BaudSlots(Guid epoch) => new(epoch, receipt => receipt.Request);
    private static readonly DateTime BaudMetadataDeadline = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task BaudOwnerCopyLogicalAliasKeepsBaselinePhysicalWordAndDetachedTuple()
    {
        var (cpu, memory) = BaudFixture();
        var epoch = Guid.NewGuid();
        var slots = BaudSlots(epoch);
        var first = slots.Request(DiagnosticPurpose.CommandStart, BaudMetadataDeadline);
        Assert.False(first.IsCompleted);
        slots.Service(epoch, request => CaptureBaudOwner(request, cpu, memory, 456));
        var baseline = Assert.IsType<BaudOwnerReceipt>(await first);
        Assert.Equal(epoch, baseline.Request.Epoch);
        Assert.Equal(1, baseline.Request.Id);
        Assert.Equal(BaudMetadataDeadline, baseline.Request.DeadlineUtc);
        Assert.Equal(456, baseline.MasterTick);
        Assert.Equal(123, baseline.CpuTStates);
        Assert.Equal((ushort)0x2345, baseline.Pc);
        Assert.Equal(new MemoryLocation(MemoryStore.Ram, 0xdf0c, true), baseline.AtDf0c);
        Assert.Equal(new MemoryLocation(MemoryStore.Ram, 0xdf0d, true), baseline.AtDf0d);
        Assert.Equal((byte)0x6b, baseline.LogicalDf0c);
        Assert.Equal((byte)0, baseline.LogicalDf0d);
        Assert.Equal((byte)0x6b, baseline.RamDf0c);
        Assert.Equal((byte)0, baseline.RamDf0d);
        Assert.Equal((byte)0, baseline.RamFf0c);
        Assert.Equal((byte)0, baseline.RamFf0d);
        Assert.False(baseline.Latches.Cpm);

        memory.SetCpm(true);
        cpu.R.PC = 0x3456;
        cpu.TStates = 789;
        var second = slots.Request(DiagnosticPurpose.PostLoop, BaudMetadataDeadline);
        slots.Service(epoch, request => CaptureBaudOwner(request, cpu, memory, 900));
        var later = Assert.IsType<BaudOwnerReceipt>(await second);
        Assert.Equal(epoch, later.Request.Epoch);
        Assert.Equal(2, later.Request.Id);
        Assert.Equal(900, later.MasterTick);
        Assert.Equal(789, later.CpuTStates);
        Assert.Equal((ushort)0x3456, later.Pc);
        Assert.True(later.Latches.Cpm);
        Assert.Equal(new MemoryLocation(MemoryStore.Ram, 0xff0c, true), later.AtDf0c);
        Assert.Equal(new MemoryLocation(MemoryStore.Ram, 0xff0d, true), later.AtDf0d);
        Assert.Equal((byte)0, later.LogicalDf0c);
        Assert.Equal((byte)0, later.LogicalDf0d);
        Assert.Equal((byte)0x6b, later.RamDf0c);
        Assert.Equal((byte)0, later.RamDf0d);
        Assert.Equal((byte)0, later.RamFf0c);
        Assert.Equal((byte)0, later.RamFf0d);
        Assert.False(baseline.Latches.Cpm);
        Assert.Equal((ushort)0x2345, baseline.Pc);
        Assert.Equal((byte)0x6b, baseline.LogicalDf0c);
    }

    [Fact]
    public async Task BaudOwnerCopyChangedStorageAtConstantMappingIsNotAliasChange()
    {
        var (cpu, memory) = BaudFixture();
        var epoch = Guid.NewGuid();
        var slots = BaudSlots(epoch);
        var first = slots.Request(DiagnosticPurpose.CommandStart, BaudMetadataDeadline);
        slots.Service(epoch, request => CaptureBaudOwner(request, cpu, memory, 1));
        var baseline = Assert.IsType<BaudOwnerReceipt>(await first);
        memory.Ram.Span[0xdf0c] = 0x34;
        memory.Ram.Span[0xdf0d] = 0x12;
        var second = slots.Request(DiagnosticPurpose.PostLoop, BaudMetadataDeadline);
        slots.Service(epoch, request => CaptureBaudOwner(request, cpu, memory, 2));
        var later = Assert.IsType<BaudOwnerReceipt>(await second);
        Assert.Equal(new MemoryLocation(MemoryStore.Ram, 0xdf0c, true), later.AtDf0c);
        Assert.Equal(baseline.AtDf0c, later.AtDf0c);
        Assert.Equal(baseline.Latches, later.Latches);
        Assert.Equal((byte)0x34, later.RamDf0c);
        Assert.Equal((byte)0x12, later.RamDf0d);
        Assert.Equal((byte)0x34, later.LogicalDf0c);
        Assert.Equal((byte)0x12, later.LogicalDf0d);
        Assert.Equal((byte)0x6b, baseline.RamDf0c);
    }

    [Fact]
    public async Task BaudOwnerCopyDoesNotMutateNativeMemoryOrCpu()
    {
        var (cpu, memory) = BaudFixture();
        memory.SetCpm(true);
        memory.SetShadow(true);
        memory.WritePort7E(8);
        var ram = memory.Ram.ToArray();
        var rom = memory.Rom.ToArray();
        var shadowRam = memory.ShadowRam.ToArray();
        var shadowRom = memory.ShadowRom.ToArray();
        var latches = memory.Latches;
        var pages = memory.Pages;
        var registers = cpu.R;
        var epoch = Guid.NewGuid();
        var slots = BaudSlots(epoch);
        var task = slots.Request(DiagnosticPurpose.CommandStart, BaudMetadataDeadline);
        slots.Service(epoch, request => CaptureBaudOwner(request, cpu, memory, 777));
        var receipt = Assert.IsType<BaudOwnerReceipt>(await task);
        Assert.Equal(777, receipt.MasterTick);
        Assert.Equal(ram, memory.Ram.ToArray());
        Assert.Equal(rom, memory.Rom.ToArray());
        Assert.Equal(shadowRam, memory.ShadowRam.ToArray());
        Assert.Equal(shadowRom, memory.ShadowRom.ToArray());
        Assert.Equal(latches, memory.Latches);
        Assert.Equal(latches, receipt.Latches);
        Assert.Same(pages, memory.Pages);
        Assert.Equal(registers, cpu.R);
        Assert.Equal(123, cpu.TStates);
        Assert.False(cpu.Halted);
        Assert.False(cpu.IntLine);
    }

    [Fact]
    public async Task BaudOwnerCopyTwoSingleUseSlotsRejectOverflowAndNeverRecapture()
    {
        var (cpu, memory) = BaudFixture();
        var epoch = Guid.NewGuid();
        var slots = BaudSlots(epoch);
        var first = slots.Request(DiagnosticPurpose.CommandStart, BaudMetadataDeadline);
        var second = slots.Request(DiagnosticPurpose.PostLoop, BaudMetadataDeadline);
        Assert.Throws<InvalidOperationException>(() => { _ = slots.Request((DiagnosticPurpose)2, BaudMetadataDeadline); });
        Assert.Throws<InvalidOperationException>(() => { _ = slots.Request(DiagnosticPurpose.CommandStart, BaudMetadataDeadline); });
        var copies = 0;
        slots.Service(epoch, request => { copies++; return CaptureBaudOwner(request, cpu, memory, 50); });
        slots.Service(epoch, _ => throw new InvalidOperationException("Must not recapture"));
        Assert.Equal(2, copies);
        Assert.NotNull(await first);
        Assert.NotNull(await second);
        Assert.False(slots.HasPending);
        Assert.Equal(DiagnosticState.Published, slots.State(DiagnosticPurpose.CommandStart));
        Assert.Equal(DiagnosticState.Published, slots.State(DiagnosticPurpose.PostLoop));
    }

    [Fact]
    public async Task BaudOwnerCopyWrongEpochUnservedAndRetirementNeverInventReceipt()
    {
        var (cpu, memory) = BaudFixture();
        var epoch = Guid.NewGuid();
        var slots = BaudSlots(epoch);
        var task = slots.Request(DiagnosticPurpose.CommandStart, BaudMetadataDeadline);
        var copies = 0;
        slots.Service(Guid.NewGuid(), request => { copies++; return CaptureBaudOwner(request, cpu, memory, 10); });
        Assert.Equal(0, copies);
        Assert.False(task.IsCompleted);
        Assert.True(slots.HasPending);
        Assert.Equal(DiagnosticState.Pending, slots.State(DiagnosticPurpose.CommandStart));
        slots.Close();
        slots.Service(epoch, request => { copies++; return CaptureBaudOwner(request, cpu, memory, 11); });
        Assert.Equal(0, copies);
        Assert.Equal(DiagnosticState.Missing, slots.State(DiagnosticPurpose.CommandStart));
        Assert.False(slots.HasPending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await task; });
        Assert.Throws<InvalidOperationException>(() => { _ = slots.Request(DiagnosticPurpose.PostLoop, BaudMetadataDeadline); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BaudOwnerCopyCancelOrCloseDuringRealCaptureSuppressesPublication(bool close)
    {
        var (cpu, memory) = BaudFixture();
        var epoch = Guid.NewGuid();
        var slots = BaudSlots(epoch);
        var task = slots.Request(DiagnosticPurpose.CommandStart, BaudMetadataDeadline);
        slots.Service(epoch, request =>
        {
            var receipt = CaptureBaudOwner(request, cpu, memory, 80);
            if (close) slots.Close(); else slots.Cancel(DiagnosticPurpose.CommandStart);
            return receipt;
        });
        Assert.Equal(close ? DiagnosticState.Missing : DiagnosticState.Cancelled,
            slots.State(DiagnosticPurpose.CommandStart));
        Assert.False(slots.HasPending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await task; });
    }

    [Fact]
    public async Task BaudOwnerCopyCancelBeforeClaimDoesNotReadMemory()
    {
        var slots = BaudSlots(Guid.Empty);
        var task = slots.Request(DiagnosticPurpose.CommandStart, BaudMetadataDeadline);
        slots.Cancel(DiagnosticPurpose.CommandStart);
        var copies = 0;
        slots.Service(Guid.Empty, _ => { copies++; throw new InvalidOperationException("Must not capture"); });
        Assert.Equal(0, copies);
        Assert.Equal(DiagnosticState.Cancelled, slots.State(DiagnosticPurpose.CommandStart));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await task; });
    }

    [Fact]
    public async Task BaudOwnerCopyInstancesAreIsolatedAndServiceUsesActualLaterState()
    {
        var (cpu, memory) = BaudFixture();
        var (otherCpu, otherMemory) = BaudFixture();
        otherMemory.Ram.Span[0xdf0c] = 0xa5;
        var epoch = Guid.NewGuid();
        var otherEpoch = Guid.NewGuid();
        var slots = BaudSlots(epoch);
        var otherSlots = BaudSlots(otherEpoch);
        var pending = slots.Request(DiagnosticPurpose.CommandStart, BaudMetadataDeadline);
        var otherPending = otherSlots.Request(DiagnosticPurpose.CommandStart, BaudMetadataDeadline);
        Assert.False(pending.IsCompleted);
        Assert.False(otherPending.IsCompleted);
        cpu.TStates = 999;
        memory.Ram.Span[0xdf0c] = 0x42;
        slots.Service(epoch, request => CaptureBaudOwner(request, cpu, memory, 1000));
        var later = Assert.IsType<BaudOwnerReceipt>(await pending);
        Assert.Equal(1000, later.MasterTick);
        Assert.Equal(999, later.CpuTStates);
        Assert.Equal((byte)0x42, later.LogicalDf0c);
        Assert.False(otherPending.IsCompleted);
        slots.Close();
        otherSlots.Service(otherEpoch, request => CaptureBaudOwner(request, otherCpu, otherMemory, 12));
        var other = Assert.IsType<BaudOwnerReceipt>(await otherPending);
        Assert.Equal(otherEpoch, other.Request.Epoch);
        Assert.Equal(12, other.MasterTick);
        Assert.Equal(123, other.CpuTStates);
        Assert.Equal((byte)0xa5, other.LogicalDf0c);
        Assert.Equal(DiagnosticState.Published, slots.State(DiagnosticPurpose.CommandStart));
    }

    [Fact]
    public async Task BaudOwnerCopyFailedCaptureAndMismatchedRequestAreUnavailable()
    {
        var (cpu, memory) = BaudFixture();
        var epoch = Guid.NewGuid();
        var slots = BaudSlots(epoch);
        var failed = slots.Request(DiagnosticPurpose.CommandStart, BaudMetadataDeadline);
        var mismatch = slots.Request(DiagnosticPurpose.PostLoop, BaudMetadataDeadline);
        slots.Service(epoch, request => request.Purpose == DiagnosticPurpose.CommandStart
            ? CaptureBaudOwner(request, cpu, null!, 90)
            : CaptureBaudOwner(request with { Epoch = Guid.NewGuid() }, cpu, memory, 90));
        Assert.Null(await failed);
        Assert.Null(await mismatch);
        Assert.Equal(DiagnosticState.Failed, slots.State(DiagnosticPurpose.CommandStart));
        Assert.Equal(DiagnosticState.Failed, slots.State(DiagnosticPurpose.PostLoop));
        Assert.False(slots.HasPending);
    }
}
