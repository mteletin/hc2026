namespace Hc2000.Machine.Expansion;

/// <summary>
/// Bounded indexed back surface used by X2 rendering commands. Supports 320x240x256 (Mode 0),
/// 640x480x256 (Mode 1), and 1280x1024x16 (Mode 2).
/// </summary>
internal sealed class ExpansionX2Surface
{
    public const int Width = 320;
    public const int Height = 240;
    public const int PaletteSize = 256;

    private static readonly uint[] StandardPalette =
    [
        0xFF000000u, // 0: Black
        0xFF0000D7u, // 1: Blue
        0xFFD70000u, // 2: Red
        0xFFD700D7u, // 3: Magenta
        0xFF00D700u, // 4: Green
        0xFF00D7D7u, // 5: Cyan
        0xFFD7D700u, // 6: Yellow
        0xFFD7D7D7u, // 7: White
        0xFF000000u, // 8: Bright Black
        0xFF0000FFu, // 9: Bright Blue
        0xFFFF0000u, // 10: Bright Red
        0xFFFF00FFu, // 11: Bright Magenta
        0xFF00FF00u, // 12: Bright Green
        0xFF00FFFFu, // 13: Bright Cyan
        0xFFFFFF00u, // 14: Bright Yellow
        0xFFFFFFFFu, // 15: Bright White
    ];

    private readonly Memory<byte> mode0Pixels;
    private byte[]? mode1Pixels;
    private byte[]? mode2Pixels;
    private Memory<byte> currentPixels;
    private readonly uint[] palette = new uint[PaletteSize];

    public int Mode { get; private set; }
    public int ActiveWidth => Mode switch { 1 => 640, 2 => 1280, _ => 320 };
    public int ActiveHeight => Mode switch { 1 => 480, 2 => 1024, _ => 240 };
    public int Bpp => Mode switch { 2 => 4, _ => 8 };
    public Memory<byte> CurrentPixels => currentPixels;

    public ExpansionX2Surface(Memory<byte> pixels)
    {
        if (pixels.Length != Width * Height) throw new ArgumentException("X2 plane size.", nameof(pixels));
        mode0Pixels = pixels;
        currentPixels = pixels;
        Array.Fill(palette, 0xff000000u);
        StandardPalette.CopyTo(palette.AsSpan(0, 16));
    }

    public bool SetMode(int mode)
    {
        if (mode is < 0 or > 2) return false;
        Mode = mode;
        if (mode == 0)
        {
            currentPixels = mode0Pixels;
        }
        else if (mode == 1)
        {
            mode1Pixels ??= new byte[640 * 480];
            currentPixels = mode1Pixels;
        }
        else
        {
            mode2Pixels ??= new byte[1280 * 1024 / 2];
            currentPixels = mode2Pixels;
        }
        return true;
    }

    public void Clear(byte colour)
    {
        if (Mode == 2)
        {
            var p = (byte)(colour & 0x0F);
            currentPixels.Span.Fill((byte)((p << 4) | p));
        }
        else
        {
            currentPixels.Span.Fill(colour);
        }
    }

    public bool FillRectangle(int x, int y, int width, int height, byte colour)
    {
        if (!ValidRectangle(x, y, width, height)) return false;
        if (Mode == 2)
        {
            var val = (byte)(colour & 0x0F);
            var stride = ActiveWidth / 2;
            for (var row = 0; row < height; row++)
            {
                var py = y + row;
                for (var col = 0; col < width; col++)
                {
                    var px = x + col;
                    var idx = py * stride + (px >> 1);
                    if ((px & 1) == 0)
                        currentPixels.Span[idx] = (byte)((currentPixels.Span[idx] & 0x0F) | (val << 4));
                    else
                        currentPixels.Span[idx] = (byte)((currentPixels.Span[idx] & 0xF0) | val);
                }
            }
            return true;
        }

        for (var row = 0; row < height; row++)
            currentPixels.Span.Slice((y + row) * ActiveWidth + x, width).Fill(colour);
        return true;
    }

    public bool CopyRectangle(int sourceX, int sourceY, int destinationX, int destinationY, int width, int height)
    {
        if (!ValidRectangle(sourceX, sourceY, width, height) ||
            !ValidRectangle(destinationX, destinationY, width, height)) return false;

        if (Mode == 2)
        {
            var stride = ActiveWidth / 2;
            if ((sourceX & 1) == 0 && (destinationX & 1) == 0 && (width & 1) == 0)
            {
                var byteW = width / 2;
                var source = new byte[byteW * height];
                for (var row = 0; row < height; row++)
                    currentPixels.Span.Slice((sourceY + row) * stride + (sourceX / 2), byteW).CopyTo(source.AsSpan(row * byteW, byteW));
                for (var row = 0; row < height; row++)
                    source.AsSpan(row * byteW, byteW).CopyTo(currentPixels.Span.Slice((destinationY + row) * stride + (destinationX / 2), byteW));
                return true;
            }

            // Pixel-by-pixel fallback for unaligned 4bpp
            var temp = new byte[width * height];
            for (var row = 0; row < height; row++)
                for (var col = 0; col < width; col++)
                {
                    var px = sourceX + col;
                    var py = sourceY + row;
                    var b = currentPixels.Span[py * stride + (px >> 1)];
                    temp[row * width + col] = (px & 1) == 0 ? (byte)((b >> 4) & 0x0F) : (byte)(b & 0x0F);
                }
            for (var row = 0; row < height; row++)
                for (var col = 0; col < width; col++)
                {
                    var px = destinationX + col;
                    var py = destinationY + row;
                    var idx = py * stride + (px >> 1);
                    var val = temp[row * width + col];
                    if ((px & 1) == 0)
                        currentPixels.Span[idx] = (byte)((currentPixels.Span[idx] & 0x0F) | (val << 4));
                    else
                        currentPixels.Span[idx] = (byte)((currentPixels.Span[idx] & 0xF0) | val);
                }
            return true;
        }

        var source8 = new byte[width * height];
        for (var row = 0; row < height; row++)
            currentPixels.Span.Slice((sourceY + row) * ActiveWidth + sourceX, width).CopyTo(source8.AsSpan(row * width, width));
        for (var row = 0; row < height; row++)
            source8.AsSpan(row * width, width).CopyTo(currentPixels.Span.Slice((destinationY + row) * ActiveWidth + destinationX, width));
        return true;
    }

    public bool DrawGlyph(int x, int y, uint rows, byte foreground, byte background)
    {
        if (!ValidRectangle(x, y, 4, 8)) return false;
        for (var row = 0; row < 8; row++)
        {
            var bits = (rows >> (row * 4)) & 0xf;
            for (var column = 0; column < 4; column++)
            {
                var colour = (bits & (8u >> column)) != 0 ? foreground : background;
                SetPixel(x + column, y + row, colour);
            }
        }
        return true;
    }

    public void DrawConsoleGlyph(int x, int y, int fontW, int fontH, ReadOnlySpan<byte> bitmap, byte fg, byte bg)
    {
        if (Mode == 2)
        {
            var stride = ActiveWidth / 2;
            var byteOffset = y * stride + (x / 2);
            var fg4 = (byte)(fg & 0x0F);
            var bg4 = (byte)(bg & 0x0F);
            for (var r = 0; r < fontH; r++)
            {
                var rowBits = bitmap[r * 8 / fontH];
                var lineOffset = (y + r) * stride + (x / 2);
                for (var pair = 0; pair < 4; pair++)
                {
                    var b0 = (rowBits & (0x80 >> (2 * pair))) != 0;
                    var b1 = (rowBits & (0x80 >> (2 * pair + 1))) != 0;
                    var p0 = b0 ? fg4 : bg4;
                    var p1 = b1 ? fg4 : bg4;
                    currentPixels.Span[lineOffset + pair] = (byte)((p0 << 4) | p1);
                }
            }
            return;
        }

        for (var r = 0; r < fontH; r++)
        {
            var rowBits = bitmap[r * 8 / fontH];
            var lineOffset = (y + r) * ActiveWidth + x;
            for (var b = 0; b < fontW; b++)
            {
                currentPixels.Span[lineOffset + b] = (rowBits & (0x80 >> b)) != 0 ? fg : bg;
            }
        }
    }

    private void SetPixel(int x, int y, byte colour)
    {
        if (Mode == 2)
        {
            var idx = y * (ActiveWidth / 2) + (x >> 1);
            var val = (byte)(colour & 0x0F);
            if ((x & 1) == 0)
                currentPixels.Span[idx] = (byte)((currentPixels.Span[idx] & 0x0F) | (val << 4));
            else
                currentPixels.Span[idx] = (byte)((currentPixels.Span[idx] & 0xF0) | val);
        }
        else
        {
            currentPixels.Span[y * ActiveWidth + x] = colour;
        }
    }

    public bool ScrollUp(int x, int y, int width, int height, int rows, byte background)
    {
        if (!ValidRectangle(x, y, width, height) || rows <= 0 || rows > height) return false;
        if (rows < height && !CopyRectangle(x, y + rows, x, y, width, height - rows)) return false;
        return FillRectangle(x, y + height - rows, width, rows, background);
    }

    public bool SetPalette(int index, byte red, byte green, byte blue)
    {
        if ((uint)index >= PaletteSize) return false;
        palette[index] = 0xff000000u | (uint)red << 16 | (uint)green << 8 | blue;
        return true;
    }

    public ExpansionX2SurfaceState Capture() => new(currentPixels.ToArray(), (uint[])palette.Clone(), Mode);
    internal uint[] CapturePalette() => (uint[])palette.Clone();

    public bool TryRestore(ExpansionX2SurfaceState state)
    {
        if (!ValidState(state)) return false;
        SetMode(state.Mode);
        state.Pixels.CopyTo(currentPixels.Span);
        state.Palette.CopyTo(palette, 0);
        return true;
    }

    internal static bool ValidState(ExpansionX2SurfaceState? state) =>
        state is not null && state.Palette is { Length: PaletteSize } &&
        state.Palette.All(colour => (colour >> 24) == 0xff) &&
        (state.Mode switch
        {
            1 => state.Pixels is { Length: 640 * 480 },
            2 => state.Pixels is { Length: 1280 * 1024 / 2 },
            _ => state.Pixels is { Length: Width * Height }
        });

    public bool ValidRectangle(int x, int y, int width, int height) =>
        x >= 0 && y >= 0 && width > 0 && height > 0 && width <= ActiveWidth && height <= ActiveHeight &&
        x <= ActiveWidth - width && y <= ActiveHeight - height;
}

internal sealed record ExpansionX2SurfaceState(byte[] Pixels, uint[] Palette, int Mode = 0);
