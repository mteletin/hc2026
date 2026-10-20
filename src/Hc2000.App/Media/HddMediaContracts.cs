using System.Collections.Immutable;

namespace Hc2000.App.Media;

// App payloads only. These are not a guest descriptor or a snapshot format.
public enum HddAction { Inspect, CaptureSave, AcknowledgeSave, CancelSave, Attach, Detach }
public sealed record HddRequest(HddAction Action, byte Drive = 2, long SaveId = 0);
public sealed record HddDriveView(byte Drive, string? Name, bool Present, uint Generation,
    bool WriteProtected, bool Dirty);
public sealed record HddSaveCapture(long Id, byte Drive, uint Generation, ImmutableArray<byte> Bytes);
public sealed record HddReply(ImmutableArray<HddDriveView> Drives, HddSaveCapture? Save = null, string? Error = null)
{
    public static HddReply Unavailable { get; } = new([], Error: "HDD profile is off; cold NEW-machine configuration only.");
}

/// <summary>Host-validated input for a NEW, non-restored machine before its first instruction.</summary>
public sealed record HddInitialMedia(byte Drive, ImmutableArray<byte> Bytes, string? Name = null, bool WriteProtected = false);
