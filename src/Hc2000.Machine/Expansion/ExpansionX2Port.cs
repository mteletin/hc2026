using System.Buffers.Binary;
using System.Text.Json.Serialization;
using Hc2000.Machine.Io;

namespace Hc2000.Machine.Expansion;

internal enum X2PortResult : byte { Accepted = 0, Busy = 1, Invalid = 2, TimeLimit = 3, Unsupported = 4, TooLate = 5 }

internal sealed record X2PortState(
    int Version,
    byte[] Staging,
    bool Collecting,
    bool Overflow,
    X2PortResult Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] uint ChainTag = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] X2PortResult BulkResult = X2PortResult.Accepted,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte[]? QueryReceipt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int QueryCursor = 0);

/// <summary>Bounded, uncomposed X2 register adapter. Port admission belongs to the host decoder.</summary>
internal sealed class ExpansionX2Port : IPortDevice
{
    internal const ushort CommandPort = 0x759d, DataPort = 0x769d, SelectorPort = 0x779d;
    internal const byte CommandService = 1;
    private const int HeaderSize = 8, MaximumFrame = 32;
    private readonly ExpansionX2Engine engine;
    private readonly ExpansionServiceDispatcher services;
    private readonly ushort cardId;
    private readonly ExpansionX2Upload? upload;
    private ExpansionDma? dma;
    private readonly byte[] staging = new byte[MaximumFrame];
    private int count;
    private bool collecting, overflow;
    private X2PortResult result;
    private uint chainTag;
    private X2PortResult bulkResult = X2PortResult.Accepted;
    private byte[]? queryReceipt;
    private int queryCursor;

    internal ExpansionX2Port(ExpansionX2Engine engine, ExpansionServiceDispatcher services, ushort cardId,
        ExpansionX2Upload? upload = null, ExpansionDma? dma = null)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        this.cardId = cardId;
        this.upload = upload;
        this.dma = dma;
        if (!services.RegisterCard(cardId) || !services.RegisterService(cardId, CommandService, Admit))
            throw new InvalidOperationException("X2 service registration failed.");
    }

    internal void BindDma(ExpansionDma dma) => this.dma = dma;

    internal bool IsQuiescent => !collecting && engine.IsQuiescent;

    internal IReadOnlyList<PortMapping> Mappings() =>
        [new(CommandPort, 0xffff, this, PortAccess.ReadWrite), new(DataPort, 0xffff, this, PortAccess.ReadWrite),
         new(SelectorPort, 0xffff, this, PortAccess.ReadWrite)];

    public byte Read(ushort port, long masterTick)
    {
        VerifyTime(masterTick);
        return port switch
        {
            CommandPort => (byte)(0x10 | (!engine.IsQuiescent ? 1 : 0) |
                (collecting ? 2 : 0) | (result is not X2PortResult.Accepted ? 4 : 0) |
                (engine.State == X2CommandState.Done ? 8 : 0)),
            DataPort => (byte)result,
            SelectorPort => queryReceipt is not null && queryCursor < queryReceipt.Length
                ? queryReceipt[queryCursor++]
                : (byte)0xff,
            _ => 0xff
        };
    }

    public void Write(ushort port, byte value, long masterTick)
    {
        VerifyTime(masterTick);
        if (port == DataPort)
        {
            if (!collecting)
            {
                engine.Console.Write(value);
                return;
            }
            if (count == MaximumFrame) { overflow = true; return; }
            staging[count++] = value;
            return;
        }
        if (port == SelectorPort)
        {
            if (value is 1 or 0xb1)
            {
                LatchQueryReceipt();
                return;
            }
            if (value == 0xb2)
            {
                LatchModeReceipt();
                return;
            }
            if (value == 0xb3)
            {
                LatchConsoleReceipt();
                return;
            }
            result = X2PortResult.Unsupported;
            return;
        }
        if (port != CommandPort) return;
        switch (value)
        {
            case 1:
                if (collecting || !engine.IsQuiescent) { result = X2PortResult.Busy; return; }
                Array.Clear(staging); count = 0; overflow = false; collecting = true; result = X2PortResult.Accepted;
                return;
            case 2:
                if (!collecting) { result = X2PortResult.Invalid; return; }
                collecting = false;
                var valid = !overflow;
                var frame = staging.AsSpan(0, count).ToArray();
                count = 0; overflow = false;
                if (!valid) { result = X2PortResult.Invalid; return; }
                var receipt = services.Submit(cardId, CommandService, frame, masterTick);
                result = receipt.State switch
                {
                    X0ServiceState.Done when receipt.Result is { Length: 1 } => (X2PortResult)receipt.Result[0],
                    X0ServiceState.Busy => X2PortResult.Busy,
                    _ => X2PortResult.Invalid
                };
                return;
            case 3:
                if (!collecting && engine.State != X2CommandState.Pending) { result = X2PortResult.Invalid; return; }
                collecting = false; count = 0; overflow = false;
                if (engine.State == X2CommandState.Pending) engine.Cancel();
                result = X2PortResult.Accepted;
                return;
            default: result = X2PortResult.Invalid; return;
        }
    }

    private void LatchQueryReceipt()
    {
        if (upload is null || dma is null)
        {
            queryReceipt = null;
            queryCursor = 0;
            return;
        }
        var buf = new byte[20];
        buf[0] = 0xb1;
        buf[1] = 1;
        buf[2] = (byte)upload.Phase;
        buf[3] = (byte)bulkResult;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)upload.Progress);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), (uint)upload.Total);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), chainTag);
        var dmaStatus = dma.Status(cardId);
        buf[16] = (byte)dmaStatus.State;
        buf[17] = (byte)dmaStatus.Refusal;
        buf[18] = 0;
        buf[19] = 0;
        queryReceipt = buf;
        queryCursor = 0;
    }

    private void LatchModeReceipt()
    {
        var buf = new byte[20];
        buf[0] = 0xb2;
        buf[1] = 1;
        buf[2] = (byte)engine.Mode;
        buf[3] = (byte)engine.Bpp;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), (ushort)engine.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6), (ushort)engine.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), (ushort)engine.Console.Columns);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10), (ushort)engine.Console.Rows);
        buf[12] = (byte)engine.Console.FontWidth;
        buf[13] = (byte)engine.Console.FontHeight;
        queryReceipt = buf;
        queryCursor = 0;
    }

    private void LatchConsoleReceipt()
    {
        var buf = new byte[20];
        buf[0] = 0xb3;
        buf[1] = 1;
        buf[2] = (byte)engine.Console.CursorRow;
        buf[3] = (byte)engine.Console.CursorCol;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), (ushort)engine.Console.Columns);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6), (ushort)engine.Console.Rows);
        buf[8] = engine.Console.Attribute;
        buf[9] = (byte)(engine.Console.CursorVisible ? 1 : 0);
        buf[10] = (byte)(engine.Console.Inverse ? 1 : 0);
        buf[11] = (byte)(engine.Console.Semigraphics ? 1 : 0);
        queryReceipt = buf;
        queryCursor = 0;
    }

    internal void Reset()
    {
        collecting = false;
        count = 0;
        overflow = false;
        result = X2PortResult.Accepted;
        chainTag = 0;
        bulkResult = X2PortResult.Accepted;
        queryReceipt = null;
        queryCursor = 0;
    }

    internal X2PortState CaptureState() => new(
        1,
        staging.AsSpan(0, count).ToArray(),
        collecting,
        overflow,
        result,
        chainTag,
        bulkResult,
        queryReceipt is null ? null : (byte[])queryReceipt.Clone(),
        queryCursor);

    internal static void ValidateState(X2PortState state, X2CommandState engineState)
    {
        if (state is null || state.Version != 1 || state.Staging is null || state.Staging.Length > MaximumFrame ||
            !Enum.IsDefined(state.Result) || !Enum.IsDefined(engineState) ||
            !state.Collecting && (state.Staging.Length != 0 || state.Overflow) ||
            state.Overflow && state.Staging.Length != MaximumFrame ||
            state.Collecting && engineState == X2CommandState.Pending ||
            !Enum.IsDefined(state.BulkResult) ||
            state.QueryReceipt is not null && (state.QueryReceipt.Length != 20 || state.QueryCursor < 0 || state.QueryCursor > 20 || state.QueryReceipt[0] is not (0xb1 or 0xb2 or 0xb3) || state.QueryReceipt[1] != 1) ||
            state.QueryReceipt is null && state.QueryCursor != 0)
            throw new ArgumentException("Invalid X2 staging state.");
    }

    internal void AdoptState(X2PortState state)
    {
        ValidateState(state, engine.State);
        Array.Clear(staging); state.Staging.CopyTo(staging, 0); count = state.Staging.Length;
        collecting = state.Collecting; overflow = state.Overflow; result = state.Result;
        chainTag = state.ChainTag;
        bulkResult = state.BulkResult;
        queryReceipt = state.QueryReceipt is null ? null : (byte[])state.QueryReceipt.Clone();
        queryCursor = state.QueryCursor;
    }

    private void VerifyTime(long tick)
    {
        if (tick != engine.Scheduler.Now) throw new ArgumentException("X2 access requires the drained device frontier.", nameof(tick));
    }

    private X0ServiceOutcome Admit(in X0ServiceCall call, long masterTick)
    {
        if (masterTick != engine.Scheduler.Now || call.Args is null || call.Args.Length < HeaderSize)
            return X0ServiceOutcome.Completed([(byte)X2PortResult.Invalid]);
        var frame = call.Args.AsSpan();
        if (frame[0] != 1 || frame[3] != 0 || frame.Length != HeaderSize + frame[2])
            return X0ServiceOutcome.Completed([(byte)X2PortResult.Invalid]);
        var opcode = frame[1];
        var tag = BinaryPrimitives.ReadUInt32LittleEndian(frame[4..]);
        var payload = frame[HeaderSize..];

        if (opcode is >= 1 and <= 7)
        {
            if (!TryDecodeDrawing(opcode, payload, out var command))
                return X0ServiceOutcome.Completed([(byte)X2PortResult.Invalid]);
            var admission = engine.Submit(command, tag) switch
            {
                X2CommandResult.Accepted => X2PortResult.Accepted,
                X2CommandResult.Busy => X2PortResult.Busy,
                X2CommandResult.TimeLimit => X2PortResult.TimeLimit,
                _ => X2PortResult.Invalid
            };
            return X0ServiceOutcome.Completed([(byte)admission]);
        }

        if (opcode is >= 8 and <= 12)
        {
            var bulkOutcome = AdmitBulk(opcode, tag, payload, masterTick);
            return X0ServiceOutcome.Completed([(byte)bulkOutcome]);
        }

        if (opcode == 16)
        {
            if (payload.Length != 1) return X0ServiceOutcome.Completed([(byte)X2PortResult.Invalid]);
            var mode = payload[0];
            if (mode > 2) return X0ServiceOutcome.Completed([(byte)X2PortResult.Invalid]);
            if (!engine.IsQuiescent) return X0ServiceOutcome.Completed([(byte)X2PortResult.Busy]);
            engine.SetMode(mode);
            return X0ServiceOutcome.Completed([(byte)X2PortResult.Accepted]);
        }

        if (opcode == 32)
        {
            engine.Console.Write(payload);
            return X0ServiceOutcome.Completed([(byte)X2PortResult.Accepted]);
        }

        if (opcode == 33)
        {
            if (payload.Length != 1) return X0ServiceOutcome.Completed([(byte)X2PortResult.Invalid]);
            engine.Console.Attribute = payload[0];
            return X0ServiceOutcome.Completed([(byte)X2PortResult.Accepted]);
        }

        return X0ServiceOutcome.Completed([(byte)X2PortResult.Invalid]);
    }

    private X2PortResult AdmitBulk(byte opcode, uint tag, ReadOnlySpan<byte> payload, long masterTick)
    {
        if (upload is null || dma is null)
        {
            bulkResult = X2PortResult.Unsupported;
            return X2PortResult.Unsupported;
        }

        switch (opcode)
        {
            case 8: // BeginUpload
            {
                if (payload.Length != 8)
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                var destOffset = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
                var totalBytes = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..8]);
                if (totalBytes == 0 || destOffset > 76800 || totalBytes > 76800 || destOffset + totalBytes > 76800)
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                if (!engine.IsQuiescent || upload.Phase == X2UploadPhase.Open)
                {
                    bulkResult = X2PortResult.Busy;
                    return X2PortResult.Busy;
                }
                var dmaStatus = dma.Status(cardId);
                if (dmaStatus.State != X0DmaState.Idle)
                {
                    bulkResult = X2PortResult.Busy;
                    return X2PortResult.Busy;
                }
                if (upload.Phase == X2UploadPhase.Cancelled && (destOffset != 0 || totalBytes != 76800))
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                if (!upload.Begin((int)destOffset, (int)totalBytes))
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                queryReceipt = null;
                queryCursor = 0;
                chainTag = tag;
                bulkResult = X2PortResult.Accepted;
                return X2PortResult.Accepted;
            }

            case 9: // UploadSegment
            {
                if (payload.Length != 16)
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                if (upload.Phase != X2UploadPhase.Open)
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                var descTag = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..16]);
                if (tag != chainTag || descTag != chainTag)
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                var version = payload[0];
                var direction = payload[1];
                var flags = payload[2];
                var reserved = payload[3];
                if (version != 1 || direction != X0DmaDirection.RamToCard || flags != 0 || reserved != 0)
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                var page = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..6]);
                var offset = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..8]);
                var count = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..12]);
                if (offset >= ExpansionDma.PageBytes || count == 0 || count > ExpansionDma.PageBytes - offset)
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                if (count > (uint)(upload.Total - upload.Progress))
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                var dmaStatus = dma.Status(cardId);
                if (dmaStatus.State != X0DmaState.Idle)
                {
                    bulkResult = X2PortResult.Busy;
                    return X2PortResult.Busy;
                }
                if (!upload.PrepareSegment((int)count))
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                dma.Trigger(cardId, X0DmaTrigger.Begin, masterTick);
                for (var i = 0; i < 16; i++) dma.WriteDescriptor(cardId, payload[i]);
                var startStatus = dma.Trigger(cardId, X0DmaTrigger.Start, masterTick);
                if (startStatus.State != X0DmaState.Queued)
                {
                    upload.CancelPreparation();
                    var refused = startStatus.Refusal == X0DmaRefusal.Busy ? X2PortResult.Busy : X2PortResult.Invalid;
                    bulkResult = refused;
                    return refused;
                }
                bulkResult = X2PortResult.Accepted;
                return X2PortResult.Accepted;
            }

            case 10: // CancelUpload
            {
                if (payload.Length != 0)
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                if (upload.Phase == X2UploadPhase.Idle)
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                if (tag != chainTag)
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                if (upload.Phase == X2UploadPhase.Complete)
                {
                    bulkResult = X2PortResult.TooLate;
                    return X2PortResult.TooLate;
                }
                if (upload.Phase == X2UploadPhase.Cancelled)
                {
                    bulkResult = X2PortResult.Accepted;
                    return X2PortResult.Accepted;
                }
                var dmaStatus = dma.Status(cardId);
                if (dmaStatus.State == X0DmaState.Queued)
                {
                    var cancelStatus = dma.Trigger(cardId, X0DmaTrigger.Cancel, masterTick);
                    if (cancelStatus.State == X0DmaState.TooLate)
                    {
                        bulkResult = X2PortResult.TooLate;
                        return X2PortResult.TooLate;
                    }
                }
                upload.Abort();
                bulkResult = X2PortResult.Accepted;
                return X2PortResult.Accepted;
            }

            case 11: // AcknowledgeSegment
            {
                if (payload.Length != 0)
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                if (tag != chainTag)
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                var dmaStatus = dma.Status(cardId);
                if (dmaStatus.State is not (X0DmaState.Done or X0DmaState.Error or X0DmaState.Cancelled or X0DmaState.TooLate))
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                dma.Trigger(cardId, X0DmaTrigger.Acknowledge, masterTick);
                bulkResult = X2PortResult.Accepted;
                return X2PortResult.Accepted;
            }

            case 12: // BeginConsoleUpload
            {
                if (payload.Length != 4)
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                var totalBytes = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
                if (totalBytes == 0 || totalBytes > 65536)
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                if (!engine.IsQuiescent || upload.Phase == X2UploadPhase.Open)
                {
                    bulkResult = X2PortResult.Busy;
                    return X2PortResult.Busy;
                }
                var dmaStatus = dma.Status(cardId);
                if (dmaStatus.State != X0DmaState.Idle)
                {
                    bulkResult = X2PortResult.Busy;
                    return X2PortResult.Busy;
                }
                if (!upload.BeginConsole((int)totalBytes))
                {
                    bulkResult = X2PortResult.Invalid;
                    return X2PortResult.Invalid;
                }
                queryReceipt = null;
                queryCursor = 0;
                chainTag = tag;
                bulkResult = X2PortResult.Accepted;
                return X2PortResult.Accepted;
            }

            default:
                bulkResult = X2PortResult.Invalid;
                return X2PortResult.Invalid;
        }
    }

    private static bool TryDecodeDrawing(byte opcode, ReadOnlySpan<byte> payload, out X2Command command)
    {
        command = default;
        switch (opcode)
        {
            case 1 when payload.Length == 1: command = new(X2CommandKind.Clear, Colour: payload[0]); return true;
            case 2 when payload.Length == 9:
                command = new(X2CommandKind.FillRectangle, X: U16(payload, 0), Y: U16(payload, 2),
                    Width: U16(payload, 4), Height: U16(payload, 6), Colour: payload[8]); return true;
            case 3 when payload.Length == 12:
                command = new(X2CommandKind.CopyRectangle, X: U16(payload, 0), Y: U16(payload, 2),
                    Width: U16(payload, 4), Height: U16(payload, 6), SourceX: U16(payload, 8), SourceY: U16(payload, 10)); return true;
            case 4 when payload.Length == 4:
                command = new(X2CommandKind.SetPalette, PaletteIndex: payload[0], Red: payload[1], Green: payload[2], Blue: payload[3]); return true;
            case 5 when payload.Length == 0: command = new(X2CommandKind.Present); return true;
            case 6 when payload.Length == 10:
                command = new(X2CommandKind.DrawGlyph, X: U16(payload, 0), Y: U16(payload, 2), Colour: payload[4],
                    Background: payload[5], GlyphRows: BinaryPrimitives.ReadUInt32LittleEndian(payload[6..])); return true;
            // DrawGlyph scaled (hc2000-ssh-terminal-resize): the same 4 x 8 glyph with each of its pixels
            // drawn sx wide and sy high (1-4 each), so a terminal fills the higher modes with 8 x 8 or 8 x 16 cells.
            // Width and Height carry the drawn cell (0, a plain glyph, is 4 x 8), so a snapshot keeps it as it is.
            case 6 when payload.Length == 12 && payload[10] is >= 1 and <= 4 && payload[11] is >= 1 and <= 4:
                command = new(X2CommandKind.DrawGlyph, X: U16(payload, 0), Y: U16(payload, 2), Colour: payload[4],
                    Background: payload[5], GlyphRows: BinaryPrimitives.ReadUInt32LittleEndian(payload[6..]),
                    Width: 4 * payload[10], Height: 8 * payload[11]); return true;
            case 7 when payload.Length == 11:
                command = new(X2CommandKind.ScrollUp, X: U16(payload, 0), Y: U16(payload, 2), Width: U16(payload, 4),
                    Height: U16(payload, 6), SourceY: U16(payload, 8), Background: payload[10]); return true;
            default: return false;
        }
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
}
