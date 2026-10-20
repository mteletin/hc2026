using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hc2000.Disasm;
using Hc2000.Machine.Memory;

namespace Hc2000.App.Debugging;

/// <summary>Emulation-thread-owned optional collector. Receives actual events, never produces them.
/// Bound to one immutable reference image/physical slice and opaque resolved-profile fingerprint.</summary>
public sealed class ExecutionRecorder
{
    private sealed record Configuration(string Id, MapWindow[] Windows, int[] Index, byte[] Flags);
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly byte[] image;
    private readonly MemoryStore store;
    private readonly int physicalOffset;
    private readonly string profileId, fingerprint;
    private readonly Dictionary<PagingLatches, Configuration?> configs = [];
    private HcMemory? observedMemory;
    public bool IsRecording { get; private set; }

    public ExecutionRecorder(byte[] image, MemoryStore store, int physicalOffset, string profileId, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(image));
        if (!Enum.IsDefined(store)) throw new ArgumentOutOfRangeException(nameof(store));
        if (physicalOffset < 0 || physicalOffset > int.MaxValue - image.Length) throw new ArgumentOutOfRangeException(nameof(physicalOffset));
        if (!Regex.IsMatch(profileId, @"\A[A-Za-z0-9._-]{1,64}\z")) throw new ArgumentException("Invalid profile ID.", nameof(profileId));
        if (!Regex.IsMatch(fingerprint, @"\A[0-9a-fA-F]{64}\z")) throw new ArgumentException("Invalid profile fingerprint.", nameof(fingerprint));
        this.image = (byte[])image.Clone(); this.store = store; this.physicalOffset = physicalOffset;
        this.profileId = profileId; this.fingerprint = fingerprint.ToLowerInvariant();
    }

    private void VerifyOwner()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Recorder belongs to the owning emulation thread.");
    }
    public void Start() { VerifyOwner(); IsRecording = true; }
    public void Stop() { VerifyOwner(); IsRecording = false; }
    public void Clear() { Stop(); configs.Clear(); }

    /// <summary>Caller supplies the effective post-access mapping, including an M1 shadow trap.
    /// M1 replaces Read, not both; returned bytes are required for reads/fetches. Targets are explicit only.</summary>
    public void Observe(HcMemory memory, ushort address, ExecutionFlags flags, byte? returnedByte = null)
    {
        VerifyOwner();
        if (!IsRecording) return;
        ArgumentNullException.ThrowIfNull(memory);
        if (observedMemory is not null && !ReferenceEquals(observedMemory, memory))
            throw new InvalidOperationException("A recorder belongs to one machine/profile instance.");
        observedMemory = memory;
        if (((byte)flags & 0xc0) != 0 || (flags & ExecutionFlags.Mismatch) != 0 ||
            ((flags & ExecutionFlags.Start) != 0 && (flags & ExecutionFlags.M1) == 0) ||
            ((flags & (ExecutionFlags.Read | ExecutionFlags.M1)) == (ExecutionFlags.Read | ExecutionFlags.M1)))
            throw new ArgumentException("Invalid producer flags.", nameof(flags));
        var read = (flags & (ExecutionFlags.Read | ExecutionFlags.M1)) != 0;
        if (read && returnedByte is null) throw new ArgumentException("Read/fetch must supply its actual byte.", nameof(returnedByte));
        if (!configs.TryGetValue(memory.Latches, out var config))
        {
            config = Build(memory);
            if (config is not null && configs.Values.Count(c => c is not null) == 64)
                throw new InvalidOperationException("Execution map exceeds 64 configurations.");
            configs.Add(memory.Latches, config);
        }
        if (config is null || config.Index[address] < 0) return;
        if (read && returnedByte != image[config.Index[address]]) flags |= ExecutionFlags.Mismatch;
        config.Flags[address] |= (byte)flags;
    }

    private Configuration? Build(HcMemory memory)
    {
        var index = Enumerable.Repeat(-1, 65536).ToArray();
        for (var a = 0; a < 65536; a++)
        {
            var location = memory.Resolve((ushort)a);
            var offset = location.Offset - physicalOffset;
            if (location.Store == store && offset >= 0 && offset < image.Length) index[a] = offset;
        }
        var windows = new List<MapWindow>();
        for (var a = 0; a < 65536; a++)
        {
            if (index[a] < 0) continue;
            var start = a;
            while (a + 1 < 65536 && index[a + 1] == index[a] + 1) a++;
            windows.Add(new(start, index[start], a - start + 1));
        }
        if (windows.Count == 0) return null;
        var latches = memory.Latches;
        return new($"7e-{latches.Port7E:x2}-cpm-{(latches.Cpm ? 1 : 0)}-shadow-{(latches.Shadow ? 1 : 0)}", windows.ToArray(), index, new byte[65536]);
    }

    private Configuration[] Ordered()
    {
        VerifyOwner();
        var rows = configs.Values.Where(c => c is not null).Cast<Configuration>().OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
        if (rows.Length == 0) throw new InvalidOperationException("No visible image configuration has been observed.");
        return rows;
    }

    private byte[] Manifest(Configuration[] rows, bool marks)
    {
        var text = new StringBuilder("hcmap 1\n");
        text.Append($"image {image.Length:X8} {Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant()}\nprofile {profileId} {fingerprint}\n");
        foreach (var config in rows)
        {
            text.Append($"config {config.Id}\n");
            foreach (var window in config.Windows) text.Append($"window {window.CpuStart:X4} {window.ImageOffset:X8} {window.Length:X8}\n");
            if (marks)
                for (var a = 0; a < 65536; a++)
                {
                    var mask = config.Flags[a]; if (mask == 0) continue;
                    var start = a;
                    while (a + 1 < 65536 && config.Flags[a + 1] == mask) a++;
                    text.Append($"mark {start:X4}-{a:X4} {mask:X2}\n");
                }
            text.Append("end\n");
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    public byte[] ExportText() => Manifest(Ordered(), true);
    public byte[] ExportBinary()
    {
        var rows = Ordered(); var manifest = Manifest(rows, false);
        var bytes = new byte[12 + manifest.Length + rows.Length * 65536];
        "HCMAPB1\0"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), (uint)manifest.Length);
        manifest.CopyTo(bytes, 12);
        for (var i = 0; i < rows.Length; i++) rows[i].Flags.CopyTo(bytes, 12 + manifest.Length + i * 65536);
        return bytes;
    }
}
