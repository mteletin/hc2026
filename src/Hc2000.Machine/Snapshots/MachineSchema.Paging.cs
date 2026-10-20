using System.Security.Cryptography;
using System.Text.Json;
using Hc2000.Machine.Memory;

namespace Hc2000.Machine.Snapshots;

public sealed partial class MachineSchema
{
    private void ValidatePagedState(JsonElement state)
    {
        if (!state.TryGetProperty("paged", out var paged) || paged.ValueKind == JsonValueKind.Null) return;
        if (profile.Memory.RamPaged is null) Error(HcsError.ProfileMismatch, "ram.paged on a device-less profile");
        var mTransfer = PagedRamPolicy.IsMTransfer(profile.Memory.RamPaged?.PolicyId);
        if (mTransfer)
        {
            Fields(paged, "v", "view", "bank", "screen", "rom", "locked", "transfer", "ramBytes", "ramSha256");
            if (Text(paged.GetProperty("view")) != "m-transfer") Error(HcsError.StateInconsistent, "ram.paged view");
            CheckBool(paged, "transfer");
        }
        else Fields(paged, "v", "bank", "screen", "rom", "locked", "ramBytes", "ramSha256");
        if (UInt(paged.GetProperty("v")) != (mTransfer ? 2u : 1u)) Error(HcsError.UnsupportedVersion, "ram.paged");
        UInt(paged.GetProperty("bank"), 65535);
        CheckBool(paged, "screen"); CheckBool(paged, "rom"); CheckBool(paged, "locked");
        if (Counter(paged.GetProperty("ramBytes")) != PagedRamPolicy.ExtraBytes)
            Error(HcsError.StateInconsistent, "ram.paged length");
        var sha = Text(paged.GetProperty("ramSha256"));
        if (sha.Length != 64 || sha.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            Error(HcsError.InvalidFormat, "ram.paged hash");
    }

    // The large-blob allowance is compiled from the trusted profile, not a file-supplied length.
    // Only the unique raw main-RAM store gets it; disk, tape and arbitrary buffers keep their caps.
    private bool AdmitPagedRamBlob(JsonElement manifest, uint blobId, int length)
    {
        if (profile.Memory.RamPaged is null || length != PagedRamPolicy.RamBytes) return false;
        var refs = manifest.GetProperty("assets").EnumerateArray()
            .Where(a => UInt(a.GetProperty("blobId")) == blobId).ToArray();
        if (refs.Length != 1) return false;
        var asset = refs[0];
        if (Text(asset.GetProperty("id")) != "ram" || Text(asset.GetProperty("kind")) != "ram"
            || Text(asset.GetProperty("format")) != "raw1" || Counter(asset.GetProperty("byteLength")) != length) return false;
        var stores = manifest.GetProperty("stores").EnumerateArray()
            .Where(s => Text(s.GetProperty("assetId")) == "ram").ToArray();
        return stores.Length == 1 && Text(stores[0].GetProperty("id")) == "ram";
    }

    private void ValidatePagedBlobModel(JsonElement manifest, byte[][] blobs)
    {
        if (profile.Memory.RamPaged is null) return;
        var memory = manifest.GetProperty("components").EnumerateArray().Single(c => Text(c.GetProperty("id")) == "memory").GetProperty("state");
        if (!memory.TryGetProperty("paged", out var paged) || paged.ValueKind == JsonValueKind.Null) return;
        var ram = manifest.GetProperty("assets").EnumerateArray().Single(a => Text(a.GetProperty("id")) == "ram");
        var data = blobs[UInt(ram.GetProperty("blobId")) - 1];
        if (data.Length != PagedRamPolicy.RamBytes || Text(paged.GetProperty("ramSha256")) !=
            Convert.ToHexStringLower(SHA256.HashData(data.AsSpan(0x10000))))
            Error(HcsError.AssetMismatch, "ram.paged identity");
    }
}
