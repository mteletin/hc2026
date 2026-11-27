using System.Diagnostics;
using Hc2000.Machine;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;

namespace Hc2000.App.Runtime;

/// <summary>
/// The boot check (hc2000-speed-140-turbo-280; the owner, 2026-11-25: "maybe with some little bench we do when we
/// boot the emulator and show the recommended speed? ... we should always aim to run as fast as possible given what the
/// host allows"). Once per app start, on a background thread, it runs a throwaway extended machine with a busy main
/// (block copies through memory) at 280, 140, 28, 14 and 7 MHz, fastest first, for a fraction of a second each, and
/// stops at the first speed this host carries in real time. Nothing it measures changes a machine by itself: the app
/// shows the recommendation and the user applies it.
/// </summary>
internal static class HostSpeedCheck
{
    /// <summary>The speeds the check offers, fastest first.</summary>
    internal static readonly MainCpuSpeed[] Candidates =
        [MainCpuSpeed.MHz280, MainCpuSpeed.MHz140, MainCpuSpeed.MHz28, MainCpuSpeed.MHz14, MainCpuSpeed.MHz7];

    /// <summary>A speed counts as carried when the host runs it at least this much faster than real time (the margin
    /// leaves room for the video, the sound and the rest of the desktop).</summary>
    internal const double RealTimeMargin = 1.1;

    /// <summary>The host time one speed is measured for, after its warm-up frames.</summary>
    internal static readonly TimeSpan PerSpeed = TimeSpan.FromMilliseconds(250);

    internal sealed record Measurement(MainCpuSpeed Speed, double RealTime);

    internal sealed record Result(MainCpuSpeed Recommended, IReadOnlyList<Measurement> Measured)
    {
        /// <summary>One line for the status bar's tooltip: what was measured.</summary>
        public string Describe() => string.Join(", ", Measured.Select(m =>
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{m.Speed.Setting()} x{m.RealTime:0.00}")));
    }

    /// <summary>The highest candidate carried in real time; when none is, the one that does the most work per host
    /// second (the machine then runs as fast as the host allows at it).</summary>
    internal static MainCpuSpeed Recommend(IReadOnlyList<Measurement> measured)
    {
        foreach (var candidate in Candidates)
            if (measured.FirstOrDefault(m => m.Speed == candidate) is { } m && m.RealTime >= RealTimeMargin) return candidate;
        return measured.Count == 0 ? MainCpuSpeed.Faithful
            : measured.MaxBy(m => m.Speed.TStatesPerSecond() * m.RealTime)!.Speed;
    }

    /// <summary>Measures, fastest first, until a speed is carried; call it off the UI thread. One throwaway machine serves
    /// every speed (switched live), and its 1 GiB RAM goes to the GC afterwards, never to the pool: the check costs one
    /// array for a moment, not one per speed, and leaves none behind beside the user's machine.</summary>
    internal static Result Run(CancellationToken cancellation = default)
    {
        var measured = new List<Measurement>();
        using var bench = new Bench(Candidates[0]);
        foreach (var speed in Candidates)
        {
            cancellation.ThrowIfCancellationRequested();
            var measurement = bench.Measure(speed, PerSpeed);
            measured.Add(measurement);
            if (measurement.RealTime >= RealTimeMargin) break;
        }
        return new(Recommend(measured), measured);
    }

    /// <summary>How fast this host runs the extended machine at <paramref name="speed"/>: machine time over host time.</summary>
    internal static Measurement Measure(MainCpuSpeed speed, TimeSpan budget)
    {
        using var bench = new Bench(speed);
        return bench.Measure(speed, budget);
    }

    private sealed class Bench : IDisposable
    {
        private readonly Hc2000.Machine.Machine machine;
        private HcMemory? memory;
        private MainCpuSpeed current;

        public Bench(MainCpuSpeed speed)
        {
            var profile = MachineProfile.Hc2000V2PagedExpansionV1 with { MainCpuTarget = speed };
            var prepared = new PreparedBasicMachine((profile, Workload(), new byte[0x4000], new byte[0x4000]));
            machine = BasicMachineComposition.CreateColdNewMachine(prepared, new(), new(new()), new(new()), _ => { }, _ => { },
                observeCpu: (_, ram) => memory = ram);
            current = speed;
        }

        public Measurement Measure(MainCpuSpeed speed, TimeSpan budget)
        {
            // A live switch waits for a safe boundary, as the App's does.
            for (var attempt = 0; current != speed; attempt++)
            {
                try { machine.SetMainCpuTarget(speed); current = speed; }
                catch (InvalidOperationException) when (attempt < 50) { RunFrame(machine); }
            }
            for (var frame = 0; frame < 2; frame++) RunFrame(machine); // the first frames pay for the JIT and the switch
            var frames = 0;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < budget && frames < 250) { RunFrame(machine); frames++; }
            watch.Stop();
            var machineSeconds = frames * (double)FrameTiming.FrameTicks / FrameTiming.MasterHz;
            return new(speed, machineSeconds / watch.Elapsed.TotalSeconds);
        }

        public void Dispose()
        {
            memory?.Retire();   // to the GC, not back to the pool (no second GiB kept, none zeroed on Windows)
            machine.Dispose();
        }
    }

    private static void RunFrame(Hc2000.Machine.Machine machine)
    {
        if (!machine.TryRunFrame(static () => true, out var result)) throw new InvalidOperationException("The speed check's machine stopped.");
        machine.Recycle(result);
    }

    /// <summary>A busy main: copies a kilobyte back and forth through RAM, forever, with interrupts off.</summary>
    internal static byte[] Workload()
    {
        byte[] program =
        [
            0xF3,                   // DI
            0x31, 0x00, 0xFF,       // LD SP,FF00
            0x21, 0x00, 0x80,       // loop: LD HL,8000
            0x11, 0x00, 0x90,       // LD DE,9000
            0x01, 0x00, 0x04,       // LD BC,0400
            0xED, 0xB0,             // LDIR
            0x21, 0x00, 0x90,       // LD HL,9000
            0x11, 0x00, 0x80,       // LD DE,8000
            0x01, 0x00, 0x04,       // LD BC,0400
            0xED, 0xB0,             // LDIR
            0x18, 0xE8,             // JR loop (26 + 2 - 24 = 4)
        ];
        var rom = new byte[0x4000];
        program.CopyTo(rom, 0);
        return rom;
    }
}
