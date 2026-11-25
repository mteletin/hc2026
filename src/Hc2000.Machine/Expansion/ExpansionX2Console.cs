using System.Buffers.Binary;
using Hc2000.Machine.Video;

namespace Hc2000.Machine.Expansion;

/// <summary>
/// A single cell in the X2 text console grid.
/// </summary>
public readonly record struct TextCell(byte Character, byte Attribute, bool Inverse = false, bool Semigraphics = false);

/// <summary>
/// Hardware text console engine on the X2 VGA card. Speaks hc-term (HC2000 console control codes and
/// escape sequences of the hc-term console), rendered directly into the card's video planes at host speed.
/// </summary>
internal sealed class ExpansionX2Console
{
    private readonly ExpansionX2Engine engine;

    public int Mode { get; private set; }
    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int FontWidth => 8;
    public int FontHeight => Mode == 0 ? 8 : 16;

    public int CursorRow { get; internal set; }
    public int CursorCol { get; internal set; }
    public byte Attribute { get; internal set; } = 0x38; // Default: black on white (ink 0, paper 7), as the HC console's
    public bool CursorVisible { get; internal set; } = true;
    public bool Inverse { get; internal set; }
    public bool Semigraphics { get; internal set; }
    public byte BorderColour { get; internal set; }

    public int WindowLeft { get; internal set; }
    public int WindowTop { get; internal set; }
    public int WindowRight { get; internal set; }
    public int WindowBottom { get; internal set; }

    private TextCell[] cells;
    private int escapeState; // 0 = normal, 1 = ESC received, 2 = expecting single arg (I, P, Q, R), 3 = expecting cursor row/col
    private byte pendingEscCode;
    private int pendingRow = -1;

    public event Action? OnBell;

    public ExpansionX2Console(ExpansionX2Engine engine)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        SetGeometry(0);
        cells = new TextCell[Columns * Rows];
        for (var i = 0; i < cells.Length; i++)
            cells[i] = new TextCell((byte)' ', Attribute, false, false);
    }

    internal void SetMode(int mode)
    {
        SetGeometry(mode);
        cells = new TextCell[Columns * Rows];
        for (var i = 0; i < cells.Length; i++)
            cells[i] = new TextCell((byte)' ', 0x38, false, false);
        Attribute = 0x38;
        Inverse = false;
        Semigraphics = false;
        escapeState = 0;
        pendingRow = -1;
    }

    private void SetGeometry(int mode)
    {
        Mode = mode;
        switch (mode)
        {
            case 1:
                Columns = 80;
                Rows = 30;
                break;
            case 2:
                Columns = 160;
                Rows = 64;
                break;
            default:
                Columns = 40;
                Rows = 30;
                break;
        }
        WindowLeft = 0;
        WindowTop = 0;
        WindowRight = Columns - 1;
        WindowBottom = Rows - 1;
        CursorRow = 0;
        CursorCol = 0;
    }

    public TextCell GetCell(int row, int col)
    {
        if ((uint)row >= (uint)Rows || (uint)col >= (uint)Columns) return default;
        return cells[row * Columns + col];
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
            Write(data[i]);
    }

    public void Write(byte value)
    {
        value &= 0x7f; // HC2000 console masks to 7 bits

        switch (escapeState)
        {
            case 1:
                escapeState = 0;
                HandleEscape(value);
                return;
            case 2:
                escapeState = 0;
                HandleEscapeArg(pendingEscCode, value);
                return;
            case 3:
                if (pendingRow < 0)
                {
                    pendingRow = Math.Clamp(value - 0x20, 0, WindowBottom - WindowTop);
                    return;
                }
                var targetCol = Math.Clamp(value - 0x20, 0, WindowRight - WindowLeft);
                CursorRow = WindowTop + pendingRow;
                CursorCol = WindowLeft + targetCol;
                pendingRow = -1;
                escapeState = 0;
                return;
        }

        // Control codes
        switch (value)
        {
            case 0x07: // BEL
                OnBell?.Invoke();
                return;
            case 0x08: // BS: cursor left
                if (CursorCol > WindowLeft) CursorCol--;
                else if (CursorRow > WindowTop) { CursorRow--; CursorCol = WindowRight; }
                return;
            case 0x09: // HT: tab to next multiple of 8
                CursorCol = Math.Min(WindowRight, (CursorCol | 7) + 1);
                return;
            case 0x0a: // LF: cursor down, scroll at bottom
                AdvanceLine();
                return;
            case 0x0b: // VT: cursor up
                if (CursorRow > WindowTop) CursorRow--;
                return;
            case 0x0c: // FF: cursor right (per hc-term table)
                AdvanceCursor();
                return;
            case 0x0d: // CR: to window left
                CursorCol = WindowLeft;
                return;
            case 0x18 or 0x1a: // CAN / SUB: clear window and home
                ClearWindow();
                return;
            case 0x1b: // ESC
                escapeState = 1;
                return;
            case 0x1e: // RS: home without clear
                CursorRow = WindowTop;
                CursorCol = WindowLeft;
                return;
        }

        if (value < 0x20) return; // Drop unhandled control codes below 0x20

        // Printable character (0x20..0x7E, 0x7F)
        PutChar(CursorRow, CursorCol, value, Attribute, Inverse, Semigraphics);
        AdvanceCursor();
    }

    private void HandleEscape(byte value)
    {
        switch (value)
        {
            case (byte)'A': // Direct cursor address (row, then col)
                escapeState = 3;
                pendingRow = -1;
                return;
            case (byte)'B': // Clear to end of line
                ClearToEol();
                return;
            case (byte)'Y': // Clear to end of window
                ClearToEndOfWindow();
                return;
            case (byte)'j': // Inverse ON
                Inverse = true;
                return;
            case (byte)'k': // Inverse OFF
                Inverse = false;
                return;
            case (byte)'p': // Semigraphics ON
                Semigraphics = true;
                return;
            case (byte)'q': // Semigraphics OFF
                Semigraphics = false;
                return;
            case (byte)'t': // Cursor ON
                CursorVisible = true;
                return;
            case (byte)'u': // Cursor OFF
                CursorVisible = false;
                return;
            case (byte)'r' or (byte)'s': // Key click ON / OFF
                return;
            case (byte)'I' or (byte)'P' or (byte)'Q' or (byte)'R': // Set Ink / Paper / Border / Indicator
                pendingEscCode = value;
                escapeState = 2;
                return;
            case 0x13: // ESC ^S: Delete current line, scroll below up
                DeleteLine();
                return;
            case 0x1a: // ESC ^Z: Insert line at current cursor row
                InsertLine();
                return;
            default:
                // Unknown escape sequence: drop escape and print character
                Write(value);
                return;
        }
    }

    private void HandleEscapeArg(byte code, byte arg)
    {
        switch (code)
        {
            case (byte)'I': // Set Ink (the text colour)
                Attribute = (byte)((Attribute & ~0x07) | (arg & 7));
                break;
            case (byte)'P': // Set Paper (the background)
                Attribute = (byte)((Attribute & ~0x38) | ((arg & 7) << 3));
                break;
            case (byte)'Q': // Set Border
                BorderColour = (byte)(arg & 7);
                break;
            case (byte)'R': // Disk activity indicator
                break;
        }
    }

    private void AdvanceCursor()
    {
        CursorCol++;
        if (CursorCol > WindowRight)
        {
            CursorCol = WindowLeft;
            AdvanceLine();
        }
    }

    private void AdvanceLine()
    {
        if (CursorRow < WindowBottom)
        {
            CursorRow++;
        }
        else
        {
            ScrollUp();
        }
    }

    public void ScrollUp()
    {
        for (var r = WindowTop; r < WindowBottom; r++)
        {
            Array.Copy(cells, (r + 1) * Columns + WindowLeft, cells, r * Columns + WindowLeft, WindowRight - WindowLeft + 1);
        }
        for (var c = WindowLeft; c <= WindowRight; c++)
        {
            cells[WindowBottom * Columns + c] = new TextCell((byte)' ', Attribute, false, false);
        }
        engine.ConsoleScrollUp(WindowLeft, WindowTop, WindowRight - WindowLeft + 1, WindowBottom - WindowTop + 1, FontWidth, FontHeight, GetBackgroundColour(Attribute, Inverse));
    }

    public void DeleteLine()
    {
        for (var r = CursorRow; r < WindowBottom; r++)
        {
            Array.Copy(cells, (r + 1) * Columns + WindowLeft, cells, r * Columns + WindowLeft, WindowRight - WindowLeft + 1);
        }
        for (var c = WindowLeft; c <= WindowRight; c++)
        {
            cells[WindowBottom * Columns + c] = new TextCell((byte)' ', Attribute, false, false);
        }
        CursorCol = WindowLeft;
        engine.ConsoleScrollUp(WindowLeft, CursorRow, WindowRight - WindowLeft + 1, WindowBottom - CursorRow + 1, FontWidth, FontHeight, GetBackgroundColour(Attribute, Inverse));
    }

    public void InsertLine()
    {
        for (var r = WindowBottom; r > CursorRow; r--)
        {
            Array.Copy(cells, (r - 1) * Columns + WindowLeft, cells, r * Columns + WindowLeft, WindowRight - WindowLeft + 1);
        }
        for (var c = WindowLeft; c <= WindowRight; c++)
        {
            cells[CursorRow * Columns + c] = new TextCell((byte)' ', Attribute, false, false);
        }
        engine.ConsoleInsertLine(WindowLeft, CursorRow, WindowRight - WindowLeft + 1, WindowBottom - CursorRow + 1, FontWidth, FontHeight, GetBackgroundColour(Attribute, Inverse));
    }

    public void ClearWindow()
    {
        for (var r = WindowTop; r <= WindowBottom; r++)
            for (var c = WindowLeft; c <= WindowRight; c++)
                cells[r * Columns + c] = new TextCell((byte)' ', Attribute, false, false);
        CursorRow = WindowTop;
        CursorCol = WindowLeft;
        engine.ConsoleClearRect(WindowLeft * FontWidth, WindowTop * FontHeight, (WindowRight - WindowLeft + 1) * FontWidth, (WindowBottom - WindowTop + 1) * FontHeight, GetBackgroundColour(Attribute, Inverse));
    }

    public void ClearToEol()
    {
        for (var c = CursorCol; c <= WindowRight; c++)
            cells[CursorRow * Columns + c] = new TextCell((byte)' ', Attribute, false, false);
        engine.ConsoleClearRect(CursorCol * FontWidth, CursorRow * FontHeight, (WindowRight - CursorCol + 1) * FontWidth, FontHeight, GetBackgroundColour(Attribute, Inverse));
    }

    public void ClearToEndOfWindow()
    {
        ClearToEol();
        for (var r = CursorRow + 1; r <= WindowBottom; r++)
            for (var c = WindowLeft; c <= WindowRight; c++)
                cells[r * Columns + c] = new TextCell((byte)' ', Attribute, false, false);
        if (CursorRow < WindowBottom)
        {
            engine.ConsoleClearRect(WindowLeft * FontWidth, (CursorRow + 1) * FontHeight, (WindowRight - WindowLeft + 1) * FontWidth, (WindowBottom - CursorRow) * FontHeight, GetBackgroundColour(Attribute, Inverse));
        }
    }

    private void PutChar(int row, int col, byte character, byte attr, bool inv, bool semi)
    {
        cells[row * Columns + col] = new TextCell(character, attr, inv, semi);
        var fg = GetForegroundColour(attr, inv);
        var bg = GetBackgroundColour(attr, inv);
        var glyphBitmap = GetGlyphBitmap(character, semi);
        engine.ConsoleDrawGlyph(col * FontWidth, row * FontHeight, FontWidth, FontHeight, glyphBitmap, fg, bg);
    }

    // The HC console draws a character in the ink colour on the paper, as the Spectrum does (hc2000 doc hc-term: the
    // ROM's glyph writer stores the font's set bits, which the ULA shows as ink); inverse swaps the two, and bright
    // brightens both.
    private static byte GetForegroundColour(byte attr, bool inv)
    {
        var paper = (attr >> 3) & 7;
        var ink = attr & 7;
        var bright = (attr & 0x40) != 0;
        var colour = inv ? paper : ink;
        return (byte)((bright ? 8 : 0) | colour);
    }

    private static byte GetBackgroundColour(byte attr, bool inv)
    {
        var paper = (attr >> 3) & 7;
        var ink = attr & 7;
        var bright = (attr & 0x40) != 0;
        var colour = inv ? ink : paper;
        return (byte)((bright ? 8 : 0) | colour);
    }

    // The alternate set (ESC p): the 12 box drawing shapes of the ROM and ssh.asm (0x20..0x2B), expanded to 8
    // columns, and the VGA console's own (0x2C..0x39).
    private static readonly byte[][] SemigraphicBitmaps =
    [
        [0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18], // 0x20: │ vertical
        [0x18, 0x18, 0x18, 0xF8, 0xF8, 0x18, 0x18, 0x18], // 0x21: ┤ tee left
        [0x00, 0x00, 0x00, 0xF8, 0xF8, 0x18, 0x18, 0x18], // 0x22: ┐ top right
        [0x18, 0x18, 0x18, 0x1F, 0x1F, 0x00, 0x00, 0x00], // 0x23: └ bottom left
        [0x18, 0x18, 0x18, 0xFF, 0xFF, 0x00, 0x00, 0x00], // 0x24: ┴ tee up
        [0x00, 0x00, 0x00, 0xFF, 0xFF, 0x18, 0x18, 0x18], // 0x25: ┬ tee down
        [0x18, 0x18, 0x18, 0x1F, 0x1F, 0x18, 0x18, 0x18], // 0x26: ├ tee right
        [0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0x00], // 0x27: ─ horizontal
        [0x18, 0x18, 0x18, 0xFF, 0xFF, 0x18, 0x18, 0x18], // 0x28: ┼ cross
        [0x18, 0x18, 0x18, 0xF8, 0xF8, 0x00, 0x00, 0x00], // 0x29: ┘ bottom right
        [0x00, 0x00, 0x00, 0x1F, 0x1F, 0x18, 0x18, 0x18], // 0x2A: ┌ top left
        [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], // 0x2B: █ full block
        // The shapes a framed text screen needs beyond the ROM's twelve (hc2000-tpe-as-drawn; hc2000 doc hc-term):
        // double lines and their corners, the four arrows, two shades, the small square and the up-down mark. The VGA
        // console only: the ROM console's alternate font ends at 0x2B.
        [0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66], // 0x2C: ║ double vertical
        [0x00, 0xFF, 0xFF, 0x00, 0x00, 0xFF, 0xFF, 0x00], // 0x2D: ═ double horizontal
        [0x00, 0x7F, 0x7F, 0x60, 0x60, 0x67, 0x67, 0x66], // 0x2E: ╔ double top left
        [0x00, 0xFE, 0xFE, 0x06, 0x06, 0xE6, 0xE6, 0x66], // 0x2F: ╗ double top right
        [0x66, 0x67, 0x67, 0x60, 0x60, 0x7F, 0x7F, 0x00], // 0x30: ╚ double bottom left
        [0x66, 0xE6, 0xE6, 0x06, 0x06, 0xFE, 0xFE, 0x00], // 0x31: ╝ double bottom right
        [0x00, 0x00, 0x18, 0x3C, 0x7E, 0xFF, 0x00, 0x00], // 0x32: ▲ arrow up
        [0x00, 0x00, 0xFF, 0x7E, 0x3C, 0x18, 0x00, 0x00], // 0x33: ▼ arrow down
        [0x08, 0x18, 0x38, 0x78, 0x78, 0x38, 0x18, 0x08], // 0x34: ◄ arrow left
        [0x10, 0x18, 0x1C, 0x1E, 0x1E, 0x1C, 0x18, 0x10], // 0x35: ► arrow right
        [0x88, 0x22, 0x88, 0x22, 0x88, 0x22, 0x88, 0x22], // 0x36: ░ light shade
        [0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55], // 0x37: ▒ medium shade
        [0x00, 0x00, 0x3C, 0x3C, 0x3C, 0x3C, 0x00, 0x00], // 0x38: ■ small square
        [0x18, 0x3C, 0x7E, 0x18, 0x18, 0x7E, 0x3C, 0x18], // 0x39: ↕ up-down mark
    ];

    private static readonly byte[] DottedBlock = [0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55];

    private static ReadOnlySpan<byte> GetGlyphBitmap(byte character, bool semi)
    {
        if (semi && character is >= 0x20 and <= 0x39)
        {
            return SemigraphicBitmaps[character - 0x20];
        }
        if (character == 0x7F)
        {
            return DottedBlock;
        }
        if (character is >= 0x20 and <= 0x7E)
        {
            return CrispFontCatalog.Basic8x8RomFont.Slice((character - 0x20) * 8, 8);
        }
        return [0, 0, 0, 0, 0, 0, 0, 0];
    }
}
