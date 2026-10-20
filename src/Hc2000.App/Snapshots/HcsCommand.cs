using System.Collections.Immutable;
using Hc2000.App.Input;

namespace Hc2000.App.Snapshots;

public enum HcsAction { Save, Load }

/// <summary>One copied snapshot request in the shared input/control FIFO. The UI owns file
/// pickers and persistence; only this immutable command and the immutable reply cross the
/// emulation boundary. Load carries the whole .hcs file bytes, exactly like InsertDisk.</summary>
public sealed record HcsCommand(HcsAction Action, ImmutableArray<byte> Bytes = default, long RequestId = 0, bool DiscardChanges = false) : InputCommand;

public sealed record HcsReply(long RequestId, bool Applied = false,
    ImmutableArray<byte> Snapshot = default, string? Error = null);
