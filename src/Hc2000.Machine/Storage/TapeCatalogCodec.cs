using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Codecs;

namespace Hc2000.Machine.Storage;

public sealed record TapeCatalogEntry(string Name, bool WriteProtected, uint ImageBytes, uint FirstBlock, uint ImageCrc);
public sealed record TapeCatalogIdentity(int SourceBlock, uint Crc);
public sealed record TapeCatalogRecord(TapeSide Side, ImmutableArray<TapeCatalogEntry> Entries,
    uint Crc, int StartBlock, int EndBlock)
{
    public TapeCatalogIdentity Identity => new(StartBlock, Crc);
}
public sealed record TapeCatalogSelection(TapeCatalogRecord? Newest, ImmutableArray<TapeCatalogRecord> Older);
public sealed record TapeCatalogPosition(int SourceBlock, uint DataBlock, int ContentOffset);
public sealed record TapeCatalogSide(TapeSide Side, ImmutableArray<ImmutableArray<byte>> RawBlocks, bool WriteProtected = false);
/// <summary>A default Image omits the volume; a non-default Image replaces it.</summary>
public sealed record TapeCatalogMutation(string Name, ImmutableArray<byte> Image, bool WriteProtected = false);
/// <summary>Caller-owned admission, not a physical tape-time calculation. TAP overhead=2, standard TZX=5.</summary>
public sealed record TapeCatalogAppendBudget(long FramedBytes, int Blocks, bool PhysicalAdmission, int FramingBytesPerBlock = 2);
public sealed record TapeCatalogAppendPlan(TapeCatalogIdentity? Expected, TapeCatalogRecord Published,
    ImmutableArray<ImmutableArray<byte>> AppendedBlocks, string? ChangedImage);

/// <summary>Detached spec13 framing/selection only; no CP/M directory, deck, session, clock or I/O.</summary>
public sealed class TapeCatalogCodec
{
    private sealed class Refusal(TapeError code, string detail) : Exception(detail) { public TapeError Code => code; }
    private static void Require(bool condition, string detail, TapeError code = TapeError.InvalidFormat)
    { if (!condition) throw new Refusal(code, detail); }
    private static TapeResult<T> Run<T>(Func<T> action) where T : notnull
    {
        try { return TapeResult<T>.Ok(action()); }
        catch (Refusal e) { return TapeResult<T>.Fail(e.Code, e.Message); }
        catch (OverflowException) { return TapeResult<T>.Fail(TapeError.BoundsExceeded, "Catalog arithmetic overflow."); }
    }
    private static void Policy(TapeSide side, TapeLimits limits)
    {
        Require(side is TapeSide.A or TapeSide.B, "Invalid side.");
        Require(limits.InputBytes >= 0 && limits.DecodedBytes >= 0 && limits.SourceBlocks >= 0,
            "Invalid catalog budgets.", TapeError.BoundsExceeded);
    }
    private static ImmutableArray<byte> Owned(byte[] bytes) => ImmutableArray.CreateRange(bytes);
    // Shared seam hc2000-hdd-shared-catalog-metadata: the tape side delegates its CRC and 8.3 name rules to
    // VolumeCatalogMetadata; the predicates below stay exactly what they were.
    private static uint Crc(ReadOnlySpan<byte> content) => VolumeCatalogMetadata.Crc32(content);
    private static byte[] NameBytes(string name)
    {
        var bytes = VolumeCatalogMetadata.CanonicalName83(name);
        Require(bytes is not null, "Invalid catalog 8.3 name.");
        return bytes!;
    }
    private static void Entry(TapeCatalogEntry entry)
    {
        _ = NameBytes(entry.Name);
        Require(entry.ImageBytes is >= 16384 and <= 8388608 && entry.ImageBytes % 4096 == 0, "Invalid volume size.");
    }
    private static void Entries(ImmutableArray<TapeCatalogEntry> entries)
    {
        Require(!entries.IsDefault && entries.Length <= ushort.MaxValue, "Invalid catalog count.", TapeError.BoundsExceeded);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            Entry(entry);
            Require(names.Add(Encoding.ASCII.GetString(NameBytes(entry.Name))), "Duplicate volume name.");
        }
        Require(entries.Select(e => e.FirstBlock).Distinct().Count() == entries.Length, "Overlapping image starts.");
    }
    private sealed class View
    {
        public TapeCatalogSide Side { get; }
        public TapeLimits Limits { get; }
        public List<int> Data { get; } = [];
        public List<long> Starts { get; } = [];
        public long RawBytes { get; private set; }
        private long charged;
        private readonly View? sharedBudget;
        public View(TapeCatalogSide side, TapeLimits limits, View? sharedBudget = null, long? reservedWork = null)
        {
            Policy(side.Side, limits); Side = side; Limits = limits; this.sharedBudget = sharedBudget;
            Require(!side.RawBlocks.IsDefault && side.RawBlocks.Length <= limits.SourceBlocks, "Source block budget exceeded.", TapeError.BoundsExceeded);
            var dataCount = 0;
            foreach (var block in side.RawBlocks)
            {
                Require(!block.IsDefault && block.Length is >= 2 and <= 65535, "Invalid raw block length.");
                RawBytes = checked(RawBytes + block.Length + 2L);
                Require(RawBytes <= limits.InputBytes, "Input byte budget exceeded.", TapeError.BoundsExceeded);
                if (block[0] == 0xff) dataCount++;
            }
            var initialWork = checked(RawBytes + 32L * dataCount);
            Require(reservedWork is null || initialWork <= reservedWork.Value, "Import reservation exceeded.", TapeError.BoundsExceeded);
            Charge(reservedWork ?? initialWork); // Keep import copies/metadata charged through initial indexing/XOR.
            Data.Capacity = dataCount; Starts.Capacity = dataCount;
            long contentOffset = 0;
            foreach (var (block, index) in side.RawBlocks.Select((b, i) => (b, i)))
            {
                byte xor = 0; foreach (var b in block) xor ^= b;
                Require(xor == 0 && block[0] is 0 or 0xff, "Invalid raw flag/XOR.");
                if (block[0] == 0xff) { Data.Add(index); Starts.Add(contentOffset); contentOffset += block.Length - 2; }
            }
        }
        public void Charge(long bytes)
        {
            if (sharedBudget is not null) { sharedBudget.Charge(bytes); return; }
            charged = checked(charged + bytes);
            Require(charged <= Limits.DecodedBytes, "Decoded/scan work budget exceeded.", TapeError.BoundsExceeded);
        }
        public (byte[] Bytes, int End) Collect(int start, int length, bool exact)
        {
            Require(start >= 0 && start < Side.RawBlocks.Length && length > 0, "Invalid payload start/length.");
            Charge(2L * length); // Temporary bytes plus detached/result copies, and repeated candidate scanning.
            var output = new byte[length]; var copied = 0;
            for (var i = start; i < Side.RawBlocks.Length; i++)
            {
                var block = Side.RawBlocks[i]; Require(block[0] == 0xff && block.Length > 2, "Header/empty block interrupts payload.");
                var content = block.AsSpan(1, block.Length - 2); var take = Math.Min(content.Length, length - copied);
                if (exact) Require(content.Length <= length - copied, "Trailing payload bytes.");
                content[..take].CopyTo(output.AsSpan(copied)); copied += take;
                if (copied == length) return (output, i);
            }
            throw new Refusal(TapeError.InvalidFormat, "Truncated payload.");
        }
        public int ImageStart(TapeCatalogEntry entry)
        {
            Entry(entry); Require(entry.FirstBlock < (uint)Data.Count, "Image start is missing.");
            return Data[(int)entry.FirstBlock];
        }
    }
    private static void Overlaps(View view, ImmutableArray<TapeCatalogEntry> entries)
    {
        // CRC/image-read failure does not remove a valid catalog entry. Inspect ranges without loading images.
        var ranges = entries.Where(e => e.FirstBlock < (uint)view.Starts.Count)
            .Select(e => (Start: view.Starts[(int)e.FirstBlock], End: checked(view.Starts[(int)e.FirstBlock] + e.ImageBytes)))
            .OrderBy(r => r.Start).ToArray();
        for (var i = 1; i < ranges.Length; i++) Require(ranges[i].Start >= ranges[i - 1].End, "Overlapping image ranges.");
        Require(entries.Select(e => e.FirstBlock).Distinct().Count() == entries.Length, "Overlapping image starts.");
    }
    private static TapeCatalogRecord Parse(View view, int start)
    {
        var header = view.Collect(start, 8, false).Bytes;
        Require(header.AsSpan(0, 4).SequenceEqual("HCAT"u8), "Not a catalog.");
        Require(header[4] == 1, "Unsupported catalog version.", TapeError.UnsupportedVersion);
        Require(header[5] == (byte)view.Side.Side, "Catalog side mismatch.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        var length = checked(12 + 24 * count); view.Charge(128L * count);
        var (bytes, end) = view.Collect(start, length, true);
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(length - 4));
        Require(Crc(bytes.AsSpan(0, length - 4)) == crc, "Torn catalog CRC.");
        var entries = ImmutableArray.CreateBuilder<TapeCatalogEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var field = bytes.AsSpan(8 + 24 * i, 24);
            var stem = Encoding.ASCII.GetString(field[..8]).TrimEnd(' '); var ext = Encoding.ASCII.GetString(field.Slice(8, 3)).TrimEnd(' ');
            var name = stem + (ext.Length == 0 ? "" : "." + ext);
            Require(field[..11].SequenceEqual(NameBytes(name)) && (field[11] & 0xfe) == 0, "Invalid catalog name/flags.");
            entries.Add(new(name, (field[11] & 1) != 0, BinaryPrimitives.ReadUInt32LittleEndian(field[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(field[16..]), BinaryPrimitives.ReadUInt32LittleEndian(field[20..])));
        }
        var result = entries.ToImmutable(); Entries(result); Overlaps(view, result);
        return new(view.Side.Side, result, crc, start, end);
    }
    private static TapeCatalogSelection Select(View view)
    {
        var valid = new List<TapeCatalogRecord>();
        foreach (var start in view.Data)
        {
            try { var record = Parse(view, start); view.Charge(64); valid.Add(record); }
            catch (Refusal e) when (e.Code is TapeError.InvalidFormat or TapeError.UnsupportedVersion) { }
        }
        return new(valid.LastOrDefault(), valid.Take(Math.Max(0, valid.Count - 1)).Reverse().ToImmutableArray());
    }
    private static byte[] Encode(TapeSide side, ImmutableArray<TapeCatalogEntry> entries, TapeLimits limits)
    {
        Policy(side, limits);
        Require(!entries.IsDefault && entries.Length <= ushort.MaxValue, "Invalid catalog count.", TapeError.BoundsExceeded);
        var length = checked(12 + 24 * entries.Length);
        Require(length <= limits.InputBytes && 3L * length + 128L * entries.Length <= limits.DecodedBytes,
            "Record encoding budget exceeded.", TapeError.BoundsExceeded);
        Entries(entries);
        var bytes = new byte[length]; "HCAT"u8.CopyTo(bytes); bytes[4] = 1; bytes[5] = (byte)side;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i]; var field = bytes.AsSpan(8 + 24 * i, 24); NameBytes(entry.Name).CopyTo(field);
            field[11] = entry.WriteProtected ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt32LittleEndian(field[12..], entry.ImageBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(field[16..], entry.FirstBlock);
            BinaryPrimitives.WriteUInt32LittleEndian(field[20..], entry.ImageCrc);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(length - 4), Crc(bytes.AsSpan(0, length - 4)));
        return bytes;
    }
    private static byte[] Image(View view, TapeCatalogEntry entry)
    {
        var bytes = view.Collect(view.ImageStart(entry), checked((int)entry.ImageBytes), true).Bytes;
        Require(Crc(bytes) == entry.ImageCrc, "Image CRC mismatch."); return bytes;
    }
    /// <summary>Admit wire/copy/index work before the single native parser; expose only a detached valid view.</summary>
    public TapeResult<TapeCatalogSide> FromTap(ReadOnlySpan<byte> wire, TapeSide side, TapeLimits limits)
    {
        try
        {
            Policy(side, limits);
            var copyWork = checked(4L * wire.Length);
            Require(wire.Length <= limits.InputBytes && copyWork <= limits.DecodedBytes,
                "TAP import reservation exceeded.", TapeError.BoundsExceeded);
            var blockCap = checked((int)Math.Min(limits.SourceBlocks, (limits.DecodedBytes - copyWork) / 128));
            var reservation = checked(copyWork + 128L * blockCap);
            var native = Tap.LoadBounded(wire, limits.InputBytes, blockCap);
            if (!native.Success) return TapeResult<TapeCatalogSide>.Fail(native.Failure!.Code, native.Failure.Detail);
            var blocks = native.Value.CopyRawBlocks(); // Only after admission/native success; never return the mutable Tap.
            var result = new TapeCatalogSide(side, blocks.Select(Owned).ToImmutableArray());
            _ = new View(result, limits, reservedWork: reservation);
            return TapeResult<TapeCatalogSide>.Ok(result);
        }
        catch (Refusal e) { return TapeResult<TapeCatalogSide>.Fail(e.Code, e.Message); }
        catch (OverflowException) { return TapeResult<TapeCatalogSide>.Fail(TapeError.BoundsExceeded, "Catalog arithmetic overflow."); }
    }
    public TapeResult<TapeCatalogSide> FromTzx(TzxDocument document, TapeSide side, TapeLimits limits) => Run(() =>
    {
        Policy(side, limits); Require(!document.Blocks.IsDefault && document.Blocks.Length <= limits.SourceBlocks,
            "TZX view block budget exceeded.", TapeError.BoundsExceeded);
        Require(document.Blocks.All(b => b.Id == 0x10), "Only straight-line standard TZX blocks are supported.", TapeError.UnsupportedFormat);
        Require(document.Blocks.All(b => !b.Body.IsDefault), "Missing standard TZX body.");
        Require(document.Blocks.Sum(b => (long)b.Body.Length + 1) + 10 <= limits.InputBytes &&
            document.Blocks.Sum(b => 4L * b.Body.Length + 32) <= limits.DecodedBytes, "TZX view byte budget exceeded.", TapeError.BoundsExceeded);
        var raw = ImmutableArray.CreateBuilder<ImmutableArray<byte>>(document.Blocks.Length);
        foreach (var block in document.Blocks)
        {
            Require(!block.Body.IsDefault && block.Body.Length >= 6 && BinaryPrimitives.ReadUInt16LittleEndian(block.Body.AsSpan(2, 2)) == block.Body.Length - 4,
                "Invalid standard TZX body.");
            raw.Add(Owned(block.Body.AsSpan()[4..].ToArray()));
        }
        var result = new TapeCatalogSide(side, raw.ToImmutable()); _ = new View(result, limits); return result;
    });
    public TapeResult<TapeCatalogRecord> ParseRecord(TapeCatalogSide side, int startBlock, TapeLimits limits) => Run(() => Parse(new View(side, limits), startBlock));
    public TapeResult<TapeCatalogSelection> FindNewest(TapeCatalogSide side, TapeLimits limits) => Run(() => Select(new View(side, limits)));
    public TapeResult<ImmutableArray<byte>> EncodeRecord(TapeSide side, ImmutableArray<TapeCatalogEntry> entries, TapeLimits limits) => Run(() => Owned(Encode(side, entries, limits)));
    public TapeResult<ImmutableArray<byte>> ReadImage(TapeCatalogSide side, TapeCatalogEntry entry, TapeLimits limits) => Run(() => Owned(Image(new View(side, limits), entry)));
    public TapeResult<TapeCatalogRecord> FindRecovery(TapeCatalogSide side, string name, ImmutableArray<TapeCatalogRecord> staleCandidates, TapeLimits limits) => Run(() =>
    {
        _ = NameBytes(name); var view = new View(side, limits); var selection = Select(view);
        Require(!staleCandidates.IsDefault && staleCandidates.Length <= limits.SourceBlocks, "Invalid stale candidate budget.", TapeError.BoundsExceeded);
        view.Charge(64L * staleCandidates.Length);
        var admitted = staleCandidates.Select(s => s.Identity).ToHashSet();
        foreach (var candidate in selection.Older.Where(r => admitted.Contains(r.Identity)))
        {
            var entry = candidate.Entries.SingleOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;
            try { _ = Image(view, entry); return candidate; }
            catch (Refusal e) when (e.Code == TapeError.InvalidFormat) { }
        }
        throw new Refusal(TapeError.InvalidFormat, "No older matching image.");
    });
    public TapeResult<TapeCatalogPosition> MapImageOffset(TapeCatalogSide side, TapeCatalogEntry entry, uint byteOffset, TapeLimits limits) => Run(() =>
    {
        var view = new View(side, limits); Require(byteOffset < entry.ImageBytes, "Image offset outside volume.");
        _ = Image(view, entry); var remaining = byteOffset;
        for (var i = (int)entry.FirstBlock; i < view.Data.Count; i++)
        {
            var source = view.Data[i]; var length = (uint)(side.RawBlocks[source].Length - 2);
            if (remaining < length) return new TapeCatalogPosition(source, (uint)i, (int)remaining);
            remaining -= length;
        }
        throw new Refusal(TapeError.InvalidFormat, "Missing image offset.");
    });
    private static ImmutableArray<ImmutableArray<byte>> Split(ReadOnlySpan<byte> content)
    {
        var blocks = ImmutableArray.CreateBuilder<ImmutableArray<byte>>();
        while (!content.IsEmpty)
        {
            var count = Math.Min(content.Length, 65533); var raw = new byte[count + 2]; raw[0] = 0xff;
            content[..count].CopyTo(raw.AsSpan(1)); byte xor = 0; foreach (var b in raw.AsSpan(0, raw.Length - 1)) xor ^= b;
            raw[^1] = xor; blocks.Add(Owned(raw)); content = content[count..];
        }
        return blocks.ToImmutable();
    }
    public TapeResult<TapeCatalogAppendPlan> PlanAppend(TapeCatalogSide side, TapeCatalogIdentity? expectedCatalogIdentity,
        TapeCatalogMutation mutation, TapeCatalogAppendBudget appendBudget, TapeLimits limits) => Run(() =>
    {
        var view = new View(side, limits); var current = Select(view).Newest;
        Require(current?.Identity == expectedCatalogIdentity, "Stale catalog identity.", TapeError.Interlock);
        Require(!side.WriteProtected, "Side is write-protected.", TapeError.WriteProtected);
        _ = NameBytes(mutation.Name); var old = current?.Entries ?? [];
        view.Charge(128L * (old.Length + 1));
        var target = old.SingleOrDefault(e => string.Equals(e.Name, mutation.Name, StringComparison.OrdinalIgnoreCase));
        Require(target?.WriteProtected != true, "Volume is write-protected.", TapeError.WriteProtected);
        var entries = old.Where(e => !string.Equals(e.Name, mutation.Name, StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
        var imageLength = mutation.Image.IsDefault ? 0 : mutation.Image.Length;
        Require(!mutation.Image.IsDefault || target is not null, "Cannot erase absent volume.");
        if (!mutation.Image.IsDefault)
        {
            var entry = new TapeCatalogEntry(mutation.Name, mutation.WriteProtected, checked((uint)imageLength), checked((uint)view.Data.Count), 0);
            Entry(entry); entries = entries.Add(entry);
        }
        Entries(entries); var recordLength = checked(12 + 24 * entries.Length);
        var imageBlocks = (imageLength + 65532) / 65533; var recordBlocks = (recordLength + 65532) / 65533;
        var count = checked(imageBlocks + recordBlocks);
        Require(appendBudget.PhysicalAdmission && appendBudget.FramingBytesPerBlock is 2 or 5 && appendBudget.Blocks >= count &&
            appendBudget.FramedBytes >= imageLength + (long)recordLength + count * (2L + appendBudget.FramingBytesPerBlock),
            "Append capacity/admission refused.", TapeError.CapacityExceeded);
        Require(side.RawBlocks.Length + (long)count <= limits.SourceBlocks &&
            view.RawBytes + imageLength + (long)recordLength + count * (2L + appendBudget.FramingBytesPerBlock) <= limits.InputBytes,
            "Append resource budget exceeded.", TapeError.BoundsExceeded);
        view.Charge(4L * (imageLength + (long)recordLength));
        if (!mutation.Image.IsDefault) entries = entries.SetItem(entries.Length - 1, entries[^1] with { ImageCrc = Crc(mutation.Image.AsSpan()) });
        var record = Encode(side.Side, entries, limits);
        var appended = (mutation.Image.IsDefault ? ImmutableArray<ImmutableArray<byte>>.Empty : Split(mutation.Image.AsSpan())).AddRange(Split(record));
        var combined = side with { RawBlocks = side.RawBlocks.AddRange(appended) };
        var published = Parse(new View(combined, limits, view), side.RawBlocks.Length + imageBlocks);
        return new TapeCatalogAppendPlan(expectedCatalogIdentity, published, appended, mutation.Image.IsDefault ? null : mutation.Name);
    });
    public TapeResult<bool> CheckPublication(TapeCatalogSide side, TapeCatalogAppendPlan plan, TapeLimits limits) => Run(() =>
    {
        var view = new View(side, limits); var newest = Select(view).Newest;
        Require(newest is not null && newest.Identity == plan.Published.Identity && newest.Side == plan.Published.Side &&
            newest.Entries.SequenceEqual(plan.Published.Entries), "Planned catalog is not published/newest.", TapeError.Interlock);
        if (plan.ChangedImage is not null)
        {
            var entry = newest!.Entries.SingleOrDefault(e => string.Equals(e.Name, plan.ChangedImage, StringComparison.OrdinalIgnoreCase));
            Require(entry is not null, "Published image is missing."); _ = Image(view, entry!);
        }
        return true;
    });
}
