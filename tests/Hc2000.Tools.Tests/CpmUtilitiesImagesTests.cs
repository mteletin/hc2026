using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Hc2000.HostFiles;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.Tools.Tests;

public sealed class CpmUtilitiesBuildFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "hc-utilities-build-" + Guid.NewGuid().ToString("N"));
    public string Repo { get; }
    public string Boot => Path.Combine(Root, "boot");
    public string Baseline => Path.Combine(Root, "baseline");
    public const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public CpmUtilitiesBuildFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Hc2000.slnx"))) directory = directory.Parent;
        Repo = directory?.FullName ?? throw new InvalidOperationException("Repository not found.");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        using var process = new Process { StartInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Repo, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        } };
        foreach (var arg in new[] { Path.Combine(Repo, "tools/UpgradeBiosBuilder/bin", configuration, "net10.0/UpgradeBiosBuilder.dll"), "m-paged", Boot })
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(240000)) { process.Kill(true); throw new TimeoutException("M v1 producer timed out."); }
        Assert.True(process.ExitCode == 0, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        CpmUtilitiesImages.GenerateInto(Repo, Baseline, Boot, Commit);
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}

public sealed class CpmUtilitiesImagesTests(CpmUtilitiesBuildFixture fixture) : IClassFixture<CpmUtilitiesBuildFixture>
{
    [Fact]
    public void IndependentBuildsMatchAndTheManifestExplainsEveryFileAndImage()
    {
        var second = Path.Combine(fixture.Root, "second");
        CpmUtilitiesImages.GenerateInto(fixture.Repo, second, fixture.Boot, CpmUtilitiesBuildFixture.Commit);
        var files = Directory.GetFiles(fixture.Baseline, "*", SearchOption.AllDirectories);
        Assert.Equal(18, files.Length); // 13 COM exports, three media, manifest and producer receipt
        foreach (var file in files)
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(second, Path.GetRelativePath(fixture.Baseline, file))));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(second, CpmUtilitiesImages.ManifestName)));
        var doc = manifest.RootElement;
        Assert.Equal(CpmUtilitiesBuildFixture.Commit, doc.GetProperty("sourceCommit").GetString());
        Assert.Equal("hc2000-v2-paged-m-v1", doc.GetProperty("bootProfile").GetString());
        Assert.Equal(6, doc.GetProperty("volumeDpb").GetProperty("Bsh").GetInt32());
        Assert.Equal(1023, doc.GetProperty("volumeDpb").GetProperty("Dsm").GetInt32());
        var floppy = CpmFs.Open(Dsk.Load(File.ReadAllBytes(Path.Combine(second, CpmUtilitiesImages.FloppyName))));
        var volume = CpmFs.OpenMVolumeV2(File.ReadAllBytes(Path.Combine(second, CpmUtilitiesImages.VolumeName)));
        Assert.Equal(13, doc.GetProperty("files").GetArrayLength());
        foreach (var file in doc.GetProperty("files").EnumerateArray())
        {
            var name = file.GetProperty("name").GetString()!;
            var bytes = File.ReadAllBytes(Path.Combine(second, CpmUtilitiesImages.ContentDirectory, name));
            Assert.Equal(file.GetProperty("size").GetInt32(), bytes.Length);
            Assert.Equal(file.GetProperty("sha256").GetString(), Hash(bytes));
            var records = floppy.Get(name);
            Assert.Equal(bytes, records[..bytes.Length]);
            Assert.All(records[bytes.Length..], b => Assert.Equal(0x1a, b));
            Assert.Equal(records, volume.Get(name));
            Assert.Equal(file.GetProperty("recordSize").GetInt32(), records.Length);
            Assert.Equal(file.GetProperty("recordSha256").GetString(), Hash(records));
        }
        Assert.DoesNotContain(floppy.List(), f => f.Name is "FORMAT.COM" or "SYSGEN.COM" or "ED.COM" or "HDDINIT.COM" or "HDSWAP.COM");
        foreach (var input in doc.GetProperty("inputs").EnumerateArray())
            Assert.Equal(input.GetProperty("sha256").GetString(), Hash(File.ReadAllBytes(Path.Combine(fixture.Repo, input.GetProperty("path").GetString()!))));
        foreach (var image in doc.GetProperty("outputs").EnumerateArray())
        {
            var bytes = File.ReadAllBytes(Path.Combine(second, image.GetProperty("name").GetString()!));
            Assert.Equal(image.GetProperty("size").GetInt32(), bytes.Length);
            Assert.Equal(image.GetProperty("sha256").GetString(), Hash(bytes));
        }
        Assert.DoesNotContain(fixture.Repo, File.ReadAllText(Path.Combine(second, "m-v1-source-fit.txt")));
    }

    [Theory]
    [InlineData("disks/cpm/CPM22-HC.DSK")]
    [InlineData("disks/cpm/CPMZ80.DSK")]
    [InlineData("roms/hc2000/eprom-dump-hc2k-2/M27C512.bin")]
    [InlineData("bios/src/hc2000/upgrade/ram.paged.asm")]
    [InlineData("tools/UpgradeBiosBuilder/Program.cs")]
    public void AChangedPinnedInputRefusesBeforeWritingAnyOutput(string changed)
    {
        var copy = CopyInputs();
        var path = Path.Combine(copy, changed);
        var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
        var output = Path.Combine(copy, "output");
        var error = Assert.Throws<InvalidDataException>(() => CpmUtilitiesImages.GenerateInto(copy, output, fixture.Boot, CpmUtilitiesBuildFixture.Commit));
        Assert.Contains(changed, error.Message);
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("CPM22-HC-ram-paged-upgrade.DSK")]
    [InlineData("ram.paged.bin")]
    [InlineData("source-fit.txt")]
    public void AMismatchedExistingProducerArtifactOrReceiptRefuses(string changed)
    {
        var boot = Path.Combine(fixture.Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(boot);
        foreach (var path in Directory.GetFiles(fixture.Boot)) File.Copy(path, Path.Combine(boot, Path.GetFileName(path)));
        File.WriteAllText(Path.Combine(boot, changed), "not the pinned producer output");
        var output = Path.Combine(boot, "output");
        Assert.Throws<InvalidDataException>(() => CpmUtilitiesImages.GenerateInto(fixture.Repo, output, boot, CpmUtilitiesBuildFixture.Commit));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void StaleUnlistedExportsAreRefusedRatherThanLeakingIntoTheCContent()
    {
        var output = Path.Combine(fixture.Root, "stale");
        var content = Path.Combine(output, CpmUtilitiesImages.ContentDirectory);
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "UNKNOWN.COM"), "not allowlisted");
        Assert.Throws<InvalidDataException>(() => CpmUtilitiesImages.GenerateInto(fixture.Repo, output, fixture.Boot, CpmUtilitiesBuildFixture.Commit));
        Assert.Equal("not allowlisted", File.ReadAllText(Path.Combine(content, "UNKNOWN.COM")));
        Assert.Single(Directory.GetFiles(output, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void MissingInputAndMissingCommitCannotBecomeAHandMadeSubstitute()
    {
        var output = Path.Combine(fixture.Root, "missing");
        Assert.Throws<InvalidDataException>(() => CpmUtilitiesImages.GenerateInto(fixture.Repo, output, fixture.Boot, ""));
        var copy = CopyInputs();
        File.Delete(Path.Combine(copy, "disks/cpm/CPMZ80.DSK"));
        Assert.Throws<FileNotFoundException>(() => CpmUtilitiesImages.GenerateInto(copy, output, fixture.Boot, CpmUtilitiesBuildFixture.Commit));
        Assert.False(Directory.Exists(output));
    }

    private string CopyInputs()
    {
        var root = Path.Combine(fixture.Root, Guid.NewGuid().ToString("N"));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Baseline, CpmUtilitiesImages.ManifestName)));
        foreach (var input in doc.RootElement.GetProperty("inputs").EnumerateArray())
        {
            var path = input.GetProperty("path").GetString()!;
            var target = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(fixture.Repo, path), target);
        }
        return root;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
