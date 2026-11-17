using System.Buffers.Binary;
using System.Collections.Immutable;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Tape.Codecs;

namespace Hc2000.Machine.Tape;

/// <summary>One file in an HCTA archive. <see cref="Data"/> is the exact byte length;
/// the directory fields are what extract writes back.</summary>
public sealed record CpmArchiveFile(
    byte User,
    string Name,
    byte Extent,
    byte ExtentHigh,
    byte RecordCount,
    uint AllocatedBytes,
    bool ReadOnly,
    bool System,
    byte[]? DateStamp,
    ushort? LoadAddress,
    byte[] Data);

/// <summary>A parsed HCTA archive. Warnings name files that were skipped, never silent.</summary>
public sealed record CpmArchive(
    string Name,
    string Created,
    ImmutableArray<CpmArchiveFile> Files,
    ImmutableArray<string> Warnings);

/// <summary>
/// HCTA v1, the CP/M tape archive. Layout is the KB doc archive-layout-20261025 on
/// hc2000-cpm22-proving. The on-wire TAP blocks are self-describing; the TZX framing
/// is what the host and the deck see.
/// </summary>
public static class CpmTapeArchive
{
    public const string Marker = "HCTA v1";
    public const string Creator = "hc2000-cpm22-proving ARCH";
    public const int MetadataLength = 34;
    public const int DataChunkBytes = 16384;
    private const int HeaderBytes = 19;
    private const int MetadataBytes = 36;
    private const ushort BlockPause = 500;
    private const ushort EndPause = 1000;

    public static byte[] Build(CpmArchive archive) => Build(archive.Files, archive.Name, archive.Created);

    public static byte[] Build(IReadOnlyList<CpmArchiveFile> files, string archiveName, string created)
    {
        ArgumentNullException.ThrowIfNull(files);
        archiveName = CleanText(archiveName, 64, "archive name");
        created = CleanText(created, 32, "created");
        foreach (var file in files) Validate(file);
        using var output = new MemoryStream();
        output.Write("ZXTape!\x1a"u8);
        output.WriteByte(1);
        output.WriteByte(20);
        output.WriteByte(0x33);
        output.WriteByte(1);
        output.WriteByte(0xFF);
        output.WriteByte(0xFF);
        output.WriteByte(0xFF);
        ArchiveInfo(output, archiveName, created);
        Text(output, 0x30, Marker + " CP/M archive HC2000");
        foreach (var file in files)
        {
            var display = DisplayName(file.Name);
            Text(output, 0x21, display.TrimEnd());
            Text(output, 0x30, $"user {file.User} {file.Name}");
            StandardBlock(output, HeaderBlock(display, (uint)file.Data.Length));
            StandardBlock(output, MetadataBlock(file));
            foreach (var chunk in DataBlocks(file.Data)) StandardBlock(output, chunk);
            Pause(output, BlockPause);
            output.WriteByte(0x22);
        }
        Pause(output, EndPause);
        return output.ToArray();
    }

    public static TapeResult<CpmArchive> Read(ReadOnlySpan<byte> bytes)
    {
        if (!bytes.StartsWith("ZXTape!\x1a"u8)) return ReadTap(bytes);
        var parsed = TzxDocument.Parse(bytes, new TapeLimits());
        if (!parsed.Success) return TapeResult<CpmArchive>.Fail(parsed.Failure!.Code, parsed.Failure.Detail);
        var hasSignalBlocks = parsed.Value.Blocks.Any(block => block.Id is >= 0x11 and <= 0x19);
        if (!parsed.Value.Blocks.Any(block => block.Id == 0x10) && hasSignalBlocks)
            return ReadSignalTzx(bytes);
        var name = "";
        var created = "";
        var files = ImmutableArray.CreateBuilder<CpmArchiveFile>();
        var warnings = ImmutableArray.CreateBuilder<string>();
        if (hasSignalBlocks) warnings.Add("TZX signal blocks were not read alongside standard data blocks.");
        var group = new List<TzxSourceBlock>();
        var ungrouped = new List<byte[]>();
        var inGroup = false;
        var groupName = "";
        void FlushUngrouped()
        {
            if (ungrouped.Count == 0) return;
            ReadFrames("TZX", ungrouped, files, warnings);
            ungrouped.Clear();
        }
        foreach (var block in parsed.Value.Blocks)
        {
            if (block.Id == 0x32)
            {
                ReadArchiveInfo(block.Body.AsSpan(), ref name, ref created);
                continue;
            }
            if (block.Id == 0x21)
            {
                FlushUngrouped();
                if (inGroup) warnings.Add($"group '{groupName}' was not closed");
                inGroup = true;
                groupName = TextOf(block.Body.AsSpan());
                group.Clear();
                continue;
            }
            if (block.Id == 0x22)
            {
                if (inGroup) ReadGroup(groupName, group, files, warnings);
                inGroup = false;
                group.Clear();
                continue;
            }
            if (inGroup) group.Add(block);
            else if (block.Id == 0x10 && TryTapData(block, out var frame))
            {
                if (frame.Length == HeaderBytes && frame[0] == 0) FlushUngrouped();
                ungrouped.Add(frame);
            }
        }
        if (inGroup) warnings.Add($"group '{groupName}' was not closed");
        FlushUngrouped();
        return TapeResult<CpmArchive>.Ok(new(name, created, files.ToImmutable(), warnings.ToImmutable()));
    }

    private static TapeResult<CpmArchive> ReadSignalTzx(ReadOnlySpan<byte> bytes)
    {
        var limits = new TapeLimits();
        var signal = new TzxTapeCodec().Read(bytes, new(TapeFormat.Tzx, TapeSide.A, TapeCapacity.C90) { Limits = limits });
        if (!signal.Success) return TapeResult<CpmArchive>.Fail(signal.Failure!.Code, signal.Failure.Detail);
        var index = new TapeSignalIndexFactory().Build(signal.Value.Image, limits);
        if (!index.Success) return TapeResult<CpmArchive>.Fail(index.Failure!.Code, index.Failure.Detail);
        var recovered = TapCodec.RecoverFrames(index.Value, limits);
        if (!recovered.Success) return TapeResult<CpmArchive>.Fail(recovered.Failure!.Code, recovered.Failure.Detail);
        if (recovered.Value.Count == 0)
            return TapeResult<CpmArchive>.Fail(TapeError.InvalidFormat, "No standard blocks recovered from TZX signal.");
        var files = ImmutableArray.CreateBuilder<CpmArchiveFile>();
        var warnings = ImmutableArray.CreateBuilder<string>();
        warnings.Add("TZX signal was scanned for standard blocks; unrecognized signal may have been skipped.");
        var frames = new List<byte[]>();
        foreach (var frame in recovered.Value)
        {
            if (frame.Length == HeaderBytes && frame[0] == 0 && frames.Count > 0)
            {
                ReadFrames("TZX signal", frames, files, warnings);
                frames.Clear();
            }
            frames.Add(frame);
        }
        ReadFrames("TZX signal", frames, files, warnings);
        return TapeResult<CpmArchive>.Ok(new("", "", files.ToImmutable(), warnings.ToImmutable()));
    }

    private static TapeResult<CpmArchive> ReadTap(ReadOnlySpan<byte> bytes)
    {
        var limits = new TapeLimits();
        if (bytes.Length > limits.InputBytes)
            return TapeResult<CpmArchive>.Fail(TapeError.BoundsExceeded, "TAP input limit.");
        var files = ImmutableArray.CreateBuilder<CpmArchiveFile>();
        var warnings = ImmutableArray.CreateBuilder<string>();
        var frames = new List<byte[]>();
        var offset = 0;
        var blocks = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 2)
                return TapeResult<CpmArchive>.Fail(TapeError.InvalidFormat, "Truncated TAP length.");
            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
            offset += 2;
            if (length < 2 || bytes.Length - offset < length)
                return TapeResult<CpmArchive>.Fail(TapeError.InvalidFormat, "Truncated or invalid TAP block.");
            if (++blocks > limits.SourceBlocks)
                return TapeResult<CpmArchive>.Fail(TapeError.BoundsExceeded, "TAP block limit.");
            var frame = bytes.Slice(offset, length).ToArray();
            offset += length;
            if (frame.Length == HeaderBytes && frame[0] == 0 && frames.Count > 0)
            {
                ReadFrames("TAP", frames, files, warnings);
                frames.Clear();
            }
            frames.Add(frame);
        }
        if (frames.Count == 0)
            return TapeResult<CpmArchive>.Fail(TapeError.InvalidFormat, "TAP contains no blocks.");
        ReadFrames("TAP", frames, files, warnings);
        return TapeResult<CpmArchive>.Ok(new("", "", files.ToImmutable(), warnings.ToImmutable()));
    }

    /// <summary>Ordinary Spectrum header-plus-data pairs become an HCTA archive. An input that is already HCTA is refused.</summary>
    public static TapeResult<CpmArchive> ImportSpectrum(ReadOnlySpan<byte> bytes, string archiveName, string created)
    {
        try
        {
            if (bytes.Length >= 8 && bytes[..8].SequenceEqual("ZXTape!\x1a"u8))
                return ImportTzx(bytes, archiveName, created);
            var loaded = Tap.LoadBounded(bytes, new TapeLimits().InputBytes, new TapeLimits().SourceBlocks);
            if (!loaded.Success) return TapeResult<CpmArchive>.Fail(loaded.Failure!.Code, loaded.Failure.Detail);
            return FromSpectrum(loaded.Value.List(), archiveName, created);
        }
        catch (InvalidDataException ex)
        {
            return TapeResult<CpmArchive>.Fail(TapeError.InvalidFormat, ex.Message);
        }
    }

    /// <summary>Host filename for unpack: upper case, letters and digits only, one dot.</summary>
    public static string HostFileName(string name)
    {
        var (stem, ext) = Split(name);
        stem = Safe(stem, 8);
        ext = Safe(ext, 3);
        if (stem.Length == 0) stem = "FILE";
        return ext.Length == 0 ? stem : stem + "." + ext;
    }

    private static TapeResult<CpmArchive> ImportTzx(ReadOnlySpan<byte> bytes, string archiveName, string created)
    {
        var parsed = TzxDocument.Parse(bytes, new TapeLimits());
        if (!parsed.Success) return TapeResult<CpmArchive>.Fail(parsed.Failure!.Code, parsed.Failure.Detail);
        if (IsHcta(parsed.Value))
            return TapeResult<CpmArchive>.Fail(TapeError.InvalidFormat, "input is an HCTA archive; use archive list or unpack");
        var blocks = new List<byte[]>();
        foreach (var block in parsed.Value.Blocks)
            if (block.Id == 0x10 && TryTapData(block, out var data)) blocks.Add(data);
        return FromSpectrum(PairSpectrum(blocks), archiveName, created);
    }

    private static TapeResult<CpmArchive> FromSpectrum(IReadOnlyList<TapeFile> spectrum, string archiveName, string created)
    {
        if (spectrum.Count == 0)
            return TapeResult<CpmArchive>.Fail(TapeError.InvalidFormat, "no standard-speed Spectrum files");
        var used = new HashSet<string>(StringComparer.Ordinal);
        var files = ImmutableArray.CreateBuilder<CpmArchiveFile>();
        foreach (var file in spectrum)
        {
            var ext = file.Type == 3 ? "BIN" : "BAS";
            var name = UniqueName(file.Name, ext, used);
            var length = file.Data.Length;
            files.Add(new CpmArchiveFile(0, name, 0, 0, RecordCount(length), (uint)length,
                false, false, null, file.Type == 3 ? file.Start : null, file.Data.ToArray()));
        }
        return TapeResult<CpmArchive>.Ok(new(CleanText(archiveName, 64, "archive name"),
            CleanText(created, 32, "created"), files.ToImmutable(), []));
    }

    private static List<TapeFile> PairSpectrum(List<byte[]> blocks)
    {
        var files = new List<TapeFile>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var header = blocks[i];
            if (header.Length != HeaderBytes || header[0] != 0 || header[1] > 3) continue;
            if (Xor(header) != 0) throw new InvalidDataException("Invalid TAP checksum.");
            if (i + 1 >= blocks.Count || blocks[i + 1].Length < 2 || blocks[i + 1][0] != 0xFF)
                throw new InvalidDataException("TAP header lacks a data block.");
            var data = blocks[++i];
            if (Xor(data) != 0) throw new InvalidDataException("Invalid TAP checksum.");
            var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12));
            if (length != data.Length - 2) throw new InvalidDataException("TAP header/data length mismatch.");
            files.Add(new TapeFile(System.Text.Encoding.ASCII.GetString(header, 2, 10).TrimEnd(), header[1],
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14)),
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(16)),
                data.AsSpan(1, length).ToArray()));
        }
        return files;
    }

    private static void ReadGroup(string groupName, List<TzxSourceBlock> group,
        ImmutableArray<CpmArchiveFile>.Builder files, ImmutableArray<string>.Builder warnings)
    {
        var data = new List<byte[]>();
        foreach (var block in group)
            if (block.Id == 0x10 && TryTapData(block, out var tap)) data.Add(tap);
        ReadFrames(groupName, data, files, warnings);
    }

    private static void ReadFrames(string groupName, List<byte[]> data,
        ImmutableArray<CpmArchiveFile>.Builder files, ImmutableArray<string>.Builder warnings)
    {
        var label = groupName.Length == 0 ? "(unnamed)" : groupName;
        if (data.Count < 2)
        {
            warnings.Add($"group '{label}' is not an HCTA file");
            return;
        }
        var header = data[0];
        var meta = data[1];
        if (Xor(header) != 0 || Xor(meta) != 0)
        {
            warnings.Add($"group '{label}': checksum mismatch");
            return;
        }
        if (header.Length != HeaderBytes || header[0] != 0 || header[1] != 3
            || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12)) != MetadataLength
            || meta.Length != MetadataBytes || meta[0] != 0xFF)
        {
            warnings.Add($"group '{label}' is not an HCTA file");
            return;
        }
        var payload = meta.AsSpan(1, MetadataLength);
        var name = NameFromDirectory(payload[1..12]);
        var declared = (uint)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14))
            | ((uint)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(16)) << 16);
        var raw = new List<byte>();
        long total = 0;
        for (var i = 2; i < data.Count; i++)
        {
            var chunk = data[i];
            if (chunk.Length < 2 || chunk[0] != 0xFF || Xor(chunk) != 0)
            {
                warnings.Add($"file '{name}': checksum mismatch");
                return;
            }
            var piece = chunk.AsSpan(1, chunk.Length - 2);
            total += piece.Length;
            if (total > declared)
            {
                warnings.Add($"file '{name}': data longer than the header length {declared}");
                return;
            }
            raw.AddRange(piece.ToArray());
        }
        if ((uint)total != declared)
        {
            warnings.Add($"file '{name}': data length {total} differs from the header length {declared}");
            return;
        }
        var flags = payload[19];
        byte[]? stamp = (flags & 4) != 0 ? payload[20..32].ToArray() : null;
        ushort? load = (flags & 8) != 0 ? BinaryPrimitives.ReadUInt16LittleEndian(payload[32..]) : null;
        if (payload[0] > 15) warnings.Add($"file '{name}': user {payload[0]} is outside 0-15");
        files.Add(new CpmArchiveFile(payload[0], name, payload[12], payload[13], payload[14],
            BinaryPrimitives.ReadUInt32LittleEndian(payload[15..]),
            (flags & 1) != 0, (flags & 2) != 0, stamp, load, raw.ToArray()));
    }

    private static bool IsHcta(TzxDocument document)
    {
        foreach (var block in document.Blocks)
        {
            if (block.Id == 0x30 && TextOf(block.Body.AsSpan()).StartsWith(Marker, StringComparison.Ordinal)) return true;
            if (block.Id == 0x32 && CreatorOf(block.Body.AsSpan()) == Creator) return true;
        }
        return false;
    }

    private static string CreatorOf(ReadOnlySpan<byte> body)
    {
        if (body.Length < 3) return "";
        var length = BinaryPrimitives.ReadUInt16LittleEndian(body);
        if (body.Length < 2 + length || length < 1) return "";
        var content = body.Slice(2, length);
        var count = content[0];
        var p = 1;
        for (var i = 0; i < count && p + 2 <= content.Length; i++)
        {
            var type = content[p];
            var len = content[p + 1];
            if (p + 2 + len > content.Length) return "";
            if (type == 0) return System.Text.Encoding.Latin1.GetString(content.Slice(p + 2, len));
            p += 2 + len;
        }
        return "";
    }

    private static void ReadArchiveInfo(ReadOnlySpan<byte> body, ref string name, ref string created)
    {
        if (body.Length < 3) return;
        var length = BinaryPrimitives.ReadUInt16LittleEndian(body);
        if (body.Length < 2 + length || length < 1) return;
        var content = body.Slice(2, length);
        var count = content[0];
        var p = 1;
        for (var i = 0; i < count && p + 2 <= content.Length; i++)
        {
            var type = content[p];
            var len = content[p + 1];
            if (p + 2 + len > content.Length) return;
            var text = System.Text.Encoding.Latin1.GetString(content.Slice(p + 2, len));
            if (type == 2) name = text;
            if (type == 3) created = text;
            p += 2 + len;
        }
    }

    private static bool TryTapData(TzxSourceBlock block, out byte[] data)
    {
        data = [];
        var body = block.Body.AsSpan();
        if (body.Length < 4) return false;
        var dlen = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
        if (body.Length != 4 + dlen) return false;
        data = body[4..].ToArray();
        return true;
    }

    private static void Validate(CpmArchiveFile file)
    {
        if (file.User > 15) throw new ArgumentException($"user {file.User} is outside 0-15");
        if (file.Extent > 31) throw new ArgumentException($"extent {file.Extent} is outside 0-31");
        if (file.Data is null) throw new ArgumentException("file data is missing");
        if (file.DateStamp is not null && file.DateStamp.Length != 12)
            throw new ArgumentException("date stamp must be 12 bytes");
        Split(file.Name);
    }

    private static byte[] HeaderBlock(string display, uint length)
    {
        var block = new byte[HeaderBytes];
        block[1] = 0x03;
        System.Text.Encoding.ASCII.GetBytes(display).CopyTo(block, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(12), MetadataLength);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(14), (ushort)length);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(16), (ushort)(length >> 16));
        block[18] = Xor(block.AsSpan(0, 18));
        return block;
    }

    private static byte[] MetadataBlock(CpmArchiveFile file)
    {
        var block = new byte[MetadataBytes];
        block[0] = 0xFF;
        var payload = block.AsSpan(1, MetadataLength);
        payload[0] = file.User;
        DirectoryBytes(file.Name).CopyTo(payload[1..]);
        payload[12] = file.Extent;
        payload[13] = file.ExtentHigh;
        payload[14] = file.RecordCount;
        BinaryPrimitives.WriteUInt32LittleEndian(payload[15..], file.AllocatedBytes);
        byte flags = 0;
        if (file.ReadOnly) flags |= 1;
        if (file.System) flags |= 2;
        if (file.DateStamp is not null) flags |= 4;
        if (file.LoadAddress is not null) flags |= 8;
        payload[19] = flags;
        file.DateStamp?.CopyTo(payload[20..]);
        if (file.LoadAddress is ushort address) BinaryPrimitives.WriteUInt16LittleEndian(payload[32..], address);
        block[35] = Xor(block.AsSpan(0, 35));
        return block;
    }

    private static IEnumerable<byte[]> DataBlocks(byte[] data)
    {
        for (var offset = 0; offset < data.Length;)
        {
            var count = Math.Min(DataChunkBytes, data.Length - offset);
            var block = new byte[count + 2];
            block[0] = 0xFF;
            data.AsSpan(offset, count).CopyTo(block.AsSpan(1));
            block[^1] = Xor(block.AsSpan(0, block.Length - 1));
            yield return block;
            offset += count;
        }
    }

    private static void StandardBlock(Stream output, byte[] tap)
    {
        output.WriteByte(0x10);
        WriteU16(output, BlockPause);
        WriteU16(output, checked((ushort)tap.Length));
        output.Write(tap);
    }

    private static void Pause(Stream output, ushort ms)
    {
        output.WriteByte(0x20);
        WriteU16(output, ms);
    }

    private static void ArchiveInfo(Stream output, string name, string created)
    {
        using var content = new MemoryStream();
        content.WriteByte(3);
        InfoEntry(content, 0, Creator);
        InfoEntry(content, 2, name);
        InfoEntry(content, 3, created);
        var bytes = content.ToArray();
        output.WriteByte(0x32);
        WriteU16(output, checked((ushort)bytes.Length));
        output.Write(bytes);
    }

    private static void InfoEntry(Stream output, byte type, string text)
    {
        var raw = System.Text.Encoding.Latin1.GetBytes(text);
        output.WriteByte(type);
        output.WriteByte(checked((byte)raw.Length));
        output.Write(raw);
    }

    private static void Text(Stream output, byte id, string text)
    {
        var raw = System.Text.Encoding.Latin1.GetBytes(text);
        if (raw.Length > 255) raw = raw[..255];
        output.WriteByte(id);
        output.WriteByte((byte)raw.Length);
        output.Write(raw);
    }

    private static string TextOf(ReadOnlySpan<byte> body)
    {
        if (body.Length < 1) return "";
        var length = body[0];
        if (body.Length < 1 + length) return "";
        return System.Text.Encoding.Latin1.GetString(body.Slice(1, length));
    }

    private static string DisplayName(string name)
    {
        var (stem, ext) = Split(name);
        return stem.PadRight(7)[..7] + ext.PadRight(3)[..3];
    }

    private static byte[] DirectoryBytes(string name)
    {
        var (stem, ext) = Split(name);
        var bytes = new byte[11];
        System.Text.Encoding.ASCII.GetBytes(stem.PadRight(8)[..8]).CopyTo(bytes, 0);
        System.Text.Encoding.ASCII.GetBytes(ext.PadRight(3)[..3]).CopyTo(bytes, 8);
        return bytes;
    }

    private static string NameFromDirectory(ReadOnlySpan<byte> eleven)
    {
        var stem = System.Text.Encoding.Latin1.GetString(eleven[..8]).TrimEnd();
        var ext = System.Text.Encoding.Latin1.GetString(eleven[8..]).TrimEnd();
        var name = ext.Length == 0 ? stem : stem + "." + ext;
        return name.Length == 0 ? "UNNAMED" : name;
    }

    private static (string Stem, string Ext) Split(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("file name is empty");
        var upper = name.Trim().ToUpperInvariant();
        var dot = upper.IndexOf('.');
        if (dot != upper.LastIndexOf('.')) throw new ArgumentException($"file name '{name}' has more than one dot");
        var stem = dot < 0 ? upper : upper[..dot];
        var ext = dot < 0 ? "" : upper[(dot + 1)..];
        if (stem.Length is < 1 or > 8 || ext.Length > 3 || !stem.All(Allowed) || !ext.All(Allowed))
            throw new ArgumentException($"file name '{name}' is not an 8.3 name of letters and digits");
        return (stem, ext);
    }

    private static bool Allowed(char c) => c is >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static string Safe(string text, int max) => new(text.Where(Allowed).Take(max).ToArray());

    private static string UniqueName(string spectrum, string ext, HashSet<string> used)
    {
        var stem = new string(spectrum.ToUpperInvariant().Where(Allowed).Take(8).ToArray());
        if (stem.Length == 0) stem = "FILE";
        for (var n = 0; n < 100; n++)
        {
            var suffix = n == 0 ? "" : n.ToString();
            var head = stem.Length + suffix.Length <= 8 ? stem : stem[..(8 - suffix.Length)];
            var candidate = head + suffix + "." + ext;
            if (used.Add(candidate)) return candidate;
        }
        throw new InvalidDataException($"too many Spectrum files named '{spectrum}'");
    }

    private static byte RecordCount(int length)
    {
        var records = (length + 127) / 128;
        return records >= 128 ? (byte)0x80 : (byte)records;
    }

    private static string CleanText(string text, int max, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException($"{label} is empty");
        var trimmed = text.Trim();
        if (trimmed.Length > max || trimmed.Any(c => c > 255 || c < 32))
            throw new ArgumentException($"{label} must be {max} characters or fewer, with no control characters");
        return trimmed;
    }

    private static void WriteU16(Stream output, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static byte Xor(ReadOnlySpan<byte> bytes)
    {
        byte x = 0;
        foreach (var b in bytes) x ^= b;
        return x;
    }
}
