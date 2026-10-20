using System.Runtime.InteropServices;
using Hc2000.App.Audio;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;

// A named, bounded manual output/ABI probe, not an automatic audible assertion.
// No UI, Machine, ROM or DB is required. Stage the pinned native artifact first.
Console.WriteLine($"host={Environment.MachineName}; OS={RuntimeInformation.OSDescription}; arch={RuntimeInformation.ProcessArchitecture}; UTC={DateTimeOffset.UtcNow:O}");
Console.WriteLine($"requested-driver={Environment.GetEnvironmentVariable("SDL_AUDIODRIVER") ?? "platform default"}; ABI-size={SdlQueuedAudioSink.AudioSpecSize}; callback-offset={SdlQueuedAudioSink.CallbackOffset}");
if (args.Contains("--fallback-only"))
{
    var diagnostics = new List<string>();
    using var output = BeeperAudioOutput.Open(FrameTiming.MasterHz, true, diagnostics.Add);
    var first = output.ProcessFrame(0, FrameTiming.FrameTicks, [], 100, default);
    var second = output.ProcessFrame(FrameTiming.FrameTicks, 2 * FrameTiming.FrameTicks, [], 100, default);
    foreach (var message in diagnostics) Console.WriteLine(message);
    Console.WriteLine($"fallback-first={!first}; fallback-second={!second}; diagnostic-count={diagnostics.Count}");
    Environment.ExitCode = !first && !second && diagnostics.Count == 1 ? 0 : 1;
    return;
}
if (args.Contains("--capture"))
{
    RunVirtualSinkCaptureCheck(args.Length > 2 && !args[2].StartsWith("--") ? args[2]
        : Path.Combine(Path.GetTempPath(), $"hc2000-audio-capture-{DateTime.UtcNow:yyyyMMddHHmmssfff}.wav"));
    return;
}
try
{
    using var sink = SdlQueuedAudioSink.Open();
    Console.WriteLine($"SDL2={SdlQueuedAudioSink.BackendVersion}; binary={sink.BinaryPath}; SHA256={sink.BinarySha256}");
    Console.WriteLine($"driver={sink.Driver}; enumerated-device={sink.DeviceName}; obtained={sink.SampleRate}Hz/0x{sink.Format:x4}/{sink.Channels}ch; block={sink.BlockSamples}");
    using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var wait = new AudioWait();
    var tone = Enumerable.Range(0, sink.SampleRate / 2).Select(i => (float)(.2 * Math.Sin(2 * Math.PI * 1000 * i / sink.SampleRate))).ToArray();
    sink.Pause(false);
    var max = 0;
    for (var offset = 0; offset < tone.Length;)
    {
        var count = Math.Min(sink.SampleRate / 50, tone.Length - offset);
        DrainBelow(sink.SampleRate * 40 / 1000 - count);
        sink.Queue(tone.AsSpan(offset, count));
        max = Math.Max(max, sink.QueuedSamples);
        offset += count;
    }
    DrainBelow(0);
    sink.Pause(true); sink.Clear();
    Console.WriteLine($"known-tone=1000Hz/500ms; software-queue-high={max}; drained=true");

    using (var output = new BeeperAudioOutput(FrameTiming.MasterHz, sink, wait, Console.WriteLine))
    {
        var edges = Enumerable.Range(0, 1000).Select(i => new BeeperEdge(i * FrameTiming.MasterHz / 2000,
            i % 2 == 0, (i / 4) % 2 != 0)).ToArray();
        var index = 0;
        long start = 0;
        for (var frame = 1; frame <= 25; frame++)
        {
            var end = frame * FrameTiming.FrameTicks;
            var begin = index;
            while (index < edges.Length && edges[index].MasterTick <= end) index++;
            if (!output.ProcessFrame(start, end, edges.AsSpan(begin, index - begin), 100, watchdog.Token))
                throw new IOException("Native queue pacing unexpectedly failed.");
            start = end;
        }
        DrainBelow(0);
        Console.WriteLine($"beeper-frames=25; software-queue-high={output.QueueHighWaterSamples}; software-starvations={output.SoftwareStarvations}; drained=true");
        output.Pause(); output.Reset();
        if (sink.QueuedSamples != 0) throw new IOException("Clear/reset left software PCM.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { output.ProcessFrame(0, FrameTiming.FrameTicks, [], 100, cancelled.Token); throw new IOException("Cancellation ignored."); }
        catch (OperationCanceledException) { Console.WriteLine("pause/clear/reset/cancellation=true"); }
    }
    using var reopened = SdlQueuedAudioSink.Open();
    Console.WriteLine($"close/reopen=true; driver={reopened.Driver}");
    Console.WriteLine("Native API probe PASS. Audible result requires a human listener; dummy driver is NOT audible evidence. Clear cannot retract hardware-held samples.");

    void DrainBelow(int threshold)
    {
        var previous = sink.QueuedSamples;
        var progress = wait.Elapsed;
        while (previous > threshold)
        {
            if (wait.Elapsed - progress >= TimeSpan.FromMilliseconds(500)) throw new IOException("500ms no-drain watchdog");
            wait.Wait(TimeSpan.FromMilliseconds(2), watchdog.Token);
            var current = sink.QueuedSamples;
            if (current < previous) progress = wait.Elapsed;
            previous = current;
        }
    }
}
catch (Exception ex) when (ex is IOException or DllNotFoundException or BadImageFormatException or InvalidOperationException)
{
    Console.Error.WriteLine($"Native API/output unavailable: {ex.Message}");
    Environment.ExitCode = 1;
}

// The named output check ruled in for wi-116b (2026-10-04): a virtual SDL2 sink at the lowest
// supported rate, with its stream persisted as Wave PCM 8-bit in a bounded one-hour rolling
// buffer on disk. Dummy-queue success and captured-output success are reported separately.
static void RunVirtualSinkCaptureCheck(string capturePath)
{
    using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var wait = new AudioWait();
    using var sink = SdlQueuedAudioSink.Open(44100); // lowest supported rate per the ruling
    var virtualSink = sink.Driver == "dummy";
    Console.WriteLine($"backend=SDL2 {SdlQueuedAudioSink.BackendVersion}; binary={sink.BinaryPath}; SHA256={sink.BinarySha256}");
    Console.WriteLine($"virtual-sink={virtualSink}; driver={sink.Driver}; enumerated-device={sink.DeviceName}; obtained={sink.SampleRate}Hz/0x{sink.Format:x4}/{sink.Channels}ch");
    using var capture = new CapturingAudioSink(sink, capturePath); // on-disk window: one hour
    var max = 0;

    // 1) Named known tone (1000 Hz, 500 ms) through the virtual sink into the on-disk capture.
    var tone = Enumerable.Range(0, sink.SampleRate / 2).Select(i => (float)(.2 * Math.Sin(2 * Math.PI * 1000 * i / sink.SampleRate))).ToArray();
    capture.Pause(false);
    for (var offset = 0; offset < tone.Length;)
    {
        var count = Math.Min(capture.SampleRate / 50, tone.Length - offset);
        DrainBelow(capture.SampleRate * 40 / 1000 - count);
        capture.Queue(tone.AsSpan(offset, count));
        max = Math.Max(max, capture.QueuedSamples);
        offset += count;
    }
    DrainBelow(0);
    Console.WriteLine($"known-tone=1000Hz/500ms; software-queue-high={max}; drained=true");

    // 2) Representative beeper output through the master-time resampler path (25 frames, 100% speed).
    using var output = new BeeperAudioOutput(FrameTiming.MasterHz, capture, wait, Console.WriteLine);
    var edges = Enumerable.Range(0, 1000).Select(i => new BeeperEdge(i * FrameTiming.MasterHz / 2000, i % 2 == 0, (i / 4) % 2 != 0)).ToArray();
    var index = 0;
    long start = 0;
    for (var frame = 1; frame <= 25; frame++)
    {
        var end = frame * FrameTiming.FrameTicks;
        var begin = index;
        while (index < edges.Length && edges[index].MasterTick <= end) index++;
        if (!output.ProcessFrame(start, end, edges.AsSpan(begin, index - begin), 100, watchdog.Token))
            throw new IOException("Audio pacing unexpectedly failed on the capture sink.");
        start = end;
    }
    DrainBelow(0);
    Console.WriteLine($"beeper-frames=25; software-queue-high={output.QueueHighWaterSamples}; software-starvations={output.SoftwareStarvations}; drained=true");
    output.Pause();
    output.Dispose();
    capture.Dispose(); // finalizes the RIFF header over the rolling window

    // 3) The named output check runs against the persisted on-disk artifact.
    var expectedBeeper = (long)((Int128)(25L * FrameTiming.FrameTicks) * sink.SampleRate / FrameTiming.MasterHz);
    var data = File.ReadAllBytes(capturePath);
    var riff = data.AsSpan(0, 4).ToArray();
    var wave = data.AsSpan(8, 4).ToArray();
    var fmt = data.AsSpan(12, 4).ToArray();
    var pcm = BitConverter.ToInt16(data.AsSpan(20, 2));
    var channels = BitConverter.ToInt16(data.AsSpan(22, 2));
    var rate = BitConverter.ToInt32(data.AsSpan(24, 4));
    var byteRate = BitConverter.ToInt32(data.AsSpan(28, 4));
    var bits = BitConverter.ToInt16(data.AsSpan(34, 2));
    var dataLength = BitConverter.ToInt32(data.AsSpan(40, 4));
    if (riff.Length != 4 || wave.Length != 4 || fmt.Length != 4
        || !riff.SequenceEqual(System.Text.Encoding.ASCII.GetBytes("RIFF"))
        || !wave.SequenceEqual(System.Text.Encoding.ASCII.GetBytes("WAVE"))
        || !fmt.SequenceEqual(System.Text.Encoding.ASCII.GetBytes("fmt "))
        || pcm != 1 || channels != 1 || rate != sink.SampleRate || byteRate != sink.SampleRate || bits != 8
        || dataLength != tone.Length + (int)expectedBeeper)
        throw new IOException($"Captured RIFF header or data length invalid: riff/wave/fmt tags, PCM{pcm}, {channels}ch, {rate}Hz, {bits}bit, data={dataLength}, expected tone {tone.Length} + beeper {expectedBeeper}.");
    var toneExact = 0;
    for (var i = 0; i < tone.Length; i++) if (data[44 + i] == CapturingAudioSink.Pcm8(tone[i])) toneExact++;
    if (toneExact != tone.Length) throw new IOException($"Known tone bytes exact in {toneExact} of {tone.Length}.");
    var nonSilent = 0;
    for (var i = tone.Length; i < tone.Length + expectedBeeper; i++) if (data[44 + i] != 128) nonSilent++;
    if (nonSilent == 0) throw new IOException("Beeper region is silent in the capture.");
    var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

    // 4) Dummy-queue success is reported separately from the captured-output success.
    Console.WriteLine($"dummy-queue=PASS; driver={sink.Driver}; virtual-sink={virtualSink}; known-tone=1000Hz/500ms drained; beeper=25 frames drained; software-queue-high={max}");
    Console.WriteLine($"captured-output=PASS; path={capturePath}; size={data.Length}; rate={rate}Hz; format=PCM8/mono; on-disk-window={capture.CapacitySamples} samples (1h); retained={Math.Min(capture.CapturedSamples, capture.CapacitySamples)}; tone-bytes-exact={toneExact}/{tone.Length}; beeper-non-silent={nonSilent}/{expectedBeeper}");
    Console.WriteLine($"artifact-sha256={sha}");
    Console.WriteLine("Virtual-sink capture PASS. The dummy driver is a virtual device: captured output is acceptance evidence, not audible output.");

    void DrainBelow(int threshold)
    {
        var previous = capture.QueuedSamples;
        var progress = wait.Elapsed;
        while (previous > threshold)
        {
            if (wait.Elapsed - progress >= TimeSpan.FromMilliseconds(500)) throw new IOException("500ms no-drain watchdog");
            wait.Wait(TimeSpan.FromMilliseconds(2), watchdog.Token);
            var current = capture.QueuedSamples;
            if (current < previous) progress = wait.Elapsed;
            previous = current;
        }
    }
}
