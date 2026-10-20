using System.Collections.ObjectModel;

namespace Hc2000.Machine.Floppy;

public sealed record FdcDiagnosticSnapshot(FdcPhase Phase, byte MainStatus,
    ReadOnlyCollection<byte> Command, ReadOnlyCollection<byte> LastCommand,
    byte? LastSt0, byte? LastSt1, byte? LastSt2, byte? LastSt3,
    ReadOnlyCollection<byte> PresentCylinders,
    ReadOnlyCollection<(byte Status, byte Cylinder)>? PendingInterrupts = null);
