using System.IO.Compression;

namespace Hc2000.XvfbHarness;

/// <summary>
/// Minimal PNG writer, so a captured frame can be opened in any image reader without adding an
/// imaging dependency to the repo. Converts the XImage ZPixmap rows (server byte order, masks from
/// the visual) to 8-bit RGB scanlines with filter byte 0, then one zlib stream.
/// </summary>
internal static class Png
{
    public static void Write(string path, byte[] pixels, int width, int height,
        int bitsPerPixel, uint redMask, uint greenMask, uint blueMask, int bytesPerLine)
    {
        var stride = 1 + width * 3;
        var raw = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            raw[y * stride] = 0;
            for (var x = 0; x < width; x++)
            {
                var o = y * bytesPerLine + x * (bitsPerPixel / 8);
                var v = bitsPerPixel == 32
                    ? BitConverter.ToUInt32(pixels, o)
                    : (uint)(pixels[o] | (pixels[o + 1] << 8));
                var p = y * stride + 1 + x * 3;
                raw[p] = (byte)((v & redMask) >> Trailing(redMask));
                raw[p + 1] = (byte)((v & greenMask) >> Trailing(greenMask));
                raw[p + 2] = (byte)((v & blueMask) >> Trailing(blueMask));
            }
        }

        using var deflated = new MemoryStream();
        deflated.Write([0x78, 0x01]);
        using (var d = new DeflateStream(deflated, CompressionLevel.Fastest, true)) d.Write(raw);
        deflated.Write(Adler32(raw));

        using var f = File.Create(path);
        f.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(f, "IHDR", Concat(Be(width), Be(height), [8, 2, 0, 0, 0]));
        WriteChunk(f, "IDAT", deflated.ToArray());
        WriteChunk(f, "IEND", []);
    }

    static int Trailing(uint mask)
    {
        var n = 0;
        while (n < 32 && ((mask >> n) & 1) == 0) n++;
        return n;
    }

    static byte[] Be(int v) { var b = BitConverter.GetBytes(v); Array.Reverse(b); return b; }
    static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    static void WriteChunk(Stream s, string type, byte[] data)
    {
        s.Write(Be(data.Length));
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        s.Write(Be((int)Crc32(t.Concat(data).ToArray())));
    }

    static uint Crc32(byte[] b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in b)
        {
            c ^= x;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }

    static byte[] Adler32(byte[] b)
    {
        uint a = 1, s = 0;
        foreach (var x in b) { a = (a + x) % 65521; s = (s + a) % 65521; }
        return Be((int)((s << 16) | a));
    }
}
