using System.Collections.Immutable;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Media;

// App payloads only. These are not a guest descriptor or a snapshot format.
public enum HddAction { Inspect, CaptureSave, AcknowledgeSave, CancelSave, Attach, Detach, BeginSwitch, CancelSwitch }
public sealed record HddSwitchTarget(string Path, HddVolumeIdentity Identity, bool Writable = false);
public enum HddSwitchState { AwaitingGuest, Committed, Cancelled, Unknown }
public sealed record HddSwitchView(ulong Id, byte Drive, HddSwitchState State, string? Error = null);
public sealed record HddRequest(HddAction Action, byte Drive = 2, long SaveId = 0,
    HddSwitchTarget? Target = null, uint? ExpectedGeneration = null, bool DiscardChanges = false, ulong SwitchId = 0);
public sealed record HddDriveView(byte Drive, string? Name, bool Present, uint Generation,
    bool WriteProtected, bool Dirty);
public sealed record HddSaveCapture(long Id, byte Drive, uint Generation, ImmutableArray<byte> Bytes);
public sealed record HddPublicationView(long SaveId, bool Acknowledged, bool RequiresFence,
    Hc2000.HostFiles.HddContainerPublication.OfflineReceipt? Receipt = null, string? Error = null);
public sealed record HddReply(ImmutableArray<HddDriveView> Drives, HddSaveCapture? Save = null, string? Error = null,
    HddSwitchView? Switch = null)
{
    public static HddReply Unavailable { get; } = new([], Error: "HDD profile is off; cold NEW-machine configuration only.");
}

/// <summary>Host-validated input for a NEW, non-restored machine before its first instruction.</summary>
public sealed record HddInitialMedia(byte Drive, ImmutableArray<byte> Bytes, string? Name = null, bool WriteProtected = false)
{
    internal HddSwitchTarget? Binding { get; init; }
}
