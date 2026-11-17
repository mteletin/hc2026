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

    /// <summary>Checks the current machine before loading onto a detached replacement.</summary>
    public string? ValidateReplacement()
    {
        if (owner != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Snapshots belong to the emulation owner.");
        try { machine.ValidateSessionSnapshotLoad(); return null; }
        catch (Exception ex) when (ex is HcsException or InvalidOperationException) { return ex.Message; }
    }

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
                    return new(command.RequestId, true, System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(machine.Save()), null);
                case HcsAction.Load:
                    // Stream the command bytes in place: the command already owns the whole file
                    // bytes, so no second whole-image array is made before the load. The stream
                    // is local to this call and read-only in use.
                    machine.Load(new ImmutableArrayStream(command.Bytes));
                    return new(command.RequestId, true, default, null);
            }
            throw new InvalidOperationException("Invalid snapshot command.");
        }
        catch (Exception ex) when (ex is HcsException or InvalidDataException or InvalidOperationException)
        {
            return new(command.RequestId, false, default, ex.Message);
        }
    }

    /// <summary>Read-only seekable view of the command's bytes; never copies the array.</summary>
    private sealed class ImmutableArrayStream(ImmutableArray<byte> data) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
        private long position;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= data.Length) return 0;
            int n = checked((int)Math.Min(count, data.Length - position));
            data.AsSpan(checked((int)position), n).CopyTo(buffer.AsSpan(offset, n));
            position += n;
            return n;
        }
        public override int Read(Span<byte> buffer)
        {
            int n = checked((int)Math.Min(buffer.Length, data.Length - position));
            data.AsSpan(checked((int)position), n).CopyTo(buffer);
            position += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            long basePosition = origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => position,
                SeekOrigin.End => data.Length,
                _ => throw new ArgumentException("Invalid origin.", nameof(origin))
            };
            long next = basePosition + offset;
            if (next < 0 || next > data.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            position = next;
            return next;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
