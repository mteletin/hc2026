using System.Collections.Immutable;
using System.Security.Cryptography;
using Hc2000.Machine;

namespace Hc2000.App.Media;

public sealed record SelectedProfileRom(ProfileRomRegion Region, ImmutableArray<byte> Bytes, string Sha256);
/// <summary>Detached validated bytes only. Selection neither constructs nor modifies a machine.</summary>
public sealed record SelectedProfileRomSet(string ProfileId, string AssetRoot, ImmutableArray<SelectedProfileRom> Regions);

/// <summary>Closed faithful registry. Identity is pinned to the repository manifest, never to a
/// manifest supplied by the user. File presence/size alone is not ROM identity or boot evidence.</summary>
public static class ProfileRomSelection
{
    public static IReadOnlyList<string> ProfileIds { get; } = Array.AsReadOnly(new[] { "hc2000-v2", "hc2000-v1" });
    // MANIFEST.sha256 canonical v1 entries. V2 has no registry source file for the CP/M lower half and cannot be staged as a full set.
    private static readonly IReadOnlyDictionary<string, string> Hashes = new Dictionary<string, string>
    {
        ["roms/hc2000/v1/basic.rom"] = "fcb2da8daa8e1698b898a13b8baf9556d7fe2c7f2039bd1b7a6033185e0798b8",
        ["roms/hc2000/v1/cpm.rom"] = "4e5a52e98cbe5f9d42bcb2dcdd38cc6095f42dc0fb437be218dacac3da3830fa",
        ["roms/hc2000/v1/if1.rom"] = "40fb91d5a42ca4eb0982966c26144e9136b8c333cc886d864cebcaddd9267ec9"
    };

    /// <summary>Whole-chip identity (MANIFEST): the owner's EPROM dump, loaded whole, never re-cut slices.</summary>
    public const string WholeChipSourcePath = "roms/hc2000/eprom-dump-hc2k-2/M27C512.bin";
    public const int WholeChipLength = 0x10000;
    private const string WholeChipSha256 = "83e3213b049c5b849ee32e04da901eba06992d5aa1f44f76564f312c79748d2b";
    // Physical offsets of each mapped half inside the whole-chip image.
    private const int WholeChipBasicOffset = 0x8000; // main ROM 0x0000 (BASIC), identical to v2/basic.rom
    private const int WholeChipCpmOffset = 0xc000;   // main ROM 0x4000 (CP/M lower+upper)
    private const int WholeChipIf1Offset = 0x4000;   // shadow ROM 0x0000 (IF1)


    public static MachineProfile Describe(string profileId) => profileId switch
    {
        "hc2000-v2" => MachineProfile.Hc2000V2,
        "hc2000-v1" => MachineProfile.Hc2000V1,
        _ => throw new ArgumentException("Unknown faithful profile ID (case-sensitive).", nameof(profileId))
    };

    public static async Task<SelectedProfileRomSet> SelectAsync(string profileId, string assetRoot)
    {
        var profile = Describe(profileId);
        profile.Validate();
        if (!profile.HasCompleteRomDescription)
            throw new InvalidDataException("hc2000-v2 cannot be staged as a complete set: the profile registry has no source file for the CP/M lower half (mainROM 0x4000..0x5fff). The v2 8K CP/M image is the complete BIOS (the rest of CP/M loads from disk at boot), not a broken set. Canonical upper 8K cpm-hc2kzip.bin stays at 0x6000; no padding/substitution allowed.");
        if (string.IsNullOrWhiteSpace(assetRoot)) throw new ArgumentException("Choose the asset root containing roms/hc2000/.", nameof(assetRoot));
        var root = Path.GetFullPath(assetRoot);
        var selected = ImmutableArray.CreateBuilder<SelectedProfileRom>();
        foreach (var region in profile.RomRegions)
        {
            var source = region.SourcePath!; // canonical complete description, validated above
            var path = Path.Combine(root, source.Replace('/', Path.DirectorySeparatorChar));
            ImmutableArray<byte> bytes;
            try { bytes = await MediaFiles.ReadAsync(path, region.SourceOffset + region.Length); }
            catch (FileNotFoundException ex) { throw new InvalidDataException($"ROM unavailable: {source}.", ex); }
            catch (DirectoryNotFoundException ex) { throw new InvalidDataException($"ROM unavailable: {source}.", ex); }
            catch (InvalidDataException ex) { throw new InvalidDataException($"ROM size mismatch: {source}. {ex.Message}", ex); }
            if (bytes.Length != region.SourceOffset + region.Length)
                throw new InvalidDataException($"ROM size mismatch: {source}, expected {region.SourceOffset + region.Length} bytes; got {bytes.Length}. LFS pointers are not ROMs.");
            var hash = Convert.ToHexString(SHA256.HashData(bytes.AsSpan())).ToLowerInvariant();
            if (!Hashes.TryGetValue(source, out var expected) || hash != expected)
                throw new InvalidDataException($"ROM identity mismatch: {source}. Use the canonical ROM for {profileId}; same size is not enough.");
            selected.Add(new(region, bytes, hash));
        }
        // Publish only after every region passed: failures never leave a partial selection.
        return new(profile.Name, root, selected.ToImmutable());
    }

        /// <summary>
        /// Load the whole 64-KiB EPROM dump as the v2 machine ROM, composed through the existing
        /// paging rather than re-cut per-file slices. The chip IS the ROM map: BASIC is the image at
        /// 0x8000..0xC000 (identical to v2/basic.rom), CP/M is 0xC000..0x10000 (this supplies the
        /// lower half the v2 file set omits, so the set is complete and faithful, not padded), and
        /// the IF1 shadow ROM is 0x4000..0x8000. Identity is the whole-chip MANIFEST hash, never a
        /// slice hash. No path is read after this returns.
        /// </summary>
        public static (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) LoadWholeChipV2(string assetRoot)
        {
            if (string.IsNullOrWhiteSpace(assetRoot)) throw new ArgumentException("Choose the asset root containing roms/hc2000/.", nameof(assetRoot));
            var root = Path.GetFullPath(assetRoot);
            var path = Path.Combine(root, WholeChipSourcePath.Replace('/', Path.DirectorySeparatorChar));
            byte[] chip;
            try { chip = MediaFiles.ReadAsync(path, WholeChipLength).GetAwaiter().GetResult().ToArray(); }
            catch (FileNotFoundException ex) { throw new InvalidDataException($"ROM unavailable: {WholeChipSourcePath}.", ex); }
            catch (DirectoryNotFoundException ex) { throw new InvalidDataException($"ROM unavailable: {WholeChipSourcePath}.", ex); }
            catch (InvalidDataException ex) { throw new InvalidDataException($"ROM size mismatch: {WholeChipSourcePath}, larger than {WholeChipLength} bytes. {ex.Message}", ex); }
            if (chip.Length != WholeChipLength)
                throw new InvalidDataException($"ROM size mismatch: {WholeChipSourcePath}, expected {WholeChipLength} bytes; got {chip.Length}. LFS pointers are not ROMs.");
            var hash = Convert.ToHexString(SHA256.HashData(chip)).ToLowerInvariant();
            if (hash != WholeChipSha256)
                throw new InvalidDataException($"ROM identity mismatch: {WholeChipSourcePath}. Use the canonical whole-chip EPROM dump; same size is not enough.");
            var profile = MachineProfile.Hc2000V2;
            profile.Validate();
            var basic = chip[WholeChipBasicOffset..(WholeChipBasicOffset + 0x4000)];
            var cpm = chip[WholeChipCpmOffset..(WholeChipCpmOffset + 0x4000)];
            var if1 = chip[WholeChipIf1Offset..(WholeChipIf1Offset + 0x4000)];
            return (profile, basic, cpm, if1);
        }

    /// <summary>Asset root whose <c>roms/</c> tree carries the bundled M27C512.bin.
    /// First match wins (spec v4.2): the installed config dir
    /// (<c>$XDG_CONFIG_HOME/hc2000</c> or <c>~/.config/hc2000</c>, then <c>%APPDATA%\hc2000</c>),
    /// then the upward walk from the app binary (dev, tests, and package-local
    /// <c>AppContext.BaseDirectory/roms</c>). The returned directory contains
    /// <c>roms/hc2000/eprom-dump-hc2k-2/M27C512.bin</c>. Provenance stays in
    /// <see cref="LoadWholeChipV2"/>.</summary>
    public static string DefaultAssetRoot() =>
        ResolveAssetRoot(InstalledLayout.ConfigDirectories(), AppContext.BaseDirectory);

    /// <summary>Same search as <see cref="DefaultAssetRoot"/>, with the installed config dirs
    /// and the walk start injected so tests do not depend on the seat's home.</summary>
    internal static string ResolveAssetRoot(IReadOnlyList<string> configDirectories, string startDirectory)
    {
        var relative = WholeChipSourcePath.Replace('/', Path.DirectorySeparatorChar);
        var checkedPaths = new List<string>();
        foreach (var config in configDirectories)
        {
            if (string.IsNullOrWhiteSpace(config)) continue;
            var root = Path.GetFullPath(config);
            var candidate = Path.Combine(root, relative);
            checkedPaths.Add(candidate);
            if (File.Exists(candidate)) return root;
        }
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            checkedPaths.Add(candidate);
            if (File.Exists(candidate)) return dir.FullName;
        }
        throw new InvalidOperationException(
            "Cannot locate the bundled roms/ tree (roms/hc2000/eprom-dump-hc2k-2/M27C512.bin). Checked: "
            + string.Join("; ", checkedPaths));
    }




    /// <summary>Re-check detached data at the machine boundary; DTO claims and hashes are untrusted.</summary>
    internal static (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) CopyVerifiedV1(SelectedProfileRomSet selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (!string.Equals(selected.ProfileId, "hc2000-v1", StringComparison.Ordinal))
            throw new InvalidDataException("Only the complete hc2000-v1 set is supported by BASIC runtime composition; the incomplete v2 registry set (no CP/M lower-half source file; its 8K CP/M image is the complete BIOS) is not staged here.");
        var profile = MachineProfile.Hc2000V1;
        profile.Validate();
        if (!profile.HasCompleteRomDescription || selected.Regions.IsDefault || selected.Regions.Length != profile.RomRegions.Count)
            throw new InvalidDataException("The selected BASIC ROM set is incomplete or has an unexpected region count.");
        var copies = new Dictionary<ProfileRomRole, byte[]>();
        for (var i = 0; i < profile.RomRegions.Count; i++)
        {
            var expectedRegion = profile.RomRegions[i];
            var rom = selected.Regions[i];
            if (rom is null || rom.Region != expectedRegion || rom.Bytes.IsDefault || rom.Bytes.Length != expectedRegion.Length)
                throw new InvalidDataException($"The selected BASIC ROM set has an invalid {expectedRegion.Role} region.");
            var hash = Convert.ToHexString(SHA256.HashData(rom.Bytes.AsSpan())).ToLowerInvariant();
            if (!Hashes.TryGetValue(expectedRegion.SourcePath!, out var expectedHash) || hash != expectedHash
                || !string.Equals(rom.Sha256, expectedHash, StringComparison.Ordinal))
                throw new InvalidDataException($"ROM identity mismatch: {expectedRegion.SourcePath}. Use the canonical hc2000-v1 bytes.");
            copies.Add(expectedRegion.Role, rom.Bytes.ToArray());
        }
        return (profile, copies[ProfileRomRole.Basic], copies[ProfileRomRole.Cpm], copies[ProfileRomRole.If1]);
    }
}
