using System.Globalization;
using System.Text;

namespace Hc2000.Asm;

public enum DiagnosticSeverity { Warning, Error }
public sealed record Diagnostic(string File, int Line, int Column, DiagnosticSeverity Severity, string Message);
public sealed record Segment(int Address, byte[] Bytes);
public sealed record ListingLine(string File, int Line, int Address, byte[] Bytes, string Source);
public sealed record BinaryOutput(string Path, int Start, int Length);

public sealed class AssemblerOptions
{
    public byte Fill { get; init; } = 0xFF;
    public IReadOnlyDictionary<string, int> Defines { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<string> IncludePaths { get; init; } = [];
}

public sealed class AssemblyResult
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; internal init; } = [];
    public IReadOnlyList<Segment> Segments { get; internal init; } = [];
    public IReadOnlyDictionary<string, int> Symbols { get; internal init; } = new Dictionary<string, int>();
    public IReadOnlyList<ListingLine> Listing { get; internal init; } = [];
    public IReadOnlyList<BinaryOutput> Outputs { get; internal init; } = [];
    public bool Success => Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);
    public byte Fill { get; internal init; } = 0xFF;

    /// <summary>Flatten physical segments, filling un-emitted addresses. Words are little endian.</summary>
    public byte[] ToBinary(int? start = null, int? length = null)
    {
        var first = start ?? (Segments.Count == 0 ? 0 : Segments.Min(s => s.Address));
        var size = length ?? (Segments.Count == 0 ? 0 : Segments.Max(s => s.Address + s.Bytes.Length) - first);
        if (first < 0 || size < 0 || (long)first + size > 65536) throw new ArgumentOutOfRangeException(nameof(length));
        var bytes = new byte[size];
        Array.Fill(bytes, Fill);
        foreach (var segment in Segments)
        {
            var from = Math.Max(first, segment.Address);
            var end = Math.Min(first + size, segment.Address + segment.Bytes.Length);
            if (end > from) Array.Copy(segment.Bytes, from - segment.Address, bytes, from - first, end - from);
        }
        return bytes;
    }

    public string ToIntelHex()
    {
        var output = new StringBuilder();
        foreach (var segment in Segments)
        for (var offset = 0; offset < segment.Bytes.Length; offset += 16)
        {
            var count = Math.Min(16, segment.Bytes.Length - offset);
            var address = segment.Address + offset;
            var sum = count + (address >> 8) + (address & 255);
            output.Append(CultureInfo.InvariantCulture, $":{count:X2}{address:X4}00");
            for (var i = 0; i < count; i++)
            {
                var b = segment.Bytes[offset + i];
                sum += b;
                output.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            output.AppendLine(((-sum) & 255).ToString("X2", CultureInfo.InvariantCulture));
        }
        output.AppendLine(":00000001FF");
        return output.ToString();
    }

    public string ToSymbolFile() => string.Join(Environment.NewLine,
        Symbols.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .Select(s => $"{s.Key} = 0x{(s.Value & 65535):X4}")) + Environment.NewLine;

    public string ToListing() => string.Join(Environment.NewLine,
        Listing.Select(l => $"{l.Address:X4} {Convert.ToHexString(l.Bytes),-24} {l.File}:{l.Line} {l.Source}")) + Environment.NewLine;
}
