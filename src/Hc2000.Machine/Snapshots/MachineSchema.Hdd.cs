using System.Text.Json;
using Hc2000.Machine.Floppy;

namespace Hc2000.Machine.Snapshots;

public sealed partial class MachineSchema
{
    internal const string HddComponentId = "hdd";
    internal static bool HasHdd(MachineProfile profile) => profile.BlockDisk is BlockDiskPolicy.HddV1 or BlockDiskPolicy.HddGuestV1;

    private void ValidateHddModel(JsonElement manifest)
    {
        var components = manifest.GetProperty("components").EnumerateArray()
            .Where(c => Text(c.GetProperty("id")) == HddComponentId).ToArray();
        if (components.Length != (HasHdd(profile) ? 1 : 0)) Error(HcsError.StateInconsistent, "HDD composition");
        if (components.Length == 0) return;
        var state = components[0].GetProperty("state");
        Fields(state, "pointer", "status", "slots");
        UInt(state.GetProperty("pointer"), ushort.MaxValue);
        UInt(state.GetProperty("status"), byte.MaxValue);
        var slots = Arr(state.GetProperty("slots"));
        if (slots.Length != 4) Error(HcsError.StateInconsistent, "HDD slots");
        for (var i = 0; i < 4; i++)
        {
            var slot = slots[i];
            Fields(slot, "assetId", "generation", "writeProtected", "dirty");
            var id = "hdd-" + (char)('c' + i);
            if (Text(slot.GetProperty("assetId")) != id) Error(HcsError.AssetMismatch, "HDD slot asset");
            var generation = UInt(slot.GetProperty("generation"));
            CheckBool(slot, "writeProtected"); CheckBool(slot, "dirty");
            var assets = manifest.GetProperty("assets").EnumerateArray().Where(a => Text(a.GetProperty("id")) == id).ToArray();
            if (assets.Length != 1) Error(HcsError.AssetMismatch, "HDD asset");
            var asset = assets[0];
            if (Text(asset.GetProperty("kind")) != "buffer" || Text(asset.GetProperty("format")) != "raw1")
                Error(HcsError.AssetMismatch, "HDD asset format");
            var length = Counter(asset.GetProperty("byteLength"));
            if (length is not (0 or HddSlot.ImageBytes) || length != 0 && generation == 0 ||
                length == 0 && (slot.GetProperty("dirty").GetBoolean() || slot.GetProperty("writeProtected").GetBoolean()))
                Error(HcsError.StateInconsistent, "HDD logical media");
        }
    }

    internal static BlockDiskPortState ReadHddState(JsonElement state, Func<string, byte[]> bytes) => new(
        state.GetProperty("pointer").GetUInt16(), state.GetProperty("status").GetByte(),
        state.GetProperty("slots").EnumerateArray().Select(slot => new HddSlotState(
            bytes(slot.GetProperty("assetId").GetString()!), slot.GetProperty("generation").GetUInt32(),
            slot.GetProperty("writeProtected").GetBoolean(), slot.GetProperty("dirty").GetBoolean())).ToArray());
}
