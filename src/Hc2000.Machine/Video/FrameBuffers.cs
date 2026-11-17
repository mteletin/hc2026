using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hc2000.Machine.Video;

/// <summary>
/// The pixel arrays of the frames one producer publishes (the ULA, the X2), reused once a frame's consumer hands that frame
/// back with <see cref="Machine.Recycle(VideoFrame)"/>. A published frame stays its consumer's for as long as it keeps it:
/// only a frame handed back is reused, so code that keeps frames, or never hands them back, sees them exactly as before.
/// Each array is 300 KB to 1.7 MB, so it lives on .NET's large object heap, which only a full collection frees: a test that
/// stepped thousands of frames left gigabytes behind (X2ConsoleDeviceTests.TheConsoleMovesToTheVgaScreenAndBack peaked at
/// 11.3 GB; hc2000-ram-one-buffer reuses the frame buffers).
/// An array is taken back only from the very frame it was lent to, once: a stale frame whose array already went to a newer
/// frame is ignored. Thread-safe: the App hands frames back on its emulation thread, tests on theirs.
/// </summary>
public sealed class FrameBuffers
{
    /// <summary>The most arrays of one size kept for reuse; a consumer that hands back more drops the rest.</summary>
    public const int KeepPerSize = 4;

    private sealed class Lease { public VideoFrame? Holder; }

    // Keyed weakly by the array: a frame nobody hands back is collected as before.
    private readonly ConditionalWeakTable<uint[], Lease> lent = new();
    private readonly Dictionary<int, Stack<uint[]>> free = [];
    private readonly object sync = new();
    private int allocated;

    /// <summary>An array of <paramref name="length"/> for the caller to overwrite whole: one handed back, or a new one.</summary>
    internal uint[] Rent(int length)
    {
        lock (sync)
            if (free.TryGetValue(length, out var stack) && stack.TryPop(out var reused)) return reused;
        var array = new uint[length];
        lock (sync) allocated++;
        lent.Add(array, new Lease());
        return array;
    }

    /// <summary>Records that <paramref name="frame"/> carries an array from <see cref="Rent"/>: only that frame can hand it back.</summary>
    internal VideoFrame Lend(VideoFrame frame)
    {
        if (ArrayOf(frame) is { } array && lent.TryGetValue(array, out var lease))
            lock (sync) lease.Holder = frame;
        return frame;
    }

    /// <summary>Takes back the arrays of <paramref name="frame"/> (and of its text overlay) that this producer lent to it.</summary>
    internal void Return(VideoFrame frame)
    {
        if (frame is TextOverlayVideoFrame overlay) Return(overlay.TextOverlay);
        if (ArrayOf(frame) is not { } array || !lent.TryGetValue(array, out var lease)) return;
        lock (sync)
        {
            if (!ReferenceEquals(lease.Holder, frame)) return;
            lease.Holder = null;
            if (!free.TryGetValue(array.Length, out var stack)) free[array.Length] = stack = new();
            if (stack.Count < KeepPerSize) stack.Push(array);
        }
    }

    /// <summary>How many arrays wait for reuse now.</summary>
    public int Kept { get { lock (sync) return free.Values.Sum(stack => stack.Count); } }

    /// <summary>How many arrays have been allocated fresh rather than reused from the pool.</summary>
    public int Allocated { get { lock (sync) return allocated; } }

    private static uint[]? ArrayOf(VideoFrame frame) =>
        MemoryMarshal.TryGetArray(frame.Pixels, out var segment) && segment.Array is { } array && segment.Offset == 0 && segment.Count == array.Length
            ? array : null;
}
