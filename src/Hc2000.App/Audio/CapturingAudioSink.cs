namespace Hc2000.App.Audio;

/// <summary>
/// Virtual-sink capture decorator (owner gate ruling 2026-10-04, wi-116b): forwards every
/// sample to the inner sink - on the SDL2 <c>dummy</c> driver that is the virtual device,
/// and its queue success is reported distinctly - and tees the same stream as Wave PCM
/// 8-bit into a capacity-bounded rolling buffer on disk. The file is pre-allocated at the
/// capacity and the RIFF header is finalized on dispose, so the on-disk artifact always
/// holds exactly the last <see cref="CapacitySamples"/> samples (or everything, when less)
/// the inner sink received. No real hardware is involved or required.
/// </summary>
public sealed class CapturingAudioSink : IQueuedAudioSink
{
    private const int HeaderSize = 44;
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly IQueuedAudioSink inner;
    private readonly FileStream file;
    private readonly byte[] pcm;
    private int cursor;
    private long total;
    private bool disposed;

    public int SampleRate => inner.SampleRate;
    public int QueuedSamples => inner.QueuedSamples;
    public string Path { get; }
    public long CapacitySamples { get; }
    public long CapturedSamples => total;
    public bool Wrapped => total > CapacitySamples;

    public CapturingAudioSink(IQueuedAudioSink inner, string path, long? capacitySamples = null)
    {
        var capacity = capacitySamples ?? inner.SampleRate * 3600L; // the ruled on-disk window: one hour
        if (capacity <= 0 || capacity > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(capacitySamples));
        this.inner = inner;
        Path = System.IO.Path.GetFullPath(path);
        CapacitySamples = capacity;
        pcm = new byte[checked((int)capacity)];
        file = new FileStream(Path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        file.SetLength(HeaderSize + pcm.Length);
        WriteHeader(0);
    }

    public void Queue(ReadOnlySpan<float> samples)
    {
        RequireOpen();
        if (samples.Length > pcm.Length)
            throw new ArgumentException("A batch may not exceed the capture capacity.", nameof(samples));
        inner.Queue(samples); // virtual-sink receipt; its success stays distinct from the capture
        if (samples.Length == 0) return;
        var start = cursor;
        for (var i = 0; i < samples.Length; i++)
        {
            pcm[cursor] = Pcm8(samples[i]);
            cursor = (cursor + 1) % pcm.Length;
        }
        var end = cursor;
        if (samples.Length == pcm.Length) // full lap: every position was rewritten
        {
            file.Seek(HeaderSize, SeekOrigin.Begin);
            file.Write(pcm, 0, pcm.Length);
        }
        else if (end > start)
        {
            file.Seek(HeaderSize + start, SeekOrigin.Begin);
            file.Write(pcm, start, end - start);
        }
        else
        {
            file.Seek(HeaderSize + start, SeekOrigin.Begin);
            file.Write(pcm, start, pcm.Length - start);
            if (end > 0)
            {
                file.Seek(HeaderSize, SeekOrigin.Begin);
                file.Write(pcm, 0, end);
            }
        }
        total += samples.Length;
    }

    public void Pause(bool paused) { RequireOpen(); inner.Pause(paused); }
    public void Clear() { RequireOpen(); inner.Clear(); } // capture is a record of what the sink received; Clear does not truncate it

    public static byte Pcm8(float sample)
    {
        var value = float.IsFinite(sample) ? Math.Clamp(sample, -1f, 1f) : 0f;
        return unchecked((byte)(128 + (int)(value * 127f)));
    }

    public void Dispose()
    {
        RequireOwner();
        if (disposed) return;
        disposed = true;
        if (total > pcm.Length && cursor != 0) FinalizeWrapped();
        WriteHeader(Math.Min(total, pcm.Length));
        file.Flush(true);
        file.Dispose();
        inner.Dispose();
    }

    // After a wrap the oldest samples are [cursor..capacity) and the newest [0..cursor):
    // rotate the ring (newest at the tail) and write the whole data region once, so the
    // finalized file reads oldest -> newest. One transient copy of the capacity in memory.
    private void FinalizeWrapped()
    {
        var n = pcm.Length;
        var linear = new byte[n];
        for (var i = 0; i < n; i++)
            linear[i] = pcm[cursor + i < n ? cursor + i : cursor + i - n];
        Buffer.BlockCopy(linear, 0, pcm, 0, n);
        file.Seek(HeaderSize, SeekOrigin.Begin);
        file.Write(pcm, 0, n);
    }

    private void WriteHeader(long dataLength)
    {
        var header = new byte[HeaderSize];
        WriteAscii(header, 0, "RIFF");
        WriteInt32(header, 4, 36 + (int)dataLength);
        WriteAscii(header, 8, "WAVE");
        WriteAscii(header, 12, "fmt ");
        WriteInt32(header, 16, 16);
        WriteInt16(header, 20, 1); // PCM
        WriteInt16(header, 22, 1); // mono
        WriteInt32(header, 24, SampleRate);
        WriteInt32(header, 28, SampleRate); // 8 bits: byte rate == sample rate
        WriteInt16(header, 32, 1);
        WriteInt16(header, 34, 8);
        WriteAscii(header, 36, "data");
        WriteInt32(header, 40, (int)dataLength);
        file.Seek(0, SeekOrigin.Begin);
        file.Write(header, 0, HeaderSize);
    }

    private static void WriteAscii(byte[] buffer, int offset, string text)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
    }
    private static void WriteInt16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }
    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private void RequireOwner()
    {
        if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("Audio belongs to the emulation worker.");
    }
    private void RequireOpen() { RequireOwner(); ObjectDisposedException.ThrowIf(disposed, this); }
}
