using Hc2000.HostFiles;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.Tools.Tests;

/// <summary>Media-home create, KB hc2000-media-home-scan-admission-v1 sec 4: the one writer to a media
/// home; refuses an unset, missing or not-a-directory home and an existing name; never registers.</summary>
public sealed class MediaHomeCreateTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc-media-home-" + Guid.NewGuid().ToString("N"));
    private readonly NativeImageAdmissionTests.Domain nativeDomain = new();
    private readonly IDisposable nativeScope;
    public MediaHomeCreateTests() { Directory.CreateDirectory(directory); nativeScope = nativeDomain.Enter(); }
    public void Dispose() { nativeScope.Dispose(); nativeDomain.Dispose(); Directory.Delete(directory, true); }
    private string PathIn(string name) => Path.Combine(directory, name);

    [Fact]
    public void AnUnsetMissingHomeOrNotADirectoryIsRefused()
    {
        Assert.Throws<ArgumentException>(() => MediaHomeCreate.Create(null, "a.dsk", MediaBlanks.TapeBlank));
        Assert.Throws<ArgumentException>(() => MediaHomeCreate.Create("   ", "a.dsk", MediaBlanks.TapeBlank));
        Assert.Throws<IOException>(() => MediaHomeCreate.Create(PathIn("no-such-home"), "a.dsk", MediaBlanks.TapeBlank));
        var file = PathIn("a-file"); File.WriteAllText(file, "x");
        Assert.Throws<IOException>(() => MediaHomeCreate.Create(file, "a.dsk", MediaBlanks.TapeBlank));
        Assert.Empty(Directory.GetFiles(directory, "*.hc-*"));
    }

    [Fact]
    public void AnExistingNameIsRefusedUntouchedAndTheNameMustStayInsideTheHome()
    {
        var existing = PathIn("taken.dsk"); File.WriteAllBytes(existing, [1, 2, 3]);
        var before = File.ReadAllBytes(existing);
        var error = Assert.Throws<IOException>(() => MediaHomeCreate.Create(directory, "taken.dsk", MediaBlanks.Floppy720));
        Assert.Contains(existing, error.Message);
        Assert.Equal(before, File.ReadAllBytes(existing));
        Assert.Throws<ArgumentException>(() => MediaHomeCreate.Create(directory, "../escape.dsk", MediaBlanks.TapeBlank));
        Assert.Empty(Directory.GetFiles(directory, "*.hc-*"));
    }

    [Fact]
    public void CreateWritesTheSharedWritersBytesAsNewFiles()
    {
        var floppy = MediaHomeCreate.Create(directory, "new.dsk", MediaBlanks.Floppy720);
        Assert.Equal(Path.Combine(directory, "new.dsk"), floppy);
        Assert.Equal(MediaBlanks.Floppy720(), File.ReadAllBytes(floppy));
        var volume = MediaHomeCreate.Create(directory, "volume.raw", MediaBlanks.HddVolume8M);
        var volumeBytes = File.ReadAllBytes(volume);
        Assert.Equal(8_388_608, volumeBytes.Length);
        Assert.True(volumeBytes.All(b => b == 0xe5));
        var tape = MediaHomeCreate.Create(directory, "blank.tap", MediaBlanks.TapeBlank);
        Assert.Empty(File.ReadAllBytes(tape));
        Assert.Empty(Directory.GetFiles(directory, "*.hc-*"));
        // "Never registers": the method writes bytes only; nothing is added to any catalog or registry.
    }
}
