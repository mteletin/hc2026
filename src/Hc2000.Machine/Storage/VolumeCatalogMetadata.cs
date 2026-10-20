using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Hc2000.Machine.Tape;

namespace Hc2000.Machine.Storage;

/// <summary>Shared pure metadata for the tape HCAT and HDD HCVD/HCVM1END formats: one CRC, one 8.3 name/flags rule,
/// separate slot/block coordinate types and checked u64 arithmetic before narrowing. No I/O, no arena bytes.</summary>
public static class VolumeCatalogMetadata
{
    /// <summary>Fixed raw slot size of the HDD arena (S=8388608); the same 8 MiB policy bounds tape images.</summary>
    public const uint SlotBytes = 8_388_608;

    /// <summary>Initial unfinalized CRC-32/ISO-HDLC register, not the checksum of empty content.</summary>
    public const uint Crc32InitialState = uint.MaxValue;

    /// <summary>Updates an unfinalized reflected 0xEDB88320 register without initial/final xor or allocation.
    /// Empty content preserves state; caller state and input are not mutated.</summary>
    public static uint Crc32Update(uint state, ReadOnlySpan<byte> content)
    {
        foreach (var b in content)
        {
            state ^= b;
            for (var bit = 0; bit < 8; bit++) state = (state >> 1) ^ ((state & 1) != 0 ? 0xEDB88320u : 0u);
        }
        return state;
    }

    /// <summary>Final xor (complement) of an unfinalized register; does not consume or mutate caller state.</summary>
    public static uint Crc32Finish(uint state) => ~state;

    /// <summary>CRC-32/ISO-HDLC: reflected 0xEDB88320, init/final xor 0xFFFFFFFF. The only catalog CRC in the repo.</summary>
    public static uint Crc32(ReadOnlySpan<byte> content) => Crc32Finish(Crc32Update(Crc32InitialState, content));

    /// <summary>Canonical padded ASCII 8.3 name (11 bytes); null when the name is not a valid catalog 8.3 name.</summary>
    public static byte[]? CanonicalName83(string name)
    {
        if (name is null) return null;
        var dot = name.LastIndexOf('.');
        var stem = dot < 0 ? name : name[..dot];
        var ext = dot < 0 ? "" : name[(dot + 1)..];
        if (stem.Length < 1 || stem.Length > 8 || stem.TrimEnd(' ').Length == 0 || ext.Length > 3 || stem.Contains('.') ||
            name.Any(c => c is < ' ' or > '~' || "<>/\\:*?\"|".Contains(c))) return null;
        return Encoding.ASCII.GetBytes(stem.PadRight(8) + ext.PadRight(3));
    }

    /// <summary>Decodes an 11-byte name field plus its 1 flags byte; null when the field is non-canonical, non-printable or the
    /// flags byte has any bit set other than bit0 (read-only). The stem and extension are the field's space-trimmed halves and a
    /// returned dotted name round-trips to exactly the same field (the tape corpus's parse rule).</summary>
    public static string? ParseName83(ReadOnlySpan<byte> name11, byte flags)
    {
        if (name11.Length != 11 || (flags & 0xfe) != 0) return null;
        var stem = Encoding.ASCII.GetString(name11[..8]).TrimEnd(' ');
        var ext = Encoding.ASCII.GetString(name11[8..11]).TrimEnd(' ');
        var name = ext.Length == 0 ? stem : stem + "." + ext;
        var canonical = CanonicalName83(name);
        return canonical is not null && name11.SequenceEqual(canonical) ? name : null;
    }
}

/// <summary>Tape data-block ordinal (the existing HCAT FirstBlock semantics); bounded by the side's data block count.</summary>
public readonly record struct TapeBlockIndex(uint Value);

/// <summary>Raw 8 MiB slot ordinal inside an HDD arena; its offset is Value*SlotBytes and rows must permute 0..N-1.
/// Distinct from <see cref="TapeBlockIndex"/>: HCVD is a different format, never accepted by legacy tape transport.</summary>
public readonly record struct HddSlotOrdinal(uint Value);

/// <summary>One shared catalog row: canonical 8.3 name, flags bit0 read-only, exact 8 MiB image, unique slot ordinal, image CRC.</summary>
public sealed record HddCatalogEntry(string Name, bool ReadOnly, uint ImageBytes, HddSlotOrdinal SlotOrdinal, uint ImageCrc);

/// <summary>The recognized enclosing footer with checked derived ranges: the index sits at ArenaBytes..IndexEnd and the file
/// ends at IndexEnd+64. Recognition is all-or-nothing.</summary>
public sealed record HddFooterView(byte[] ContainerId, ulong Revision, uint Entries, ulong ArenaBytes,
    uint IndexBytes, uint IndexCrc, ulong IndexStart, ulong IndexEnd, ulong FileLength);

/// <summary>A container's identity plus its entries in writer row order; selection by name/ordinal is a pure data lookup,
/// never an inference from row position.</summary>
public sealed record HddContainerDescriptor(HddFooterView Footer, ImmutableArray<HddCatalogEntry> Entries);

/// <summary>Detached metadata only, not an arena or proof of stored payloads, file identity or publication.</summary>
public sealed record HddEncodedMetadata(ImmutableArray<byte> Index, ImmutableArray<byte> Footer, ulong FileLength);

/// <summary>Finite caller admission bounds; checked u64 arithmetic happens before any narrowing. The G4 selection/copy budgets
/// (K/S/Q/R/E/C) belong to the later selection and streaming slices.</summary>
public sealed record HddLimits(uint MaxFileBytes, uint MaxEntries, uint MaxIndexBytes, uint MaxMetadataBytes);

/// <summary>Bounded pure read API for HDD containers: footer recognition, index decoding and descriptors. Never reads arena
/// bytes, never does I/O; tape HCAT records are parsed with the same shared primitives.</summary>
public static class HddCatalogCodec
{
    private sealed class Refusal(TapeError code, string detail) : Exception(detail) { public TapeError Code => code; }
    private static void Require(bool condition, string detail, TapeError code = TapeError.InvalidFormat)
    { if (!condition) throw new Refusal(code, detail); }

    /// <summary>Encodes existing v1 metadata with ascending slot ordinals. The caller must exclusively stabilize ID
    /// backing storage and entries for this synchronous call (including no unsafe ImmutableArray backing mutation).
    /// Copies only bounded metadata; output owns immutable storage and never aliases the caller. Revision is encoded
    /// verbatim, not incremented; this does not authorize a mutation or verify any arena bytes.</summary>
    public static TapeResult<HddEncodedMetadata> EncodeHdd(ReadOnlySpan<byte> containerId, ulong revision,
        ImmutableArray<HddCatalogEntry> entries, HddLimits limits)
    {
        try { return TapeResult<HddEncodedMetadata>.Ok(Encode(containerId, revision, entries, limits)); }
        catch (Refusal e) { return TapeResult<HddEncodedMetadata>.Fail(e.Code, e.Message); }
        catch (OverflowException) { return TapeResult<HddEncodedMetadata>.Fail(TapeError.BoundsExceeded, "Container arithmetic overflow."); }
    }

    private static HddEncodedMetadata Encode(ReadOnlySpan<byte> containerId, ulong revision,
        ImmutableArray<HddCatalogEntry> entries, HddLimits limits)
    {
        Require(limits is not null, "Missing container limits.");
        Require(!entries.IsDefault, "Missing catalog entries.");
        Require(containerId.Length == 16, "Invalid container identity length.");
        Require(revision >= 1, "Invalid container revision.");
        var count = (ulong)entries.Length;
        Require(count <= ushort.MaxValue && count <= limits!.MaxEntries,
            "Entry count exceeds limit.", TapeError.BoundsExceeded);
        var indexBytes = checked(12UL + 24UL * count);
        var arenaBytes = checked(count * VolumeCatalogMetadata.SlotBytes);
        var fileLength = checked(arenaBytes + indexBytes + 64UL);
        // Same conservative reserve as decoding: work/output copies, bounded name/row/ordinal bookkeeping,
        // footer copies and the 16-byte identity copy. No count-dependent allocation precedes admission.
        var metadata = checked(4UL * indexBytes + 128UL * count + 128UL);
        Require(indexBytes <= limits!.MaxIndexBytes && fileLength <= limits.MaxFileBytes && metadata <= limits.MaxMetadataBytes,
            "Container encoding budget exceeded.", TapeError.BoundsExceeded);
        var id = containerId.ToArray();
        Require(id.Any(b => b != 0), "Empty container identity.");
        var rows = new HddCatalogEntry[entries.Length];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            Require(entry is not null, "Missing catalog entry.");
            Require(entry!.Name is not null && entry.Name.Length <= 12, "Invalid catalog 8.3 name.");
            var canonical = VolumeCatalogMetadata.CanonicalName83(entry.Name!);
            Require(canonical is not null, "Invalid catalog 8.3 name.");
            var name = VolumeCatalogMetadata.ParseName83(canonical!, 0);
            Require(name is not null && names.Add(name), "Invalid or duplicate volume name.");
            Require(entry.ImageBytes == VolumeCatalogMetadata.SlotBytes, "Invalid slot image length.");
            var ordinal = entry.SlotOrdinal.Value;
            Require(ordinal < count && rows[ordinal] is null, "Invalid slot ordinal.");
            rows[ordinal] = entry;
        }
        // Validate all rows before allocating output; no output is exposed on a typed refusal.
        var index = new byte[checked((int)indexBytes)];
        "HCVD"u8.CopyTo(index);
        index[4] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(index.AsSpan(6, 2), (ushort)count);
        for (var ordinal = 0; ordinal < rows.Length; ordinal++)
        {
            var entry = rows[ordinal];
            var field = index.AsSpan(8 + 24 * ordinal, 24);
            VolumeCatalogMetadata.CanonicalName83(entry.Name)!.CopyTo(field);
            field[11] = entry.ReadOnly ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt32LittleEndian(field[12..16], entry.ImageBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(field[16..20], entry.SlotOrdinal.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(field[20..24], entry.ImageCrc);
        }
        var indexCrc = VolumeCatalogMetadata.Crc32(index.AsSpan(0, index.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(index.Length - 4), indexCrc);
        var footer = new byte[64];
        "HCVM1END"u8.CopyTo(footer);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(8, 4), 1);
        id.CopyTo(footer, 16);
        BinaryPrimitives.WriteUInt64LittleEndian(footer.AsSpan(32, 8), revision);
        BinaryPrimitives.WriteUInt64LittleEndian(footer.AsSpan(40, 8), arenaBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(48, 4), (uint)indexBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(52, 4), indexCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(60, 4), VolumeCatalogMetadata.Crc32(footer.AsSpan(0, 60)));
        return new HddEncodedMetadata(ImmutableArray.CreateRange(index), ImmutableArray.CreateRange(footer), fileLength);
    }

    private static HddFooterView Inspect(ReadOnlySpan<byte> footer, ulong fileLength, HddLimits limits)
    {
        Require(footer.Length == 64, "Invalid footer length.");
        Require(footer[..8].SequenceEqual("HCVM1END"u8), "Invalid footer magic.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(footer[8..12]);
        Require(version == 1, "Unsupported container version.", TapeError.UnsupportedVersion);
        Require(BinaryPrimitives.ReadUInt32LittleEndian(footer[12..16]) == 0, "Unsupported container flags.", TapeError.UnsupportedVersion);
        var idEmpty = true;
        foreach (var b in footer[16..32]) { if (b != 0) { idEmpty = false; break; } }
        Require(!idEmpty, "Empty container identity.");
        var revision = BinaryPrimitives.ReadUInt64LittleEndian(footer[32..40]);
        Require(revision >= 1, "Invalid container revision.");
        var arenaBytes = BinaryPrimitives.ReadUInt64LittleEndian(footer[40..48]);
        var indexBytes = BinaryPrimitives.ReadUInt32LittleEndian(footer[48..52]);
        var indexCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer[52..56]);
        Require(BinaryPrimitives.ReadUInt32LittleEndian(footer[56..60]) == 0, "Invalid footer reserved field.");
        var footerCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer[60..64]);
        Require(indexBytes >= 12 && (indexBytes - 12) % 24 == 0, "Invalid index length.");
        var entries = (uint)((indexBytes - 12) / 24);
        Require(entries <= 65535, "Entry count exceeds wire maximum.", TapeError.BoundsExceeded);
        Require(entries <= limits.MaxEntries, "Entry count exceeds caller limit.", TapeError.BoundsExceeded);
        Require(indexBytes <= limits.MaxIndexBytes, "Index bytes exceed caller limit.", TapeError.BoundsExceeded);
        var arena = checked((ulong)entries * (ulong)VolumeCatalogMetadata.SlotBytes);
        Require(arenaBytes == arena, "Footer arena length mismatch.");
        var indexEnd = checked(arenaBytes + (ulong)indexBytes);
        var length = checked(indexEnd + 64UL);
        Require(fileLength == length, "Enclosing length mismatch.", TapeError.BoundsExceeded);
        Require(fileLength <= (ulong)limits.MaxFileBytes, "File length exceeds caller limit.", TapeError.BoundsExceeded);
        var metadata = checked(4UL * indexBytes + 128UL * entries + 128UL);
        Require(metadata <= (ulong)limits.MaxMetadataBytes, "Metadata reserve exceeds caller limit.", TapeError.BoundsExceeded);
        Require(footerCrc == VolumeCatalogMetadata.Crc32(footer[0..60]), "Torn footer CRC.");
        return new HddFooterView(footer[16..32].ToArray(), revision, entries, arena, indexBytes, indexCrc, arena, indexEnd, length);
    }

    /// <summary>Recognizes the enclosing 64-byte footer and derives the checked index ranges. Unknown version, torn CRCs,
    /// trailing bytes or impossible checked ranges refuse; there is no fallback to older embedded records.</summary>
    public static TapeResult<HddFooterView> InspectHddFooter(ReadOnlySpan<byte> footer, ulong fileLength, HddLimits limits)
    {
        try { return TapeResult<HddFooterView>.Ok(Inspect(footer, fileLength, limits)); }
        catch (Refusal e) { return TapeResult<HddFooterView>.Fail(e.Code, e.Message); }
        catch (OverflowException) { return TapeResult<HddFooterView>.Fail(TapeError.BoundsExceeded, "Container arithmetic overflow."); }
    }

    private static HddContainerDescriptor Decode(ReadOnlySpan<byte> footer, ReadOnlySpan<byte> index, ulong fileLength, HddLimits limits)
    {
        var view = Inspect(footer, fileLength, limits);
        Require(index.Length == view.IndexBytes, "Invalid index length.");
        Require(index[..4].SequenceEqual("HCVD"u8), "Invalid index magic.");
        Require(index[4] == 1, "Unsupported index version.", TapeError.UnsupportedVersion);
        Require(index[5] == 0, "Invalid index reserved byte.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(index[6..8]);
        Require(count == view.Entries, "Footer/index count mismatch.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedOrdinals = new bool[count];
        var entries = ImmutableArray.CreateBuilder<HddCatalogEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var field = index[(8 + 24 * i)..];
            var name = VolumeCatalogMetadata.ParseName83(field[0..11], field[11]);
            Require(name is not null, "Invalid catalog name/flags.");
            Require(names.Add(name!), "Duplicate volume name.");
            var imageBytes = BinaryPrimitives.ReadUInt32LittleEndian(field[12..16]);
            Require(imageBytes == VolumeCatalogMetadata.SlotBytes, "Invalid slot image length.");
            var ordinal = BinaryPrimitives.ReadUInt32LittleEndian(field[16..20]);
            Require(ordinal < (uint)count && !usedOrdinals[ordinal], "Invalid slot ordinal.");
            usedOrdinals[ordinal] = true;
            var imageCrc = BinaryPrimitives.ReadUInt32LittleEndian(field[20..24]);
            entries.Add(new HddCatalogEntry(name!, field[11] != 0, imageBytes, new HddSlotOrdinal(ordinal), imageCrc));
        }
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(index[^4..]);
        Require(stored == VolumeCatalogMetadata.Crc32(index[..(index.Length - 4)]), "Torn index CRC.");
        Require(stored == view.IndexCrc, "Footer/index CRC link mismatch.");
        return new HddContainerDescriptor(view, entries.ToImmutable());
    }

    /// <summary>Decodes a container's identity and index from its footer and index bytes only; arena bytes are never read or
    /// required. Entries keep writer row order; ordinals must permute 0..N-1 and the stored index CRC must equal both its
    /// recomputation and the footer's indexCRC link.</summary>
    public static TapeResult<HddContainerDescriptor> DecodeHdd(ReadOnlySpan<byte> footer, ReadOnlySpan<byte> index,
        ulong fileLength, HddLimits limits)
    {
        try { return TapeResult<HddContainerDescriptor>.Ok(Decode(footer, index, fileLength, limits)); }
        catch (Refusal e) { return TapeResult<HddContainerDescriptor>.Fail(e.Code, e.Message); }
        catch (OverflowException) { return TapeResult<HddContainerDescriptor>.Fail(TapeError.BoundsExceeded, "Container arithmetic overflow."); }
    }

    private static ImmutableArray<TapeCatalogEntry> DecodeTape(ReadOnlySpan<byte> bytes, byte sideByte, TapeLimits limits)
    {
        Require(bytes.Length >= 8, "Truncated catalog record.");
        Require(bytes[..4].SequenceEqual("HCAT"u8), "Not a catalog.");
        Require(bytes[4] == 1, "Unsupported catalog version.", TapeError.UnsupportedVersion);
        Require(bytes[5] == sideByte, "Catalog side mismatch.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]);
        var length = checked(12 + 24 * count);
        Require(bytes.Length == length, "Invalid catalog record length.");
        Require(length <= (uint)limits.InputBytes && 3L * length + 128L * count <= limits.DecodedBytes,
            "Record decoding budget exceeded.", TapeError.BoundsExceeded);
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(bytes[^4..]);
        Require(VolumeCatalogMetadata.Crc32(bytes[..(length - 4)]) == crc, "Torn catalog CRC.");
        var entries = ImmutableArray.CreateBuilder<TapeCatalogEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var field = bytes[(8 + 24 * i)..];
            var stem = Encoding.ASCII.GetString(field[..8]).TrimEnd(' ');
            var ext = Encoding.ASCII.GetString(field[8..11]).TrimEnd(' ');
            var name = stem + (ext.Length == 0 ? "" : "." + ext);
            var canonical = VolumeCatalogMetadata.CanonicalName83(name);
            Require(canonical is not null, "Invalid catalog 8.3 name.");
            Require(field[..11].SequenceEqual(canonical!) && (field[11] & 0xfe) == 0, "Invalid catalog name/flags.");
            entries.Add(new TapeCatalogEntry(name, field[11] != 0,
                BinaryPrimitives.ReadUInt32LittleEndian(field[12..16]),
                BinaryPrimitives.ReadUInt32LittleEndian(field[16..20]),
                BinaryPrimitives.ReadUInt32LittleEndian(field[20..24])));
        }
        return entries.ToImmutable();
    }

    /// <summary>Pure HCAT record parse shared with <see cref="TapeCatalogCodec"/>: header, side byte, 24-byte entries through
    /// the shared name/CRC primitives and the trailing CRC. FirstBlock keeps its tape data-block semantics
    /// (<see cref="TapeBlockIndex"/>), not an HDD slot ordinal.</summary>
    public static TapeResult<ImmutableArray<TapeCatalogEntry>> DecodeTapeIndex(ReadOnlySpan<byte> bytes, byte sideByte, TapeLimits limits)
    {
        try { return TapeResult<ImmutableArray<TapeCatalogEntry>>.Ok(DecodeTape(bytes, sideByte, limits)); }
        catch (Refusal e) { return TapeResult<ImmutableArray<TapeCatalogEntry>>.Fail(e.Code, e.Message); }
        catch (OverflowException) { return TapeResult<ImmutableArray<TapeCatalogEntry>>.Fail(TapeError.BoundsExceeded, "Container arithmetic overflow."); }
    }
}
