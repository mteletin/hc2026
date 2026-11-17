using System.Text;

namespace Hc2000.App.Explorer;

internal sealed record ExplorerPreview(string Text, string Hex, bool Truncated)
{
    internal const int MaximumBytes = 4096;

    internal static ExplorerPreview FromBytes(ReadOnlySpan<byte> bytes)
    {
        var truncated = bytes.Length > MaximumBytes;
        bytes = bytes[..Math.Min(bytes.Length, MaximumBytes)];
        var text = new StringBuilder(bytes.Length);
        var hex = new StringBuilder();
        for (var index = 0; index < bytes.Length; index++)
        {
            var value = bytes[index];
            text.Append(value is 9 or 10 or 13 || value is >= 32 and <= 126 ? (char)value : '·');
            if (index % 16 == 0)
            {
                if (index != 0) hex.AppendLine();
                hex.Append(index.ToString("X4")).Append("  ");
            }
            else hex.Append(' ');
            hex.Append(value.ToString("X2"));
        }
        return new(text.ToString(), hex.ToString(), truncated);
    }

    internal static ExplorerPreview FromHostFile(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = new byte[MaximumBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var count = file.Read(bytes, length, bytes.Length - length);
            if (count == 0) break;
            length += count;
        }
        return FromBytes(bytes.AsSpan(0, length));
    }
}
