using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;

namespace Hc2000.Machine.Expansion;

internal enum X2CommandKind { Clear, FillRectangle, CopyRectangle, SetPalette, Present, DrawGlyph, ScrollUp }
internal enum X2CommandState { Idle, Pending, Done, Cancelled }
internal enum X2CommandResult { Accepted, Busy, Invalid, TimeLimit }

internal readonly record struct X2Command(X2CommandKind Kind, int X = 0, int Y = 0, int Width = 0,
    int Height = 0, int SourceX = 0, int SourceY = 0, byte Colour = 0, int PaletteIndex = 0,
    byte Red = 0, byte Green = 0, byte Blue = 0, uint GlyphRows = 0, byte Background = 0);
internal sealed record X2EngineState(int Version, X2CommandState State, X2Command Active, uint Tag,
    long DueTick, long Token, bool PresentPending, ExpansionX2SurfaceState Back, byte[] Front, ExpansionX2SurfaceState Display);
internal sealed record X2EngineMetadata(int Version, X2CommandState State, X2Command Active, uint Tag,
    long DueTick, long Token, bool PresentPending, uint[] BackPalette, ExpansionX2SurfaceState Display);

/// <summary>
/// Internal bounded X2 command engine. V1 timing is a deterministic software policy, not a
/// measured card speed. A port ABI, DMA endpoint, video-boundary hook and HCS wiring are separate.
/// </summary>
internal sealed class ExpansionX2Engine
{
    private const int CompletionKind = 1;
    private readonly DeviceScheduler scheduler;
    private readonly int deviceId;
    private readonly Func<X2CommandKind, bool>? canSubmit;
    private readonly Action<X2CommandKind>? onCommandCompleted;
    private readonly ExpansionX2Surface surface;
    private readonly Memory<byte> front;
    private Memory<byte> activeFront;
    private byte[]? mode1Front;
    private byte[]? mode2Front;
    private ExpansionX2SurfaceState displayed;
    private X2Command active;
    private long token;
    private bool presentPending;

    public ExpansionX2Engine(DeviceScheduler scheduler, int deviceId, Memory<byte> back, Memory<byte> front,
        Func<X2CommandKind, bool>? canSubmit = null, Action<X2CommandKind>? onCommandCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        if (back.Length != ExpansionX2Surface.Width * ExpansionX2Surface.Height || front.Length != back.Length ||
            back.Span.Overlaps(front.Span)) throw new ArgumentException("X2 planes must be distinct and full-sized.");
        this.scheduler = scheduler;
        this.deviceId = deviceId;
        this.canSubmit = canSubmit;
        this.onCommandCompleted = onCommandCompleted;
        this.front = front;
        activeFront = front;
        surface = new(back);
        var initial = surface.Capture();
        displayed = new(front.ToArray(), initial.Palette, 0);
        scheduler.RegisterDevice(deviceId, Complete);
        Console = new ExpansionX2Console(this);
    }

    public ExpansionX2Console Console { get; }
    public int Mode => surface.Mode;
    public int Width => surface.ActiveWidth;
    public int Height => surface.ActiveHeight;
    public int Bpp => surface.Bpp;

    public X2CommandState State { get; private set; }
    internal DeviceScheduler Scheduler => scheduler;
    internal bool IsQuiescent => State != X2CommandState.Pending && !presentPending;
    public uint Tag { get; private set; }
    public long DueTick { get; private set; }

    public bool SetMode(int mode)
    {
        if (mode is < 0 or > 2) return false;
        if (!surface.SetMode(mode)) return false;
        if (mode == 0)
        {
            activeFront = front;
        }
        else if (mode == 1)
        {
            mode1Front ??= new byte[640 * 480];
            activeFront = mode1Front;
        }
        else
        {
            mode2Front ??= new byte[1280 * 1024 / 2];
            activeFront = mode2Front;
        }
        activeFront.Span.Clear();
        displayed = new(activeFront.ToArray(), surface.CapturePalette(), mode);
        Console.SetMode(mode);
        return true;
    }

    public X2CommandResult Submit(X2Command command, uint tag)
    {
        if (!IsQuiescent || canSubmit is not null && !canSubmit(command.Kind)) return X2CommandResult.Busy;
        if (!TryWorkUnits(command, out var work)) return X2CommandResult.Invalid;
        var duration = 1L + work;
        if (scheduler.Now > long.MaxValue - duration) return X2CommandResult.TimeLimit;
        var due = scheduler.Now + duration;
        var scheduled = scheduler.Schedule(new DeviceEvent(due, EventPhase.Controller, deviceId,
            CompletionKind, scheduler.Generation(deviceId)));
        active = command;
        Tag = tag;
        DueTick = due;
        token = scheduled;
        State = X2CommandState.Pending;
        return X2CommandResult.Accepted;
    }

    public bool Cancel()
    {
        if (State != X2CommandState.Pending) return false;
        if (!scheduler.Cancel(token)) throw new InvalidOperationException("Pending X2 event is missing.");
        token = 0;
        State = X2CommandState.Cancelled;
        return true;
    }

    public void VideoBoundary()
    {
        if (!presentPending) return;
        var frame = surface.Capture();
        frame.Pixels.CopyTo(activeFront.Span);
        displayed = frame;
        presentPending = false;
    }

    public ExpansionX2SurfaceState CaptureBack() => surface.Capture();
    public ExpansionX2SurfaceState CaptureDisplay() => new((byte[])displayed.Pixels.Clone(), (uint[])displayed.Palette.Clone(), Mode);

    internal VideoFrame PublishFrame(long masterTick)
    {
        VideoBoundary();
        var width = Width;
        var height = Height;
        var count = checked(width * height);
        var pixels = Buffers.Rent(count);
        if (Mode == 2)
        {
            var raw = displayed.Pixels;
            var dest = 0;
            for (var i = 0; i < raw.Length && dest < count - 1; i++)
            {
                byte b = raw[i];
                pixels[dest++] = displayed.Palette[(b >> 4) & 0x0F];
                pixels[dest++] = displayed.Palette[b & 0x0F];
            }
        }
        else
        {
            var raw = displayed.Pixels;
            for (var index = 0; index < raw.Length && index < count; index++)
                pixels[index] = displayed.Palette[raw[index]];
        }
        return Buffers.Lend(new VideoFrame(masterTick, width, height, pixels));
    }

    internal void ConsoleDrawGlyph(int x, int y, int fontW, int fontH, ReadOnlySpan<byte> bitmap, byte fg, byte bg)
    {
        surface.DrawConsoleGlyph(x, y, fontW, fontH, bitmap, fg, bg);
        DrawToFront(x, y, fontW, fontH, bitmap, fg, bg);
    }

    internal void ConsoleClearRect(int x, int y, int width, int height, byte bg)
    {
        surface.FillRectangle(x, y, width, height, bg);
        FillFront(x, y, width, height, bg);
    }

    internal void ConsoleScrollUp(int col, int row, int widthCols, int heightRows, int fontW, int fontH, byte bg)
    {
        int px = col * fontW, py = row * fontH;
        int pw = widthCols * fontW, ph = heightRows * fontH;
        surface.ScrollUp(px, py, pw, ph, fontH, bg);
        ScrollFront(px, py, pw, ph, fontH, bg);
    }

    internal void ConsoleInsertLine(int col, int row, int widthCols, int heightRows, int fontW, int fontH, byte bg)
    {
        int px = col * fontW, py = row * fontH;
        int pw = widthCols * fontW, ph = heightRows * fontH;
        if (ph > fontH) surface.CopyRectangle(px, py, px, py + fontH, pw, ph - fontH);
        surface.FillRectangle(px, py, pw, fontH, bg);

        if (ph > fontH) CopyFront(px, py, px, py + fontH, pw, ph - fontH);
        FillFront(px, py, pw, fontH, bg);
    }

    private void DrawToFront(int x, int y, int fontW, int fontH, ReadOnlySpan<byte> bitmap, byte fg, byte bg)
    {
        var dispPixels = displayed.Pixels;
        if (Mode == 2)
        {
            var stride = Width / 2;
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
                    var val = (byte)(((b0 ? fg4 : bg4) << 4) | ((b1 ? fg4 : bg4) & 0x0F));
                    activeFront.Span[lineOffset + pair] = val;
                    if (lineOffset + pair < dispPixels.Length) dispPixels[lineOffset + pair] = val;
                }
            }
        }
        else
        {
            for (var r = 0; r < fontH; r++)
            {
                var rowBits = bitmap[r * 8 / fontH];
                var lineOffset = (y + r) * Width + x;
                for (var b = 0; b < fontW; b++)
                {
                    var val = (rowBits & (0x80 >> b)) != 0 ? fg : bg;
                    activeFront.Span[lineOffset + b] = val;
                    if (lineOffset + b < dispPixels.Length) dispPixels[lineOffset + b] = val;
                }
            }
        }
    }

    private void FillFront(int x, int y, int width, int height, byte bg)
    {
        var dispPixels = displayed.Pixels;
        if (Mode == 2)
        {
            var val = (byte)(bg & 0x0F);
            var stride = Width / 2;
            for (var r = 0; r < height; r++)
            {
                var py = y + r;
                for (var col = 0; col < width; col++)
                {
                    var px = x + col;
                    var idx = py * stride + (px >> 1);
                    if (idx < dispPixels.Length)
                    {
                        byte cur = dispPixels[idx];
                        cur = (px & 1) == 0 ? (byte)((cur & 0x0F) | (val << 4)) : (byte)((cur & 0xF0) | val);
                        activeFront.Span[idx] = cur;
                        dispPixels[idx] = cur;
                    }
                }
            }
        }
        else
        {
            for (var r = 0; r < height; r++)
            {
                var offset = (y + r) * Width + x;
                activeFront.Span.Slice(offset, width).Fill(bg);
                dispPixels.AsSpan(offset, width).Fill(bg);
            }
        }
    }

    private void CopyFront(int sx, int sy, int dx, int dy, int w, int h)
    {
        var dispPixels = displayed.Pixels;
        if (Mode == 2)
        {
            var stride = Width / 2;
            if ((sx & 1) == 0 && (dx & 1) == 0 && (w & 1) == 0)
            {
                var byteW = w / 2;
                for (var r = 0; r < h; r++)
                {
                    var srcOff = (sy + r) * stride + (sx / 2);
                    var dstOff = (dy + r) * stride + (dx / 2);
                    activeFront.Span.Slice(srcOff, byteW).CopyTo(activeFront.Span.Slice(dstOff, byteW));
                    dispPixels.AsSpan(srcOff, byteW).CopyTo(dispPixels.AsSpan(dstOff, byteW));
                }
            }
        }
        else
        {
            for (var r = 0; r < h; r++)
            {
                var srcOff = (sy + r) * Width + sx;
                var dstOff = (dy + r) * Width + dx;
                activeFront.Span.Slice(srcOff, w).CopyTo(activeFront.Span.Slice(dstOff, w));
                dispPixels.AsSpan(srcOff, w).CopyTo(dispPixels.AsSpan(dstOff, w));
            }
        }
    }

    private void ScrollFront(int x, int y, int w, int h, int rows, byte bg)
    {
        if (rows < h) CopyFront(x, y + rows, x, y, w, h - rows);
        FillFront(x, y + h - rows, w, rows, bg);
    }

    /// <summary>The arrays of the frames this X2 publishes, reused once a consumer hands a frame back (Machine.Recycle).</summary>
    internal FrameBuffers Buffers { get; } = new();

    internal X2EngineState CaptureState() => new(1, State, active, Tag, DueTick, token, presentPending,
        surface.Capture(), activeFront.ToArray(), CaptureDisplay());

    // The two live planes belong to the RAM asset. Only palette and the detached displayed frame
    // need their own metadata; restoring these must not create a second authority for RAM bytes.
    internal X2EngineMetadata CaptureMetadata() => new(1, State, active, Tag, DueTick, token, presentPending,
        surface.CapturePalette(), CaptureDisplay());

    internal void ValidateMetadata(X2EngineMetadata state, long now, IReadOnlyList<DeviceEvent> events)
    {
        ArgumentNullException.ThrowIfNull(state);
        const int pixels = ExpansionX2Surface.Width * ExpansionX2Surface.Height;
        ValidateState(new(state.Version, state.State, state.Active, state.Tag, state.DueTick, state.Token,
            state.PresentPending, new(new byte[pixels], state.BackPalette, state.Display.Mode), new byte[pixels], state.Display), now, events);
    }

    internal void AdoptMetadata(X2EngineMetadata state, long now, IReadOnlyList<DeviceEvent> events)
    {
        ValidateMetadata(state, now, events);
        var back = surface.Capture();
        AdoptState(new(state.Version, state.State, state.Active, state.Tag, state.DueTick, state.Token,
            state.PresentPending, new(back.Pixels, state.BackPalette, state.Display.Mode), front.ToArray(), state.Display), now, events);
    }

    internal void ValidateState(X2EngineState state, long now, IReadOnlyList<DeviceEvent> events)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(events);
        if (state.Version != 1 || now < 0 || !Enum.IsDefined(state.State) ||
            !ExpansionX2Surface.ValidState(state.Back) ||
            (state.Display.Mode == 0 && state.Front is not { Length: ExpansionX2Surface.Width * ExpansionX2Surface.Height }) ||
            !ExpansionX2Surface.ValidState(state.Display))
            throw new ArgumentException("Invalid X2 state.");
        if (state.PresentPending && (state.State != X2CommandState.Done || state.Active.Kind != X2CommandKind.Present))
            throw new ArgumentException("Invalid X2 present fence.");
        var owned = events.Where(item => item.DeviceId == deviceId).ToArray();
        if (state.State == X2CommandState.Idle)
        {
            if (state.Token != 0 || state.DueTick != 0 || state.Tag != 0 || state.PresentPending || owned.Length != 0)
                throw new ArgumentException("Invalid idle X2 state.");
            return;
        }
        if (!TryWorkUnits(state.Active, out _) || state.DueTick <= 0) throw new ArgumentException("Invalid X2 command.");
        if (state.State == X2CommandState.Pending)
        {
            if (state.DueTick <= now || state.Token <= 0 || owned.Length != 1 ||
                owned[0].DueTick != state.DueTick || owned[0].InsertionSequence != state.Token ||
                owned[0].Phase != EventPhase.Controller || owned[0].Kind != CompletionKind)
                throw new ArgumentException("Invalid pending X2 event.");
        }
        else if (state.Token != 0 || owned.Length != 0 ||
            state.State == X2CommandState.Done && state.DueTick > now)
            throw new ArgumentException("Invalid terminal X2 state.");
    }

    internal void AdoptState(X2EngineState state, long now, IReadOnlyList<DeviceEvent> events)
    {
        ValidateState(state, now, events);
        if (!surface.TryRestore(state.Back)) throw new InvalidOperationException("Validated X2 surface was refused.");
        var mode = state.Display.Mode;
        if (mode == 0)
        {
            activeFront = front;
        }
        else if (mode == 1)
        {
            mode1Front ??= new byte[640 * 480];
            activeFront = mode1Front;
        }
        else
        {
            mode2Front ??= new byte[1280 * 1024 / 2];
            activeFront = mode2Front;
        }
        Console.SetMode(mode);
        state.Front.CopyTo(activeFront.Span);
        displayed = new((byte[])state.Display.Pixels.Clone(), (uint[])state.Display.Palette.Clone(), state.Display.Mode);
        State = state.State;
        active = state.Active;
        Tag = state.Tag;
        DueTick = state.DueTick;
        token = state.Token;
        presentPending = state.PresentPending;
    }

    private void Complete(DeviceEvent item)
    {
        if (State != X2CommandState.Pending || item.InsertionSequence != token || item.Kind != CompletionKind)
            throw new InvalidOperationException("Unexpected X2 completion.");
        switch (active.Kind)
        {
            case X2CommandKind.Clear: surface.Clear(active.Colour); break;
            case X2CommandKind.FillRectangle:
                if (!surface.FillRectangle(active.X, active.Y, active.Width, active.Height, active.Colour))
                    throw new InvalidOperationException("Validated X2 fill was refused.");
                break;
            case X2CommandKind.CopyRectangle:
                if (!surface.CopyRectangle(active.SourceX, active.SourceY, active.X, active.Y, active.Width, active.Height))
                    throw new InvalidOperationException("Validated X2 copy was refused.");
                break;
            case X2CommandKind.SetPalette:
                if (!surface.SetPalette(active.PaletteIndex, active.Red, active.Green, active.Blue))
                    throw new InvalidOperationException("Validated X2 palette entry was refused.");
                break;
            case X2CommandKind.DrawGlyph:
                if (!surface.DrawGlyph(active.X, active.Y, active.GlyphRows, active.Colour, active.Background))
                    throw new InvalidOperationException("Validated X2 glyph was refused.");
                break;
            case X2CommandKind.ScrollUp:
                if (!surface.ScrollUp(active.X, active.Y, active.Width, active.Height, active.SourceY, active.Background))
                    throw new InvalidOperationException("Validated X2 scroll was refused.");
                break;
            case X2CommandKind.Present: presentPending = true; break;
            default: throw new InvalidOperationException("Unexpected X2 command.");
        }
        token = 0;
        onCommandCompleted?.Invoke(active.Kind);
        State = X2CommandState.Done;
    }

    private bool TryWorkUnits(X2Command command, out int work)
    {
        work = 0;
        switch (command.Kind)
        {
            case X2CommandKind.Clear: work = surface.ActiveWidth * surface.ActiveHeight; return true;
            case X2CommandKind.FillRectangle:
                if (!surface.ValidRectangle(command.X, command.Y, command.Width, command.Height)) return false;
                work = command.Width * command.Height; return true;
            case X2CommandKind.CopyRectangle:
                if (!surface.ValidRectangle(command.SourceX, command.SourceY, command.Width, command.Height) ||
                    !surface.ValidRectangle(command.X, command.Y, command.Width, command.Height)) return false;
                work = command.Width * command.Height; return true;
            case X2CommandKind.SetPalette: return (uint)command.PaletteIndex < ExpansionX2Surface.PaletteSize;
            case X2CommandKind.DrawGlyph:
                if (!surface.ValidRectangle(command.X, command.Y, 4, 8)) return false;
                work = 32; return true;
            case X2CommandKind.ScrollUp:
                if (!surface.ValidRectangle(command.X, command.Y, command.Width, command.Height) ||
                    command.SourceY <= 0 || command.SourceY > command.Height) return false;
                work = command.Width * command.Height; return true;
            case X2CommandKind.Present: return true;
            default: return false;
        }
    }
}
