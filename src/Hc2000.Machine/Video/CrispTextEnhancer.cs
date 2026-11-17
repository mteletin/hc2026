namespace Hc2000.Machine.Video;

/// <summary>
/// Opt-in crisp text presentation enhancer for HC2000 and ZX Spectrum frames.
/// Recognizes supported 32-column BASIC and 64-column CP/M known ROM fonts,
/// rendering high-resolution typography with conservative faithful fallback
/// for unmatched cells, coincident graphics, and user sprites.
/// </summary>
public static class CrispTextEnhancer
{
    public const int FrameWidth = 352;
    public const int FrameHeight = 296;
    public const int EnhancedWidth = 704;
    public const int EnhancedHeight = 592;

    public const int DisplayOriginX = 48;
    public const int DisplayOriginY = 40;
    public const int DisplayCols = 32;
    public const int DisplayRows = 24;

    public const int TargetDisplayOriginX = 96;
    public const int TargetDisplayOriginY = 80;
    public const int SplitDividerX = 352;

    private enum FrameMode { Basic, Cpm }

    /// <summary>
    /// Enhances a frame according to the given presentation mode.
    /// Output buffer must have at least 704 * 592 uint elements.
    /// </summary>
    public static void Enhance(VideoFrame frame, PresentationMode mode, Span<uint> output)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width != FrameWidth || frame.Height != FrameHeight || frame.Pixels.Length != FrameWidth * FrameHeight)
            throw new ArgumentException($"Expected {FrameWidth}x{FrameHeight} frame.", nameof(frame));
        if (output.Length < EnhancedWidth * EnhancedHeight)
            throw new ArgumentException($"Output buffer requires {EnhancedWidth * EnhancedHeight} elements.", nameof(output));

        var src = frame.Pixels.Span;

        // If faithful mode: 2x nearest-neighbor expansion of base frame
        if (mode == PresentationMode.Faithful)
        {
            for (int y = 0; y < EnhancedHeight; y++)
            {
                int srcY = y / 2;
                int rowOffset = y * EnhancedWidth;
                int srcRowOffset = srcY * FrameWidth;
                for (int x = 0; x < EnhancedWidth; x++)
                {
                    output[rowOffset + x] = src[srcRowOffset + (x / 2)];
                }
            }
            return;
        }

        // Determine dominant frame font mode (CP/M 64-col vs BASIC 32-col)
        var dominantMode = DetectDominantMode(src);

        // 1. Render all border regions faithfully
        for (int y = 0; y < EnhancedHeight; y++)
        {
            int srcY = y / 2;
            int rowOffset = y * EnhancedWidth;
            int srcRowOffset = srcY * FrameWidth;

            bool isTopOrBottomBorder = y < TargetDisplayOriginY || y >= TargetDisplayOriginY + DisplayRows * 16;
            if (isTopOrBottomBorder)
            {
                for (int x = 0; x < EnhancedWidth; x++)
                    output[rowOffset + x] = src[srcRowOffset + (x / 2)];
            }
            else
            {
                // Left border
                for (int x = 0; x < TargetDisplayOriginX; x++)
                    output[rowOffset + x] = src[srcRowOffset + (x / 2)];
                // Right border
                for (int x = TargetDisplayOriginX + DisplayCols * 16; x < EnhancedWidth; x++)
                    output[rowOffset + x] = src[srcRowOffset + (x / 2)];
            }
        }

        // 2. Render 24 rows x 32 columns of display cells
        Span<byte> bits = stackalloc byte[8];
        Span<byte> leftNibbles = stackalloc byte[8];
        Span<byte> rightNibbles = stackalloc byte[8];

        for (int r = 0; r < DisplayRows; r++)
        {
            int cellSrcY = DisplayOriginY + r * 8;
            int cellTargetY = TargetDisplayOriginY + r * 16;

            for (int c = 0; c < DisplayCols; c++)
            {
                int cellSrcX = DisplayOriginX + c * 8;
                int cellTargetX = TargetDisplayOriginX + c * 16;

                // Split comparison: columns 0..15 (left half) render faithful 2x2 blocks
                if (mode == PresentationMode.SplitComparison && c < 16)
                {
                    RenderFaithfulCell(src, cellSrcX, cellSrcY, cellTargetX, cellTargetY, output);
                    continue;
                }

                // Analyze cell colors
                if (!ExtractCellColors(src, cellSrcX, cellSrcY, out uint color0, out uint color1, out int count0, out int count1))
                {
                    // More than 2 colors: faithful fallback
                    RenderFaithfulCell(src, cellSrcX, cellSrcY, cellTargetX, cellTargetY, output);
                    continue;
                }

                // Single-color blank cell
                if (count1 == 0)
                {
                    FillCell(color0, cellTargetX, cellTargetY, output);
                    continue;
                }

                // 2 colors: determine paper (background) vs ink (foreground)
                uint paper = count0 >= count1 ? color0 : color1;
                uint ink = count0 >= count1 ? color1 : color0;

                // Extract 8x8 bit pattern (1 = ink, 0 = paper)
                for (int dy = 0; dy < 8; dy++)
                {
                    byte rowBits = 0;
                    int rowOffset = (cellSrcY + dy) * FrameWidth + cellSrcX;
                    for (int dx = 0; dx < 8; dx++)
                    {
                        if (src[rowOffset + dx] == ink)
                            rowBits |= (byte)(0x80 >> dx);
                    }
                    bits[dy] = rowBits;
                    leftNibbles[dy] = (byte)((rowBits >> 4) & 0x0F);
                    rightNibbles[dy] = (byte)(rowBits & 0x0F);
                }

                // Match against declared fonts
                bool rendered = false;
                if (dominantMode == FrameMode.Cpm)
                {
                    if (CrispFontCatalog.MatchCpm(leftNibbles, rightNibbles, out char chLeft, out char chRight))
                    {
                        RenderCpmPair(chLeft, chRight, ink, paper, cellTargetX, cellTargetY, output);
                        rendered = true;
                    }
                    else if (CrispFontCatalog.MatchBasic(bits, out char chB, out bool invB))
                    {
                        RenderBasicGlyph(chB, invB, ink, paper, cellTargetX, cellTargetY, output);
                        rendered = true;
                    }
                }
                else
                {
                    if (CrispFontCatalog.MatchBasic(bits, out char chB, out bool invB))
                    {
                        RenderBasicGlyph(chB, invB, ink, paper, cellTargetX, cellTargetY, output);
                        rendered = true;
                    }
                    else if (CrispFontCatalog.MatchCpm(leftNibbles, rightNibbles, out char chLeft, out char chRight))
                    {
                        RenderCpmPair(chLeft, chRight, ink, paper, cellTargetX, cellTargetY, output);
                        rendered = true;
                    }
                }

                // Conservative fallback: unmatched or ambiguous cells use faithful 2x2 rendering
                if (!rendered)
                {
                    RenderFaithfulCell(src, cellSrcX, cellSrcY, cellTargetX, cellTargetY, output);
                }
            }
        }

        // 3. In SplitComparison mode, draw clean 1-pixel divider at X = SplitDividerX (352)
        if (mode == PresentationMode.SplitComparison)
        {
            const uint dividerColor = 0xFF555555;
            for (int y = 0; y < EnhancedHeight; y++)
            {
                output[y * EnhancedWidth + SplitDividerX] = dividerColor;
            }
        }
    }

    private static FrameMode DetectDominantMode(ReadOnlySpan<uint> src)
    {
        int basicMatches = 0;
        int cpmMatches = 0;
        Span<byte> bits = stackalloc byte[8];
        Span<byte> leftNibbles = stackalloc byte[8];
        Span<byte> rightNibbles = stackalloc byte[8];

        for (int r = 0; r < DisplayRows; r++)
        {
            int cellSrcY = DisplayOriginY + r * 8;
            for (int c = 0; c < DisplayCols; c++)
            {
                int cellSrcX = DisplayOriginX + c * 8;
                if (!ExtractCellColors(src, cellSrcX, cellSrcY, out uint color0, out uint color1, out int count0, out int count1))
                    continue;
                if (count1 == 0) continue; // blank cell

                uint ink = count0 >= count1 ? color1 : color0;
                for (int dy = 0; dy < 8; dy++)
                {
                    byte rowBits = 0;
                    int rowOffset = (cellSrcY + dy) * FrameWidth + cellSrcX;
                    for (int dx = 0; dx < 8; dx++)
                    {
                        if (src[rowOffset + dx] == ink)
                            rowBits |= (byte)(0x80 >> dx);
                    }
                    bits[dy] = rowBits;
                    leftNibbles[dy] = (byte)((rowBits >> 4) & 0x0F);
                    rightNibbles[dy] = (byte)(rowBits & 0x0F);
                }

                if (CrispFontCatalog.MatchBasic(bits, out char chB, out _) && chB != ' ')
                    basicMatches++;
                if (CrispFontCatalog.MatchCpm(leftNibbles, rightNibbles, out char chL, out char chR) && (chL != ' ' || chR != ' '))
                    cpmMatches++;
            }
        }

        return cpmMatches > basicMatches ? FrameMode.Cpm : FrameMode.Basic;
    }

    private static bool ExtractCellColors(ReadOnlySpan<uint> src, int cellSrcX, int cellSrcY,
        out uint color0, out uint color1, out int count0, out int count1)
    {
        color0 = src[cellSrcY * FrameWidth + cellSrcX];
        color1 = 0;
        count0 = 0;
        count1 = 0;
        bool hasColor1 = false;

        for (int dy = 0; dy < 8; dy++)
        {
            int rowOffset = (cellSrcY + dy) * FrameWidth + cellSrcX;
            for (int dx = 0; dx < 8; dx++)
            {
                uint p = src[rowOffset + dx];
                if (p == color0)
                {
                    count0++;
                }
                else if (!hasColor1)
                {
                    color1 = p;
                    hasColor1 = true;
                    count1 = 1;
                }
                else if (p == color1)
                {
                    count1++;
                }
                else
                {
                    // 3 or more colors
                    return false;
                }
            }
        }
        return true;
    }

    private static void RenderFaithfulCell(ReadOnlySpan<uint> src, int cellSrcX, int cellSrcY,
        int cellTargetX, int cellTargetY, Span<uint> output)
    {
        for (int dy = 0; dy < 16; dy++)
        {
            int targetRow = (cellTargetY + dy) * EnhancedWidth + cellTargetX;
            int srcRow = (cellSrcY + dy / 2) * FrameWidth + cellSrcX;
            for (int dx = 0; dx < 16; dx++)
            {
                output[targetRow + dx] = src[srcRow + (dx / 2)];
            }
        }
    }

    private static void FillCell(uint color, int cellTargetX, int cellTargetY, Span<uint> output)
    {
        for (int dy = 0; dy < 16; dy++)
        {
            int targetRow = (cellTargetY + dy) * EnhancedWidth + cellTargetX;
            for (int dx = 0; dx < 16; dx++)
            {
                output[targetRow + dx] = color;
            }
        }
    }

    private static void RenderBasicGlyph(char ch, bool inverted, uint ink, uint paper,
        int cellTargetX, int cellTargetY, Span<uint> output)
    {
        var glyph = CrispFontCatalog.GetBasic16x16Glyph(ch);
        uint fg = inverted ? paper : ink;
        uint bg = inverted ? ink : paper;

        for (int dy = 0; dy < 16; dy++)
        {
            ushort row = glyph[dy];
            int targetRow = (cellTargetY + dy) * EnhancedWidth + cellTargetX;
            for (int dx = 0; dx < 16; dx++)
            {
                bool bit = (row & (0x8000 >> dx)) != 0;
                output[targetRow + dx] = bit ? fg : bg;
            }
        }
    }

    private static void RenderCpmPair(char chLeft, char chRight, uint ink, uint paper,
        int cellTargetX, int cellTargetY, Span<uint> output)
    {
        var leftGlyph = CrispFontCatalog.GetCpm8x16Glyph(chLeft);
        var rightGlyph = CrispFontCatalog.GetCpm8x16Glyph(chRight);

        for (int dy = 0; dy < 16; dy++)
        {
            byte leftRow = leftGlyph[dy];
            byte rightRow = rightGlyph[dy];
            int targetRow = (cellTargetY + dy) * EnhancedWidth + cellTargetX;

            for (int dx = 0; dx < 8; dx++)
            {
                bool bit = (leftRow & (0x80 >> dx)) != 0;
                output[targetRow + dx] = bit ? ink : paper;
            }
            for (int dx = 0; dx < 8; dx++)
            {
                bool bit = (rightRow & (0x80 >> dx)) != 0;
                output[targetRow + 8 + dx] = bit ? ink : paper;
            }
        }
    }
}
