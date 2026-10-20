namespace Hc2000.App.Audio;

/// <summary>Sound-dump fallback (PO ruling board 634859): the route was requested but the audio
/// sink is not live, so the dump still writes the file - one second of silence at <see cref="Rate"/>,
/// with a RIFF <c>LIST/INFO/ICMT</c> comment chunk that says why it is silent. The file says so; the
/// machine's deck line is unchanged. PCM 8-bit mono, matching the capture's Pcm8 convention
/// (silence = 128). The comment is even-byte padded per RIFF.</summary>
public static class SoundDumpSilentFile
{
    public const int Rate = 44100;

    public static void Write(string path, string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var note = reason.Length > 256 ? reason[..256] : reason;
        var noteBytes = System.Text.Encoding.UTF8.GetBytes(note + '\0');
        var padding = noteBytes.Length % 2;
        const int samples = Rate; // one second
        const int fmtSize = 16;
        var listSize = 4 + 8 + noteBytes.Length + padding; // INFO + ICMT chunk
        var riffSize = 4 + 8 + fmtSize + 8 + listSize + 8 + samples;
        using var stream = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
        using var writer = new System.IO.BinaryWriter(stream);
        writer.Write(new byte[] { 0x52, 0x49, 0x46, 0x46 }); // RIFF
        writer.Write((uint)riffSize);
        writer.Write(new byte[] { 0x57, 0x41, 0x56, 0x45 }); // WAVE
        writer.Write(new byte[] { 0x66, 0x6D, 0x74, 0x20 }); // fmt
        writer.Write((uint)fmtSize);
        writer.Write((ushort)1);                             // PCM
        writer.Write((ushort)1);                             // mono
        writer.Write((uint)Rate);                            // sample rate
        writer.Write((uint)Rate);                            // byte rate (8-bit mono)
        writer.Write((ushort)1);                             // block align
        writer.Write((ushort)8);                             // bits per sample
        writer.Write(new byte[] { 0x4C, 0x49, 0x53, 0x54 }); // LIST
        writer.Write((uint)listSize);
        writer.Write(new byte[] { 0x49, 0x4E, 0x46, 0x4F }); // INFO
        writer.Write(new byte[] { 0x49, 0x43, 0x4D, 0x54 }); // ICMT
        writer.Write((uint)noteBytes.Length);
        writer.Write(noteBytes);
        if (padding != 0) writer.Write((byte)0);
        writer.Write(new byte[] { 0x64, 0x61, 0x74, 0x61 }); // data
        writer.Write((uint)samples);
        var silence = new byte[samples];
        Array.Fill(silence, (byte)128); // PCM8 midpoint, the capture's silence
        writer.Write(silence);
    }
}
