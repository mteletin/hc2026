using Hc2000.Machine.Floppy;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class BlockDiskTests
{
    private static (HcMemory Memory, FloppyDrive A, FloppyDrive B, BlockDiskTransfer Transfer) Create()
    {
        var memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var a = new FloppyDrive(new(), Dsk.Create(new(80, 2, 9, 512)));
        var b = new FloppyDrive(new(), Dsk.Create(new(80, 2, 9, 512)));
        return (memory, a, b, new(memory, a, b));
    }

    [Fact]
    public void OneTransferCopiesExactly512BytesAndDoesNotMoveHeads()
    {
        var (memory, a, b, transfer) = Create();
        var data = Enumerable.Range(0, 512).Select(n => (byte)n).ToArray();
        data.CopyTo(a.Image!.Tracks[7].Sectors[3].Data, 0);
        memory.Ram.Span.Fill(0x55);
        Assert.Equal(BlockDiskStatus.Success, transfer.Execute(false, 0, 3, 1, 4, 0x5f00));
        Assert.Equal(data, Enumerable.Range(0, 512).Select(n => memory.Read((ushort)(0x5f00 + n))));
        Assert.Equal(0x55, memory.Read(0x5eff)); Assert.Equal(0x55, memory.Read(0x6100));
        Assert.Equal(0, a.HeadPosition); Assert.Equal(0, b.HeadPosition);
        Assert.Empty(a.DirtySectors);
    }

    [Fact]
    public void WritesOnlySelectedDriveSectorAndDirtyOverlaySurvivesExport()
    {
        var (memory, a, b, transfer) = Create();
        var data = Enumerable.Range(0, 512).Select(n => (byte)(n ^ 0xa6)).ToArray();
        data.CopyTo(memory.Ram.Span[0x7000..]);
        Assert.Equal(BlockDiskStatus.Success, transfer.Execute(true, 1, 5, 1, 2, 0x7000));
        Assert.Equal(data, b.Image!.Tracks[11].Sectors[1].Data);
        Assert.Equal(new DirtySector(5, 1, 1), Assert.Single(b.DirtySectors));
        Assert.Equal(data, Dsk.Load(b.ExportMedia()).Tracks[11].Sectors[1].Data);
        Assert.Empty(a.DirtySectors); Assert.Equal(0, b.HeadPosition);
        Assert.All(b.Image.Tracks[11].Sectors[0].Data, v => Assert.Equal(0xe5, v));
    }

    [Theory]
    [InlineData(0x1f00)] [InlineData(0xff00)]
    public void InvalidDmaNeverPartiallyCopiesOrDirties(int dma)
    {
        var (memory, a, _, transfer) = Create();
        var before = memory.Ram.ToArray();
        Assert.Equal(BlockDiskStatus.InvalidDma, transfer.Execute(false, 0, 0, 0, 1, (ushort)dma));
        Assert.Equal(before, memory.Ram.ToArray());
        Assert.Equal(BlockDiskStatus.InvalidDma, transfer.Execute(true, 0, 0, 0, 1, (ushort)dma));
        Assert.Empty(a.DirtySectors);
        Assert.All(a.Image!.Tracks[0].Sectors[0].Data, v => Assert.Equal(0xe5, v));
    }

    [Fact]
    public void DmaUsesCpmAliasRatherThanPhysicalAddressAndLeavesPagingUnchanged()
    {
        var (memory, _, _, transfer) = Create();
        memory.SetCpm(true); memory.Ram.Span[0xc000..0xc200].Fill(0x3c);
        var latches = memory.Latches;
        Assert.Equal(BlockDiskStatus.Success, transfer.Execute(false, 0, 0, 0, 1, 0xc000));
        Assert.All(memory.Ram.Slice(0xe000, 512).ToArray(), v => Assert.Equal(0xe5, v));
        Assert.All(memory.Ram.Slice(0xc000, 512).ToArray(), v => Assert.Equal(0x3c, v));
        Assert.Equal(latches, memory.Latches);
    }

    [Fact]
    public void FortyTrackMediaUsesLogicalCylinderWithoutDoubleStepping()
    {
        var (memory, a, _, transfer) = Create();
        a.Mount(Dsk.Create(new(40, 2, 9, 512)));
        memory.Ram.Span[0x4000..0x4200].Fill(0x42);
        Assert.Equal(BlockDiskStatus.Success, transfer.Execute(true, 0, 39, 1, 9, 0x4000));
        Assert.All(a.Image!.Tracks[79].Sectors[8].Data, v => Assert.Equal(0x42, v));
        Assert.Equal(new DirtySector(39, 1, 8), Assert.Single(a.DirtySectors));
        Assert.Equal(0, a.HeadPosition);
    }

    [Fact]
    public void DmaEndingAtFfffWorksUnlessTopRomIsPagedIn()
    {
        var (memory, a, _, transfer) = Create();
        Assert.Equal(BlockDiskStatus.Success, transfer.Execute(false, 0, 0, 0, 1, 0xfe00));
        Assert.Equal(0xe5, memory.Read(0xffff));
        memory.WritePort7E(2);
        Assert.Equal(BlockDiskStatus.InvalidDma, transfer.Execute(false, 0, 0, 0, 1, 0xfe00));
        Assert.Equal(BlockDiskStatus.InvalidDma, transfer.Execute(true, 0, 0, 0, 1, 0xfe00));
        Assert.Empty(a.DirtySectors);
    }

    [Fact]
    public void AmbiguousIdsAndMediaErrorsDoNotPartiallyCopyAndFullWriteRepairsMarks()
    {
        var (memory, a, _, transfer) = Create();
        var bytes = a.ExportMedia();
        bytes[256 + 24 + 4] = 0x20; // CRC mark
        a.Mount(Dsk.Load(bytes));
        Assert.Equal(BlockDiskStatus.MediaError, transfer.Execute(false, 0, 0, 0, 1, 0x4000));
        Assert.All(memory.Ram.ToArray(), v => Assert.Equal(0, v));
        Assert.Equal(BlockDiskStatus.Success, transfer.Execute(true, 0, 0, 0, 1, 0x4000));
        Assert.Equal(BlockDiskStatus.Success, transfer.Execute(false, 0, 0, 0, 1, 0x4000));
        var repaired = Dsk.Load(a.ExportMedia()).Tracks[0].Sectors[0];
        Assert.Equal(0, repaired.ST1); Assert.Equal(0, repaired.ST2);
        bytes = a.ExportMedia();
        bytes[256 + 24 + 8 + 2] = 1; // duplicate R on same C/H
        a.Mount(Dsk.Load(bytes));
        Assert.Equal(BlockDiskStatus.SectorNotFound, transfer.Execute(true, 0, 0, 0, 1, 0x4000));
        Assert.Empty(a.DirtySectors);
    }

    [Fact]
    public void BadDriveMissingMediaMissingSectorSizeAndProtectionReturnErrors()
    {
        var (memory, a, b, transfer) = Create();
        Assert.Equal(BlockDiskStatus.InvalidDrive, transfer.Execute(false, 2, 0, 0, 1, 0x4000));
        b.Mount(null);
        Assert.Equal(BlockDiskStatus.NoMedia, transfer.Execute(false, 1, 0, 0, 1, 0x4000));
        Assert.Equal(BlockDiskStatus.SectorNotFound, transfer.Execute(false, 0, 80, 0, 1, 0x4000));
        Assert.Equal(BlockDiskStatus.SectorNotFound, transfer.Execute(false, 0, 0, 2, 1, 0x4000));
        Assert.Equal(BlockDiskStatus.SectorNotFound, transfer.Execute(false, 0, 0, 0, 99, 0x4000));
        a.WriteProtected = true;
        Assert.Equal(BlockDiskStatus.WriteProtected, transfer.Execute(true, 0, 0, 0, 1, 0x4000));
        Assert.Empty(a.DirtySectors);
        a.Mount(Dsk.Create(new(80, 2, 9, 256))); a.WriteProtected = false;
        Assert.Equal(BlockDiskStatus.InvalidSectorSize, transfer.Execute(false, 0, 0, 0, 1, 0x4000));
        Assert.All(memory.Ram.ToArray(), v => Assert.Equal(0, v));
    }
}
