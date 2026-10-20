using System.Buffers.Binary;
using System.Text;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.Machine.Tests;

// Resident M: v2 8 KiB drive: one flat 8,388,608-byte (1024 blocks x 8 KiB) raw image with the
// accepted logical DPB (SPT 128, Bsh 6, Blm 63, EXM 1, DSM 1023, DRM 255, ALV 128, CKS/OFF 0;
// KB hc2000-m-base-view-v1 doc amendment-m-v2-geometry-8kib). No DSK container, no skew.
public sealed class MVolumeV2VolumeTests
{
    private const int VolumeBytes = 8_388_608;
    private const int BlockSize = 8_192;
    private static byte[] Volume() { var v = new byte[VolumeBytes]; Array.Fill(v, (byte)0xe5); return v; } // formatted: dead slots 0xE5
    private static void Entry(byte[] raw, int index, int user, string name83, int extent, byte records, params int[] blocks)
    {
        var o = index * 32; Array.Clear(raw, o, 32); raw[o] = (byte)user;
        Encoding.ASCII.GetBytes(name83).AsSpan(0, 11).CopyTo(raw.AsSpan(o + 1));
        raw[o + 12] = (byte)(extent & 31); raw[o + 14] = (byte)(extent >> 5); raw[o + 15] = records;
        foreach (var (block, j) in blocks.Select((b, j) => (b, j)))
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(o + 16 + j * 2), (ushort)block);
    }
    [Fact]
    public void MVolumeV2DpbMatchesPinnedGeometry()
    {
        var fs = CpmFs.OpenMVolumeV2(Volume());
        var dpb = fs.Parameters;
        Assert.Equal(128, dpb.Spt); Assert.Equal(0, dpb.Off); Assert.Equal(0, dpb.Cks);
        Assert.Equal((6, 63, 1, 1023, 255, (byte)0x80), (dpb.Bsh, dpb.Blm, dpb.Exm, dpb.Dsm, dpb.Drm, dpb.Al0));
        Assert.Equal(BlockSize, dpb.BlockSize); Assert.Equal(1, dpb.DirectoryBlocks);
        Assert.Equal(VolumeBytes, dpb.Geometry.Length);
        Assert.Equal(dpb.Spt * 128, dpb.Geometry.Sectors * dpb.Geometry.SectorSize);
        Assert.True(dpb.WordAllocations); Assert.False(dpb.HcHeader);
        Assert.Equal(1023, fs.MaximumBlock);
    }
    [Fact]
    public void MVolumeV2ReadsFilesAcrossBlocksAndTheLastDirectorySlot()
    {
        var raw = Volume();
        Array.Fill(raw, (byte)0x1a, BlockSize, 2 * BlockSize);
        var hello = Enumerable.Range(0, 3000).Select(n => (byte)(n * 17)).ToArray();
        hello.CopyTo(raw, BlockSize); // block 1 holds 3000 of its 8192 bytes
        var expected = new byte[4 * BlockSize + 128];
        for (var i = 0; i < 4; i++)
        {
            Array.Fill(raw, (byte)(0x40 + i), (5 + i) * BlockSize, BlockSize);
            Array.Fill(expected, (byte)(0x40 + i), i * BlockSize, BlockSize);
        }
        Array.Fill(raw, (byte)0x6c, 1023 * BlockSize, BlockSize);
        Array.Fill(expected, (byte)0x6c, 4 * BlockSize, 128);
        Entry(raw, 0, 3, "HELLO   BIN ", 0, 24, 1); // 3000 bytes = 24 records in one 8 KiB block
        Entry(raw, 1, 31, "BIG     COM ", 1, 128, 5, 6, 7, 8); // full extent: 256 records = 4 x 8 KiB
        Entry(raw, 255, 31, "BIG     COM ", 2, 1, 1023);
        var fs = CpmFs.OpenMVolumeV2(raw);
        var listed = fs.List().OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, listed.Length);
        Assert.Equal("BIG.COM", listed[0].Name); Assert.Equal(31, listed[0].User);
        Assert.Equal(257, listed[0].Records); Assert.Equal(5 * BlockSize, listed[0].AllocatedBytes);
        Assert.Equal("HELLO.BIN", listed[1].Name); Assert.Equal(3, listed[1].User);
        Assert.Equal(24, listed[1].Records); Assert.Equal(BlockSize, listed[1].AllocatedBytes);
        var got = fs.Get("HELLO.BIN", 3);
        Assert.Equal(hello, got[..3000]); Assert.All(got[3000..], b => Assert.Equal(0x1a, b));
        Assert.Equal(expected, fs.Get("big.com", 31));
    }
    [Fact]
    public void MVolumeV2EmptyRawVolumeListsEmptyWithoutWarnings()
    {
        var raw = Volume(); Array.Fill(raw, (byte)0xe5); // formatted drive: dead slots stay 0xE5
        var fs = CpmFs.OpenMVolumeV2(raw);
        Assert.Empty(fs.List()); Assert.Empty(fs.Warnings); Assert.Equal(1023, fs.MaximumBlock);
    }
    [Fact]
    public void MVolumeV2UnformattedAllZeroDirectoryIsARefusalNotAnEmptyDrive()
    {
        // An all-zero directory is live-but-invalid, not an empty drive.
        Assert.Throws<InvalidDataException>(() => CpmFs.OpenMVolumeV2(new byte[VolumeBytes]));
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(8388607)] [InlineData(8388609)]
    public void MVolumeV2WrongLengthIsARefusalNotAnEmptyDrive(int length)
    {
        var raw = new byte[length];
        Assert.Throws<InvalidDataException>(() => CpmFs.OpenMVolumeV2(raw));
    }
    [Fact]
    public void MVolumeV2HeaderedDskLookalikeIsARefusal()
    {
        // The drive deliberately has no header: a DSK header must not be guessed past.
        var raw = Volume(); Encoding.ASCII.GetBytes("EXTENDED CPC DSK File\r\nDisk-Info\r\n").CopyTo(raw, 0);
        Assert.Throws<InvalidDataException>(() => CpmFs.OpenMVolumeV2(raw));
    }
    [Fact]
    public void MVolumeV2CorruptDirectoryIsARefusal()
    {
        // Allocation pointer past the physical maximum block (DSM 1023).
        var raw = Volume(); Entry(raw, 0, 0, "BAD     DAT ", 0, 1, 1024);
        Assert.Throws<InvalidDataException>(() => CpmFs.OpenMVolumeV2(raw));
        raw = Volume(); Entry(raw, 0, 0, "BAD     DAT ", 0, 1, 65535);
        Assert.Throws<InvalidDataException>(() => CpmFs.OpenMVolumeV2(raw));
        // Block 0 holds the whole directory (ALV 128) and is reserved.
        raw = Volume(); Entry(raw, 0, 0, "RES0    DAT ", 0, 1, 0);
        Assert.Throws<InvalidDataException>(() => CpmFs.OpenMVolumeV2(raw));
        // 128 records need two 8 KiB blocks; one word is an under-allocated extent.
        raw = Volume(); Entry(raw, 0, 0, "SHORT   DAT ", 0, 128, 9);
        Assert.Throws<InvalidDataException>(() => CpmFs.OpenMVolumeV2(raw));
        // Live entry with a user byte outside 0..31.
        raw = Volume(); Entry(raw, 0, 32, "BAD     DAT ", 0, 1, 9);
        Assert.Throws<InvalidDataException>(() => CpmFs.OpenMVolumeV2(raw));
        // Same file extent recorded twice.
        raw = Volume(); Entry(raw, 0, 0, "DUP     DAT ", 0, 1, 9);
        Entry(raw, 1, 0, "DUP     DAT ", 0, 1, 10);
        Assert.Throws<InvalidDataException>(() => CpmFs.OpenMVolumeV2(raw));
    }
    [Fact]
    public void MVolumeV2ColdDriveRefusesHostSideWrites()
    {
        var raw = Volume(); Entry(raw, 0, 0, "LIVE    COM ", 0, 1, 1);
        var fs = CpmFs.OpenMVolumeV2(raw);
        const string refusal = "M: v2 host view is read-only.";
        Assert.Equal(refusal, Assert.Throws<InvalidDataException>(() => fs.Put("NEW.COM", [1, 2, 3])).Message);
        Assert.Equal(refusal, Assert.Throws<InvalidDataException>(() => fs.Delete("NEW.COM")).Message);
        Assert.Equal(refusal, Assert.Throws<InvalidDataException>(() => fs.Delete("LIVE.COM")).Message);
        Assert.Equal(raw, fs.ExportVolumeBytes());
    }
    [Fact]
    public void MVolumeV2IsNotAddedToTheFloppyPresets()
    {
        var presets = Dpb.Presets();
        Assert.Equal(4, presets.Count);
        Assert.DoesNotContain(Dpb.MVolumeV2(), presets);
    }
}
