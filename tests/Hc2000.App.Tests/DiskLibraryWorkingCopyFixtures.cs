using System.Security.Cryptography;
using Hc2000.App.Library;
using Hc2000.App.Media;
using Hc2000.HostFiles;
using Xunit;

namespace Hc2000.App.Tests;

// Every consumer constructs this fixture, whose first operation skips non-Linux hosts.
#pragma warning disable CA1416

// Independent hand-laid single-track containers, sector data 00..7f. Hashes computed
// separately with Python hashlib, not Dsk.Save/Create or the catalog fixture encoder.
internal sealed class DiskLibraryWorkingCopyFixtures : IDisposable
{
    internal const string StandardHash = "09dc077064577d381d53f5240fe96a924bda9b47a6a1bf1b48ea8ef624fab23b";
    internal const string ExtendedHash = "2fe90a646b74239ec52cad1e558dd932d82e21199b3d30b1e7a6c8338444d929";
    internal const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "hc-copy-test-" + Guid.NewGuid().ToString("N"));
    internal string Source => Path.Combine(Root, "original.dsk");
    internal string CatalogPath => Path.Combine(Root, "catalog.json");
    internal string CopiesPath => Path.Combine(Root, "copies");
    internal DiskLibraryCatalog Catalog { get; private set; } = null!;
    internal DiskLibraryEntry Entry { get; private set; } = null!;
    internal byte[] Prior { get; private set; } = [];
    internal byte[] Bytes { get; }
    internal string Hash { get; }
    private readonly IDisposable domain;

    internal DiskLibraryWorkingCopyFixtures(bool extended = false)
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Real Linux private-directory/native identity contract");
        Directory.CreateDirectory(Root, Private);
        domain = NativeImageAdmission.EnterPrivateTestDomain(Root);
        Bytes = Literal(extended);
        Hash = extended ? ExtendedHash : StandardHash;
        Assert.Equal(Hash, Convert.ToHexStringLower(SHA256.HashData(Bytes)));
        File.WriteAllBytes(Source, Bytes);
        File.SetUnixFileMode(Source, UnixFileMode.UserRead);
    }

    internal async Task RegisterAsync()
    {
        Catalog = DiskLibraryCatalog.Load(CatalogPath, DiskLibraryHostAdmission.StoppedHost);
        var result = await Catalog.RegisterAsync(Source, "manual fixture", "preserve me");
        Assert.Equal(DiskLibraryRefusal.Accepted, result.Reason);
        Entry = Assert.IsType<DiskLibraryEntry>(result.Entry);
        Assert.Equal(DiskLibrarySaveStatus.Published, (await Catalog.SaveAsync()).Status);
        Prior = File.ReadAllBytes(CatalogPath);
    }

    internal Task<DiskLibraryCopyResult> Copy(DiskLibraryWorkingCopyPublication? publication = null,
        DiskLibraryPublication? catalogPublication = null, CancellationToken cancellation = default) =>
        Catalog.CreateWorkingCopyAsync(Entry.Id, Root, publication, catalogPublication, cancellation);

    internal void Preserved(bool priorCatalog = true)
    {
        Assert.Equal(Bytes, File.ReadAllBytes(Source));
        Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(Source));
        Assert.Equal(Entry.FileId, LinuxFileFacts.Inspect(Source).FileId);
        Assert.Equal(Hash, Entry.Sha256);
        Assert.Equal(new DiskLibraryProvenance("manual fixture", "preserve me"), Entry.Provenance);
        if (priorCatalog) Assert.Equal(Prior, File.ReadAllBytes(CatalogPath));
    }

    internal void NoCandidates()
    {
        Assert.Empty(Catalog.Copies);
        if (Directory.Exists(CopiesPath)) Assert.Empty(Directory.GetFiles(CopiesPath));
    }

    internal static byte[] Literal(bool extended)
    {
        var bytes = new byte[768];
        if (extended) "EXTENDED CPC DSK File\r\nDisk-Info\r\n"u8.CopyTo(bytes);
        else "MV - CPCEMU Disk-File\r\nDisk-Info\r\n"u8.CopyTo(bytes);
        bytes[0x30] = bytes[0x31] = 1;
        bytes[extended ? 0x34 : 0x33] = 2;
        "Track-Info\r\n"u8.CopyTo(bytes.AsSpan(0x100));
        bytes[0x115] = 1; bytes[0x116] = 0x4e; bytes[0x117] = 0xe5; bytes[0x11a] = 1;
        if (extended) bytes[0x11e] = 128;
        for (var i = 0; i < 128; i++) bytes[0x200 + i] = (byte)i;
        return bytes;
    }

    public void Dispose()
    {
        domain.Dispose();
        Directory.Delete(Root, true);
    }
}
