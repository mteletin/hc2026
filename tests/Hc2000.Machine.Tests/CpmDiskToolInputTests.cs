using System.Security.Cryptography;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>Step 6 input contract only. These tests do NOT execute guest disk utilities.</summary>
public sealed class CpmDiskToolInputTests
{
    [StorageAssetFact("disks/cpm/CPM22-HC.DSK")]
    public void SuppliedDiskPinsGuestUtilitiesAndRecordAlignedBinaryPayload()
    {
        var bytes = File.ReadAllBytes(Path.Combine(StorageAssetTests.Root(), "disks/cpm/CPM22-HC.DSK"));
        Assert.Equal("ff5a1c541c8aa80778a0f44d3ece398013084aafd9be1d3f32d2f68b774bb008", Hash(bytes));
        var disk = Dsk.Load(bytes);
        Assert.Equal(new DiskGeometry(80, 2, 9, 512), disk.UniformGeometry());
        var fs = CpmFs.Open(disk, "cpm-hc80");
        Assert.Equal(44, fs.List().Count);
        CheckFile(fs, "PIP.COM", 7424, "7f9e12a92e2bcfd814b5b680a2f7d5c2a2c50c9a5ef94a6891dcaa3527f08ec2");
        CheckFile(fs, "FORMAT.COM", 1152, "d069db72be1af6cd9447c704ed25da8727c66cda4125fb1ddde48c4b13a19a60");
        CheckFile(fs, "SYSGEN.COM", 1024, "6e6746d3b3f615b0e1e4c97d1f172bcd85eaa08cbbd75c4a2adb6a1c515f9e66");

        // PIP.COM itself is a binary payload spanning multiple allocation blocks.
        // CP/M lengths are record-rounded; this deliberately avoids guessed EOF trimming.
        var payload = fs.Get("PIP.COM");
        Assert.Equal(0, payload.Length % 128);
        Assert.True(payload.Length > fs.Parameters.BlockSize);
        Assert.Contains((byte)0x1a, payload); // [O] is required: do not stop at a text EOF byte.
        Assert.Equal(bytes, disk.Save()); // inspection must not repair or modify source media
    }

    [Fact]
    public void BlankDestinationIsDeterministicAndHasNoBootSystemOrFiles()
    {
        var blank = Dsk.Create(new DiskGeometry(80, 2, 9, 512));
        var bytes = blank.Save();
        Assert.Equal(778496, bytes.Length);
        Assert.Equal("afc6db1cb330712e57965f3db4b5bad1468ab11bc05b04d712ddccf8d0dd5f82", Hash(bytes));
        var fs = CpmFs.Open(blank, "cpm-hc80");
        Assert.Empty(fs.List());
        // This is a host-created container, NOT evidence of guest FORMAT or SYSGEN.
        // All sectors (including OFF=4 reserved sides) contain only the E5 filler.
        foreach (var sector in blank.Tracks.SelectMany(t => t.Sectors))
            Assert.All(sector.Data, b => Assert.Equal((byte)0xe5, b));
        Assert.Equal(bytes, Dsk.Load(bytes).Save());
    }

    private static void CheckFile(CpmFs fs, string name, int length, string sha256)
    {
        var file = Assert.Single(fs.List(), f => f.User == 0 && f.Name == name);
        Assert.Equal(length / 128, file.Records);
        var bytes = fs.Get(name);
        Assert.Equal(length, bytes.Length);
        Assert.Equal(sha256, Hash(bytes));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
