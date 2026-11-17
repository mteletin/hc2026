using System.Buffers.Binary;
using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Expansion;

internal enum X4Admission : byte { Ok, Busy, Invalid, Unsupported, NoTarget, TooLate, TimeLimit, ResourceLimit }
internal enum X4EngineState : byte { Idle, Busy, Done, NumericalError, Cancelled }
internal sealed record X4EngineSnapshot(int Version, X4EngineState State, ulong Generation, uint Tag,
    long AcceptedTick, long DueTick, long Token, byte[] Request, X4NumericResult? Candidate, X4NumericResult? Published);

internal sealed class ExpansionX4Engine
{
    private readonly DeviceScheduler scheduler;
    private readonly int deviceId;
    private X4NumericResult? candidate;
    private X4NumericResult? published;
    private ulong generation = 1;
    private long token, acceptedTick, dueTick;
    private uint tag;
    private byte operation, source, destination;
    private byte[] requestBytes = [];
    internal X4EngineState State { get; private set; }
    internal bool IsQuiescent => State == X4EngineState.Idle;

    internal ExpansionX4Engine(DeviceScheduler scheduler, int deviceId)
    {
        this.scheduler = scheduler;
        this.deviceId = deviceId;
        scheduler.RegisterDevice(deviceId, Complete);
    }

    internal byte[] Handle(ReadOnlySpan<byte> request)
    {
        if (request.Length < 8) throw new ArgumentException("X4 requires a correlation header.", nameof(request));
        var requestOp = request[1];
        var requestTag = BinaryPrimitives.ReadUInt32LittleEndian(request[4..8]);
        if (request.Length != 8 + request[2] || request[3] != 0)
            return Response(requestOp, requestTag, X4Admission.Invalid);
        if (request[0] != 1 || requestOp > 4)
            return Response(requestOp, requestTag, X4Admission.Unsupported);
        if ((requestOp == 1 ? request[2] != 24 : request[2] != 0) || requestOp != 0 && requestTag == 0)
            return Response(requestOp, requestTag, X4Admission.Invalid);
        if (requestOp == 0)
        {
            byte[] info = [1, 1, 1, 0, 0x1f, 0, 0, 0, 0xfe, 0, 0, 0, 32, 64, 0, 0];
            return Response(requestOp, requestTag, X4Admission.Ok, info);
        }
        if (requestOp == 1) return Start(request, requestTag);
        if (State == X4EngineState.Idle || requestTag != tag)
            return Response(requestOp, requestTag, X4Admission.NoTarget);
        if (requestOp == 2) return Response(requestOp, requestTag, X4Admission.Ok, View());
        if (requestOp == 3)
        {
            if (State != X4EngineState.Busy) return Response(requestOp, requestTag, X4Admission.TooLate);
            if (!scheduler.Cancel(token)) throw new InvalidOperationException("X4 pending event is missing.");
            token = 0;
            candidate = null;
            State = X4EngineState.Cancelled;
            return Response(requestOp, requestTag, X4Admission.Ok, View());
        }
        if (State == X4EngineState.Busy) return Response(requestOp, requestTag, X4Admission.Busy);
        Clear();
        return Response(requestOp, requestTag, X4Admission.Ok, View());
    }

    private byte[] Start(ReadOnlySpan<byte> request, uint requestTag)
    {
        var data = request[8..];
        var validation = ValidateStart(data);
        if (validation != X4Admission.Ok) return Response(1, requestTag, validation);
        var requestedOperation = data[0];
        var requestedSource = data[1];
        var requestedDestination = data[2];
        var options = data[3];
        var integer = requestedSource is 1 or 2;
        if (State != X4EngineState.Idle) return Response(1, requestTag, X4Admission.Busy);
        var latency = Latency(requestedOperation, requestedSource);
        if (scheduler.Now > long.MaxValue - latency) return Response(1, requestTag, X4Admission.TimeLimit);
        if (generation == ulong.MaxValue) return Response(1, requestTag, X4Admission.ResourceLimit);
        try { _ = scheduler.NextSequence; }
        catch (OverflowException) { return Response(1, requestTag, X4Admission.ResourceLimit); }
        var numeric = integer
            ? ExpansionX4Math.EvaluateInteger(requestedOperation, requestedSource, data[4..12], data[12..20])
            : ExpansionX4Math.EvaluateFloat(requestedOperation, requestedSource, requestedDestination,
                (options & 1) != 0, data[4..12], data[12..20]);
        var nextGeneration = generation + 1;
        var due = scheduler.Now + latency;
        var nextToken = scheduler.Schedule(new(due, EventPhase.Controller, deviceId, 1,
            scheduler.Generation(deviceId), unchecked((long)nextGeneration)));
        operation = requestedOperation; source = requestedSource; destination = requestedDestination;
        tag = requestTag; acceptedTick = scheduler.Now; dueTick = due; generation = nextGeneration;
        token = nextToken; candidate = numeric; published = null; requestBytes = request.ToArray(); State = X4EngineState.Busy;
        return Response(1, requestTag, X4Admission.Ok, View());
    }

    private static X4Admission ValidateStart(ReadOnlySpan<byte> data)
    {
        if (data.Length != 24) return X4Admission.Invalid;
        var requestedOperation = data[0];
        var requestedSource = data[1];
        var requestedDestination = data[2];
        var options = data[3];
        if (requestedOperation is < 1 or > 7 || requestedSource is < 1 or > 7 || requestedDestination is < 1 or > 7)
            return X4Admission.Unsupported;
        var integer = requestedSource is 1 or 2;
        var binaryFloat = requestedSource is 3 or 4;
        var supported = requestedOperation switch
        {
            1 or 2 or 3 => (integer || binaryFloat) && requestedSource == requestedDestination,
            4 or 6 => binaryFloat && requestedSource == requestedDestination,
            5 => integer && requestedSource == requestedDestination,
            7 => requestedSource >= 3 && requestedDestination >= 3,
            _ => false
        };
        if (!supported) return X4Admission.Unsupported;
        var width = requestedSource switch { 1 or 3 or 5 => 4, 2 or 4 or 6 => 8, 7 => 6, _ => 0 };
        if ((requestedOperation == 7 ? (options & ~1) != 0 : options != 0) ||
            data[20..24].IndexOfAnyExcept((byte)0) >= 0 || data.Slice(4 + width, 8 - width).IndexOfAnyExcept((byte)0) >= 0 ||
            (requestedOperation is 6 or 7 ? data[12..20].IndexOfAnyExcept((byte)0) >= 0 :
                data.Slice(12 + width, 8 - width).IndexOfAnyExcept((byte)0) >= 0))
            return X4Admission.Invalid;
        return X4Admission.Ok;
    }

    private static long Latency(byte requestedOperation, byte format) => requestedOperation switch
    {
        1 or 2 => format switch { 1 => 64, 2 or 3 => 128, _ => 256 },
        3 => format switch { 1 => 128, 2 or 3 => 256, _ => 512 },
        4 => format == 3 ? 512 : 1024,
        5 => format == 1 ? 512 : 1024,
        6 => format == 3 ? 1024 : 2048,
        7 => 256,
        _ => throw new ArgumentOutOfRangeException(nameof(requestedOperation))
    };

    internal bool TryReset()
    {
        if (generation == ulong.MaxValue) return false;
        if (State == X4EngineState.Busy && !scheduler.Cancel(token))
            throw new InvalidOperationException("X4 pending event is missing.");
        generation++;
        Clear();
        return true;
    }

    internal X4EngineSnapshot Capture() => new(1, State, generation, tag, acceptedTick, dueTick, token,
        requestBytes.ToArray(), Copy(candidate), Copy(published));

    internal bool ValidState(X4EngineSnapshot? snapshot, long now, IReadOnlyList<DeviceEvent> events)
    {
        if (snapshot is not { Version: 1, Generation: > 0, Request: not null } || now < 0 || events is null ||
            !Enum.IsDefined(snapshot.State)) return false;
        var owned = events.Where(item => item.DeviceId == deviceId).ToArray();
        if (snapshot.State == X4EngineState.Idle)
            return snapshot.Tag == 0 && snapshot.AcceptedTick == 0 && snapshot.DueTick == 0 && snapshot.Token == 0 &&
                snapshot.Request.Length == 0 && snapshot.Candidate is null && snapshot.Published is null && owned.Length == 0;
        var frame = snapshot.Request;
        if (frame.Length != 32 || frame[0] != 1 || frame[1] != 1 || frame[2] != 24 || frame[3] != 0 ||
            snapshot.Tag == 0 || BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)) != snapshot.Tag ||
            ValidateStart(frame.AsSpan(8)) != X4Admission.Ok || snapshot.AcceptedTick < 0 || snapshot.AcceptedTick > now)
            return false;
        var expectedLatency = Latency(frame[8], frame[9]);
        if (snapshot.AcceptedTick > long.MaxValue - expectedLatency || snapshot.DueTick != snapshot.AcceptedTick + expectedLatency)
            return false;
        if (snapshot.State == X4EngineState.Busy)
            return snapshot.DueTick > now && snapshot.Token > 0 && snapshot.Published is null &&
                ValidResult(snapshot.Candidate, frame) && owned.Length == 1 &&
                owned[0].DueTick == snapshot.DueTick && owned[0].Phase == EventPhase.Controller && owned[0].Kind == 1 &&
                owned[0].InsertionSequence == snapshot.Token && unchecked((ulong)owned[0].Payload) == snapshot.Generation;
        if (snapshot.Token != 0 || snapshot.Candidate is not null || owned.Length != 0) return false;
        if (snapshot.State == X4EngineState.Cancelled) return snapshot.Published is null;
        if (snapshot.DueTick > now || !ValidResult(snapshot.Published, frame)) return false;
        return snapshot.State == X4EngineState.Done
            ? snapshot.Published!.Status == X4NumericStatus.Success
            : snapshot.State == X4EngineState.NumericalError && snapshot.Published!.Status != X4NumericStatus.Success;
    }

    internal void Restore(X4EngineSnapshot snapshot, long now, IReadOnlyList<DeviceEvent> events)
    {
        if (!ValidState(snapshot, now, events)) throw new ArgumentException("Invalid X4 engine state.", nameof(snapshot));
        State = snapshot.State; generation = snapshot.Generation; tag = snapshot.Tag;
        acceptedTick = snapshot.AcceptedTick; dueTick = snapshot.DueTick; token = snapshot.Token;
        requestBytes = snapshot.Request.ToArray(); candidate = Copy(snapshot.Candidate); published = Copy(snapshot.Published);
        operation = requestBytes.Length == 0 ? (byte)0 : requestBytes[8];
        source = requestBytes.Length == 0 ? (byte)0 : requestBytes[9];
        destination = requestBytes.Length == 0 ? (byte)0 : requestBytes[10];
    }

    private static X4NumericResult? Copy(X4NumericResult? result) =>
        result is null ? null : new(result.Status, result.Flags, result.Bytes.ToArray());

    private static bool ValidResult(X4NumericResult? result, ReadOnlySpan<byte> frame)
    {
        if (result is null || result.Bytes is null || !Enum.IsDefined(result.Status) || (result.Flags & ~(X4NumericFlags)7) != 0)
            return false;
        if (result.Status != X4NumericStatus.Success) return result.Bytes.Length == 0 && result.Flags == X4NumericFlags.None;
        var operation = frame[8];
        var source = frame[9];
        var destination = frame[10];
        var width = destination switch { 1 or 3 or 5 => 4, 2 or 4 or 6 => 8, 7 => 6, _ => 0 };
        if (result.Bytes.Length != (operation == 5 ? width * 2 : width)) return false;
        if (source <= 2 && result.Flags != X4NumericFlags.None) return false;
        if ((result.Flags & X4NumericFlags.Underflow) != 0 && (result.Flags & X4NumericFlags.Inexact) == 0) return false;
        if ((result.Flags & X4NumericFlags.NegativeZeroLost) != 0 && (operation != 7 || destination < 5)) return false;
        return true;
    }

    private void Complete(DeviceEvent item)
    {
        if (State != X4EngineState.Busy || item.InsertionSequence != token || item.DueTick != dueTick ||
            unchecked((ulong)item.Payload) != generation || candidate is null)
            throw new InvalidOperationException("Unexpected X4 completion.");
        published = candidate; candidate = null; token = 0;
        State = published.Status == X4NumericStatus.Success ? X4EngineState.Done : X4EngineState.NumericalError;
    }

    private void Clear()
    {
        State = X4EngineState.Idle;
        candidate = null; published = null; token = acceptedTick = dueTick = 0;
        tag = 0; operation = source = destination = 0; requestBytes = [];
    }

    private byte[] View()
    {
        var result = new byte[56];
        result[0] = (byte)State; result[1] = operation; result[2] = source; result[3] = destination;
        if (published is not null)
        {
            result[4] = (byte)published.Status;
            result[5] = checked((byte)published.Bytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), (ushort)published.Flags);
            published.Bytes.CopyTo(result, 40);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), generation);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(24), (ulong)acceptedTick);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(32), (ulong)dueTick);
        return result;
    }

    private static byte[] Response(byte requestOp, uint requestTag, X4Admission admission, byte[]? payload = null)
    {
        payload ??= [];
        var result = new byte[8 + payload.Length];
        result[0] = 1; result[1] = requestOp; result[2] = (byte)admission; result[3] = checked((byte)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), requestTag);
        payload.CopyTo(result, 8);
        return result;
    }
}
