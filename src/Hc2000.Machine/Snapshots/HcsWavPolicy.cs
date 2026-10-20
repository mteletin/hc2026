using Hc2000.Machine.Tape;

namespace Hc2000.Machine.Snapshots;

/// <summary>Trusted caller opt-in, never serialized. File size is NOT a process-RSS claim.
/// Sixteen wire-size reservations conservatively cover retained input/cassette, source/capture/immutable
/// copies, codec-owned clones, growing MemoryStream and returned snapshot, including schema staging.</summary>
public sealed record HcsWavPolicy(int MaxInputBytes = 256 * 1024 * 1024, long TransientBytes = 4L * 1024 * 1024 * 1024)
{
    public static HcsWavPolicy Default { get; } = new();
    internal TapeLimits TapeLimits => new() { InputBytes = Math.Min(MaxInputBytes, TapeInputPolicy.WavInputBytes) };
    internal void Reserve(long wireBytes)
    {
        if (MaxInputBytes <= 0 || MaxInputBytes > TapeInputPolicy.WavInputBytes || TransientBytes <= 0
            || wireBytes < 0 || wireBytes > TransientBytes / 16)
            throw new HcsException(HcsError.LimitExceeded, "WAV transient-copy reservation");
    }
}
