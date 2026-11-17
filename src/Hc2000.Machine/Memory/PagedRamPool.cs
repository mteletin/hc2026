using System.Runtime;
using System.Runtime.InteropServices;

namespace Hc2000.Machine.Memory;

/// <summary>The paged machine's 1 GiB + 64 KiB RAM arrays, allocated once per concurrently live machine and reused for
/// the life of the process (hc2000-ram-one-buffer: one allocation kept from the start, never freed and allocated again).
/// A machine rents its RAM when it is built and returns it when it is disposed; the next
/// machine gets the same array, zeroed, instead of a new gigabyte the GC must find. A returned array costs no memory
/// while it waits: on Linux its pages go back to the system and read as zero again (a fresh 1 GiB array is resident
/// only where the guest writes, about 250 MB for a booted CP/M, and clearing it would make all of it resident).
/// Smaller stores are not pooled.</summary>
public static class PagedRamPool
{
    /// <summary>Stores at least this large come from the pool; every other size is a plain allocation.</summary>
    public const int MinimumBytes = 64 << 20;

    private static readonly Lock gate = new();
    private static readonly Dictionary<int, Stack<byte[]>> free = [];
    private static readonly Dictionary<byte[], (object? Renter, IDisposable? Owner)> rented = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<object, (int Live, int Peak)> renters = [];
    private static int allocated;

    /// <summary>Arrays the pool has ever allocated; stays at the peak number of machines live at once.</summary>
    public static int Allocated { get { lock (gate) return allocated; } }

    /// <summary>Arrays rented and not yet returned.</summary>
    public static int Outstanding { get { lock (gate) return rented.Count; } }

    /// <summary>Test seam: names who is renting right now (the running test, say), so <see cref="Rentals"/> can hold
    /// each renter to one live machine. Null, the default, names nobody.</summary>
    public static Func<object?>? Renter { get; set; }

    /// <summary>A zeroed array of <paramref name="length"/> bytes: a returned one, or a new one.
    /// <paramref name="owner"/> is what <see cref="Reclaim"/> disposes to take the array back from a renter that left it.</summary>
    public static byte[] Rent(int length, IDisposable? owner = null)
    {
        if (length < MinimumBytes) return new byte[length];
        var who = Renter?.Invoke();
        byte[]? array = null;
        lock (gate)
        {
            if (free.TryGetValue(length, out var stack) && stack.Count > 0) array = stack.Pop();
            else allocated++;
        }
        if (array is null)
        {
            try { array = new byte[length]; }
            catch (OutOfMemoryException ex)
            {
                Console.Error.WriteLine($"[PagedRamPool OOM] Length={length}, Allocated={Allocated}, Outstanding={Outstanding}: {ex.Message}");
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                try
                {
                    array = new byte[length];
                    Console.Error.WriteLine($"[PagedRamPool OOM Recovery Success] Allocated array after collection");
                }
                catch (OutOfMemoryException retryEx)
                {
                    Console.Error.WriteLine($"[PagedRamPool OOM Recovery FAILED]: {retryEx}");
                    throw;
                }
            }
        }
        lock (gate)
        {
            rented[array] = (who, owner);
            if (who is not null)
            {
                var (live, peak) = renters.GetValueOrDefault(who);
                renters[who] = (live + 1, Math.Max(peak, live + 1));
            }
        }
        return array;
    }

    /// <summary>Hands a rented array back for the next machine. An array the pool did not rent, or one already
    /// returned, is ignored, so a double dispose cannot give one array to two machines.</summary>
    public static void Return(byte[] array) => Release(array, reuse: true);

    /// <summary>Lets go of a rented array without handing it to another machine: it no longer counts as live, and the GC
    /// frees it once nothing references it. For a machine retired while some of its parts may still hold its array (the App
    /// retiring a machine at a reset or a snapshot load), where reuse could put another machine's bytes under them.</summary>
    public static void Forget(byte[] array) => Release(array, reuse: false);

    private static void Release(byte[] array, bool reuse)
    {
        ArgumentNullException.ThrowIfNull(array);
        lock (gate)
        {
            if (!rented.Remove(array, out var rental)) return;
            if (rental.Renter is { } who && renters.TryGetValue(who, out var counts)) renters[who] = counts with { Live = counts.Live - 1 };
        }
        if (!reuse) return;
        Zero(array);
        lock (gate)
        {
            if (!free.TryGetValue(array.Length, out var stack)) free[array.Length] = stack = new();
            stack.Push(array);
        }
    }

    /// <summary>Zeroes a returned array. On Linux its whole pages go back to the system (madvise DONTNEED: the
    /// array keeps its address, the pages read as zero and cost nothing until written) and only the partial pages
    /// at its edges are cleared; anywhere else, or if that fails, the whole array is cleared.</summary>
    private static void Zero(byte[] array)
    {
        if (OperatingSystem.IsLinux())
        {
            var handle = GCHandle.Alloc(array, GCHandleType.Pinned);
            try
            {
                nint start = handle.AddrOfPinnedObject(), page = Environment.SystemPageSize;
                nint first = (start + page - 1) & ~(page - 1), end = (start + array.Length) & ~(page - 1);
                if (end > first && Madvise(first, (nuint)(end - first), MadviseDontNeed) == 0)
                {
                    array.AsSpan(0, (int)(first - start)).Clear();
                    array.AsSpan((int)(end - start)).Clear();
                    return;
                }
            }
            finally { handle.Free(); }
        }
        Array.Clear(array);
    }

    private const int MadviseDontNeed = 4;

    [DllImport("libc", EntryPoint = "madvise", SetLastError = true)]
    private static extern int Madvise(nint address, nuint length, int advice);

    /// <summary>Ends <paramref name="renter"/>: disposes the owner of every array it still holds, which hands the array
    /// back (a test that never disposed its machine, say), and reports how many it held at the end and at most at once.</summary>
    public static (int Left, int Peak) Reclaim(object renter)
    {
        ArgumentNullException.ThrowIfNull(renter);
        List<IDisposable> owners;
        (int Live, int Peak) counts;
        lock (gate)
        {
            renters.Remove(renter, out counts);
            owners = [.. rented.Values.Where(r => Equals(r.Renter, renter) && r.Owner is not null).Select(r => r.Owner!)];
        }
        foreach (var owner in owners) owner.Dispose();
        return counts;
    }
}
