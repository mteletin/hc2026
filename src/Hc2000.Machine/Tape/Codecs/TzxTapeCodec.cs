using System.Collections.Immutable;
using System.Text;
using Hc2000.Machine.Tape.Encoding;

namespace Hc2000.Machine.Tape.Codecs;

/// <summary>Native TZX1.20 reader, finite route interpreter and representability-checked writer.</summary>
public sealed partial class TzxTapeCodec(ITapeRunEncoding? runEncoding = null) : ITapeCodec
{
    private readonly ITapeRunEncoding encoding = runEncoding ?? new TapeRunEncoding();
    public TapeFormat Format => TapeFormat.Tzx;
    public TapeCodecCapabilities Capabilities => new(true, true, null);

    private static string C64Convention(TzxSourceBlock block) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(block.Body.AsSpan()) == block.Body.Length
            ? "LengthIncludesDword" : "ExtensionLengthExcludesDword";

    public TapeResult<TapeReadResult> Read(ReadOnlySpan<byte> bytes, TapeImportOptions options)
    {
        var parsed = TzxDocument.Parse(bytes, options.Limits);
        if (!parsed.Success) return TapeResult<TapeReadResult>.Fail(parsed.Failure!.Code, parsed.Failure.Detail);
        try
        {
            if (options.Format != TapeFormat.Tzx) throw new TzxException(TapeError.UnsupportedFormat, "TZX codec requires TZX options.");
            if (options.Limits.RouteSteps < 0 || options.Limits.VirtualTransitions < 0 || options.Limits.DecodedBytes < 0)
                throw new TzxException(TapeError.BoundsExceeded, "Invalid TZX resource policy.");
            var builder = new TzxSignalBuilder(options.Limits);
            builder.Reserve(2L * bytes.Length); // Preserved original bytes plus framed source blocks.
            var annotations = new List<TapeAnnotation>();
            var markers = new List<TapeStopMarker>();
            var visits = new List<TapeRouteVisit>();
            var warnings = new List<TapeWarning>();
            const string source = "tzx-source-0";
            var blocks = parsed.Value.Blocks;
            int pc = 0, loopStart = -1, loopLeft = 0, callBase = -1, callNext = 0;
            short[] calls = [];
            uint occurrence = 0;
            var choices = new Dictionary<uint, uint>();
            foreach (var selection in options.Selections)
                if (!choices.TryAdd(selection.Occurrence, selection.Option))
                    throw new TzxException(TapeError.InvalidRoute, "Duplicate TZX selection occurrence.");
            var used = new HashSet<uint>();
            while (pc < blocks.Length)
            {
                if (pc < 0) throw new TzxException(TapeError.InvalidRoute, "TZX jump before first block.");
                if (visits.Count >= options.Limits.RouteSteps)
                    throw new TzxException(TapeError.BoundsExceeded, "TZX finite route-work budget exceeded.");
                var block = blocks[pc];
                builder.Reserve(48);
                visits.Add(new(source, (uint)pc, (uint)visits.Count));
                var c = new TzxCursor(block.Body.ToArray());
                int next = pc + 1;
                void Note(string label, TapeAnnotationKind kind = TapeAnnotationKind.Metadata)
                {
                    builder.Reserve(96L + label.Length * 2L);
                    annotations.Add(new(builder.Position, (uint)annotations.Count, kind, label, source, (uint)pc));
                }
                void Stop(string label)
                {
                    builder.Reserve(96L + label.Length * 2L);
                    markers.Add(new(builder.Position, (uint)markers.Count, label, source, (uint)pc));
                }
                int Target(int relative)
                {
                    int target = checked(pc + relative);
                    if (target < 0 || target >= blocks.Length)
                        throw new TzxException(TapeError.InvalidRoute, "TZX relative target outside source blocks.");
                    return target;
                }
                switch (block.Id)
                {
                    case 0x10:
                    {
                        uint pause = c.Word(); var data = c.Take(c.Word());
                        if (data.Length == 0) throw new TzxException(TapeError.InvalidFormat, "TZX standard data has no flag byte.");
                        builder.Tone(2168, data[0] < 128 ? 8063U : 3223U);
                        builder.Pulse(667); builder.Pulse(735); builder.Data(data, 8, 855, 1710); builder.Pause(pause); break;
                    }
                    case 0x11:
                    {
                        uint pilot = c.Word(), sync1 = c.Word(), sync2 = c.Word(), zero = c.Word(), one = c.Word(), count = c.Word();
                        int bits = c.Byte(); uint pause = c.Word(); var data = c.Take(c.UInt24());
                        builder.Tone(pilot, count); builder.Pulse(sync1); builder.Pulse(sync2);
                        builder.Data(data, bits, zero, one); builder.Pause(pause); break;
                    }
                    case 0x12: { uint width = c.Word(); builder.Tone(width, c.Word()); break; }
                    case 0x13: { int n = c.Byte(); for (int i = 0; i < n; i++) builder.Pulse(c.Word()); break; }
                    case 0x14:
                    {
                        uint zero = c.Word(), one = c.Word(); int bits = c.Byte(); uint pause = c.Word();
                        builder.Data(c.Take(c.UInt24()), bits, zero, one); builder.Pause(pause); break;
                    }
                    case 0x15:
                    {
                        uint period = c.Word(), pause = c.Word(); int bits = c.Byte(); var data = c.Take(c.UInt24());
                        if (period == 0 || bits is < 1 or > 8) throw new TzxException(TapeError.InvalidFormat, "Invalid TZX direct sample period/bits.");
                        if ((data.Length == 0 ? 0 : ((long)data.Length - 1) * 8 + bits) > options.Limits.VirtualTransitions)
                            throw new TzxException(TapeError.BoundsExceeded, "TZX direct sample work exceeds budget.");
                        for (int i = 0; i < data.Length; i++)
                            for (int j = 0; j < (i == data.Length - 1 ? bits : 8); j++)
                                builder.Hold((data[i] & 1 << (7 - j)) != 0, new(period, 3_500_000));
                        builder.Pause(pause); break;
                    }
                    case 0x16: C64Rom(c, builder); Note($"DeprecatedC64Rom:{C64Convention(block)}"); break;
                    case 0x17: C64Turbo(c, builder); Note($"DeprecatedC64Turbo:{C64Convention(block)}"); break;
                    case 0x18:
                    {
                        c.Dword(); uint pause = c.Word(); uint rate = (uint)c.UInt24(); byte compression = c.Byte(); uint count = c.Dword();
                        if (rate == 0) throw new TzxException(TapeError.InvalidFormat, "TZX CSW rate is zero.");
                        var payload = c.Take(c.Remaining);
                        var decoded = compression switch {
                            1 => encoding.DecodeRle(payload, count, options.Limits),
                            2 => encoding.DecodeZrle(payload, count, options.Limits),
                            _ => TapeResult<ImmutableArray<uint>>.Fail(TapeError.UnsupportedCompression, "Unsupported TZX CSW compression.") };
                        if (!decoded.Success) throw new TzxException(decoded.Failure!.Code, decoded.Failure.Detail);
                        foreach (uint samples in decoded.Value) { builder.Hold(builder.Level, new(samples, rate)); builder.Level = !builder.Level; }
                        if (decoded.Value.Length != 0) builder.Level = !builder.Level; // CSW current level is last played, not next pulse.
                        builder.Pause(pause); break;
                    }
                    case 0x19: Generalized(c, builder, options.Limits); break;
                    case 0x20: { uint pause = c.Word(); if (pause == 0) Stop("TZX stop"); else builder.Pause(pause); break; }
                    case 0x21: Note(System.Text.Encoding.Latin1.GetString(c.Take(c.Byte())), TapeAnnotationKind.Text); break;
                    case 0x22: Note("GroupEnd"); break;
                    case 0x23: next = Target(c.SignedWord()); break;
                    case 0x24:
                        if (loopStart >= 0) throw new TzxException(TapeError.InvalidRoute, "Nested TZX loops are forbidden.");
                        loopStart = next; loopLeft = c.Word();
                        if (loopLeft < 2) throw new TzxException(TapeError.InvalidRoute, "TZX loop count must exceed one.");
                        break;
                    case 0x25:
                        if (loopStart < 0) throw new TzxException(TapeError.InvalidRoute, "Unmatched TZX loop end.");
                        if (--loopLeft > 0) next = loopStart; else loopStart = -1;
                        break;
                    case 0x26:
                        if (callBase >= 0) throw new TzxException(TapeError.InvalidRoute, "Nested TZX calls are forbidden.");
                        calls = new short[c.Word()]; for (int i = 0; i < calls.Length; i++) calls[i] = c.SignedWord();
                        foreach (short relative in calls) Target(relative);
                        if (calls.Length != 0) { callBase = pc; callNext = 1; next = Target(calls[0]); }
                        break;
                    case 0x27:
                        if (callBase < 0) throw new TzxException(TapeError.InvalidRoute, "Unmatched TZX return.");
                        if (callNext < calls.Length) next = callBase + calls[callNext++];
                        else { next = callBase + 1; callBase = -1; }
                        break;
                    case 0x28:
                    {
                        c.Word(); int n = c.Byte();
                        var offsets = new short[n]; var labels = new string[n];
                        for (int i = 0; i < n; i++) { offsets[i] = c.SignedWord(); labels[i] = System.Text.Encoding.Latin1.GetString(c.Take(c.Byte())); Target(offsets[i]); }
                        if (!choices.TryGetValue(occurrence, out uint option))
                            throw new TzxException(TapeError.RouteSelectionRequired, $"Selection occurrence {occurrence}: {string.Join(" | ", labels)}");
                        if (option >= n) throw new TzxException(TapeError.InvalidRoute, "TZX selection option outside list.");
                        used.Add(occurrence++); next = Target(offsets[option]); Note($"Selected: {labels[option]}"); break;
                    }
                    case 0x2A:
                        if (c.Dword() != 0) throw new TzxException(TapeError.InvalidFormat, "TZX48K stop length must be zero.");
                        if (options.Target48K) Stop("TZX 48K stop"); break;
                    case 0x2B:
                        if (c.Dword() != 1) throw new TzxException(TapeError.InvalidFormat, "TZX level length must be one.");
                        byte level = c.Byte(); if (level > 1) throw new TzxException(TapeError.InvalidFormat, "Invalid TZX level.");
                        builder.Level = level != 0; Note($"SetLevel:{level}"); break;
                    case 0x30: Note(System.Text.Encoding.Latin1.GetString(c.Take(c.Byte())), TapeAnnotationKind.Text); break;
                    case 0x31: { byte seconds = c.Byte(); Note($"Message({seconds}s): {System.Text.Encoding.Latin1.GetString(c.Take(c.Byte()))}", TapeAnnotationKind.Text); break; }
                    case 0x32:
                    {
                        c.Word(); int n = c.Byte();
                        for (int i = 0; i < n; i++) { byte type = c.Byte(); Note($"Archive({type}): {System.Text.Encoding.Latin1.GetString(c.Take(c.Byte()))}"); }
                        break;
                    }
                    case 0x33:
                    {
                        int n = c.Byte(); for (int i = 0; i < n; i++) Note($"Hardware:{c.Byte()}/{c.Byte()}/{c.Byte()}"); break;
                    }
                    case 0x34: c.Skip(8); Note("UnsupportedEmulationAction", TapeAnnotationKind.UnsupportedAction); warnings.Add(new("UnsupportedEmulationAction", "Emulation requests are not executed.")); break;
                    case 0x35:
                    {
                        string identifier = System.Text.Encoding.Latin1.GetString(c.Take(16)); c.Skip(c.Length32()); Note($"CustomInfo:{identifier}"); break;
                    }
                    case 0x40: c.Byte(); c.Skip(c.UInt24()); Note("UnsupportedSnapshotAction", TapeAnnotationKind.UnsupportedAction); warnings.Add(new("UnsupportedSnapshotAction", "Embedded CPU snapshots are never restored.")); break;
                    case 0x5A:
                        if (!c.Take(7).AsSpan().SequenceEqual("XTape!\x1a"u8)) throw new TzxException(TapeError.InvalidFormat, "Invalid concatenation glue.");
                        if (c.Byte() != 1 || c.Byte() > 20) throw new TzxException(TapeError.UnsupportedVersion, "Unsupported concatenated TZX version.");
                        Note("ConcatenationGlue"); break;
                    default: throw new TzxException(TapeError.UnsupportedFormat, $"Unknown TZX block {block.Id:X2} has unsupported signal semantics.");
                }
                c.End(); pc = next;
            }
            if (loopStart >= 0 || callBase >= 0) throw new TzxException(TapeError.InvalidRoute, "Unterminated TZX loop/call.");
            if (used.Count != choices.Count) throw new TzxException(TapeError.InvalidRoute, "Unused TZX route selections.");
            var sourceDuration = builder.Position;
            TapeTime capacity = new(options.Capacity == TapeCapacity.C60 ? 1800 : 2700, 1);
            if (TzxTime.Compare(sourceDuration, capacity) > 0) throw new TzxException(TapeError.CapacityExceeded, "TZX route exceeds selected cassette capacity.");
            annotations.Add(new(sourceDuration, (uint)annotations.Count, TapeAnnotationKind.SourceEof, "source-eof", null, null));
            // Capacity padding is not source expansion and may exceed a caller's source-duration limit.
            var chunks = builder.Chunks.ToList();
            if (sourceDuration != capacity) chunks.Add(new(1, [new(false, TzxTime.Subtract(capacity, sourceDuration))]));
            var image = new TapeSideImage(capacity, sourceDuration, chunks, markers, annotations, visits,
                new(TapeFormat.Tzx, null, options.Target48K, options.Selections, null));
            return TapeResult<TapeReadResult>.Ok(new(image, [new(source, TapeFormat.Tzx, bytes)], warnings.ToImmutableArray()));
        }
        catch (TzxException e) { return TapeResult<TapeReadResult>.Fail(e.Error, e.Message); }
        catch (OverflowException) { return TapeResult<TapeReadResult>.Fail(TapeError.BoundsExceeded, "TZX integer/rational overflow."); }
    }
}
