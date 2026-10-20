using System.Collections.Immutable;
using Hc2000.Machine.Snapshots;
using LiveMachine = Hc2000.Machine.Machine;

namespace Hc2000.App.Snapshots;

/// <summary>Owner-side adapter around the public full <see cref="LiveMachine.Save"/>/
/// <see cref="LiveMachine.Load"/> API (04g). It adds no codec, capture or adoption logic of its
/// own: both calls run on the emulation owner at an ordered input boundary, and any failure
/// is copied as a message into the reply for UI display. A failed Load leaves the machine
/// untouched, which the boundary caller can rely on for the next command.</summary>
public sealed class HcsSession(LiveMachine machine)
{
    private readonly int owner = Environment.CurrentManagedThreadId;

    public HcsReply Apply(HcsCommand command)
    {
        if (owner != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Snapshots belong to the emulation owner.");
        if (!Enum.IsDefined(command.Action))
            throw new ArgumentException("Invalid snapshot command.");
        try
        {
            switch (command.Action)
            {
                case HcsAction.Save:
                    return new(command.RequestId, true, ImmutableArray.CreateRange(machine.Save()), null);
                case HcsAction.Load:
                    machine.Load(command.Bytes.ToArray());
                    return new(command.RequestId, true, default, null);
            }
            throw new InvalidOperationException("Invalid snapshot command.");
        }
        catch (Exception ex) when (ex is HcsException or InvalidDataException or InvalidOperationException)
        {
            return new(command.RequestId, false, default, ex.Message);
        }
    }
}
