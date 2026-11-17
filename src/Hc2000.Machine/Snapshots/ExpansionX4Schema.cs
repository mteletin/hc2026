using System.Text;
using System.Text.Json;
using Hc2000.Machine.Expansion;

namespace Hc2000.Machine.Snapshots;

internal static class ExpansionX4Schema
{
    internal const string ComponentId = "expansion-x4";
    private const int MetadataBytes = 8192;

    internal static JsonElement Encode(X4CardMetadata state)
    {
        var result = JsonSerializer.SerializeToElement(state, ExpansionX0Schema.Options);
        if (Encoding.UTF8.GetByteCount(result.GetRawText()) > MetadataBytes)
            throw new HcsException(HcsError.LimitExceeded, "expansion-x4 metadata");
        return result;
    }

    internal static JsonElement ValidateCaptured(X4CardMetadata state)
    {
        ValidateShape(state);
        return Encode(state);
    }

    internal static X4CardMetadata Read(JsonElement element)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(element.GetRawText()) > MetadataBytes) throw Invalid();
            var engine = element.GetProperty("Engine");
            CheckBytes(engine.GetProperty("Request"), 32);
            CheckResult(engine.GetProperty("Candidate"));
            CheckResult(engine.GetProperty("Published"));
            var port = element.GetProperty("Port");
            CheckBytes(port.GetProperty("Staging"), 32);
            var reply = port.GetProperty("Reply");
            if (reply.ValueKind != JsonValueKind.Null) CheckBytes(reply, 64);
            var state = element.Deserialize<X4CardMetadata>(ExpansionX0Schema.Options) ?? throw Invalid();
            if (!JsonElement.DeepEquals(element, Encode(state))) throw Invalid();
            ValidateShape(state);
            return state;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or
            KeyNotFoundException or NullReferenceException or OverflowException or FormatException)
        {
            throw new HcsException(HcsError.StateInconsistent, "expansion-x4 metadata: " + error.GetType().Name);
        }
    }

    private static void ValidateShape(X4CardMetadata state)
    {
        if (state.Version != 1 || state.CardId is 0 or > 255 || state.EngineDevice < 0 ||
            state.Engine is not { Version: 1, Request.Length: <= 32 } ||
            state.Engine.Candidate is { Bytes.Length: > 16 } || state.Engine.Published is { Bytes.Length: > 16 } ||
            state.Port is not { Version: 1, Staging.Length: <= 32 } || state.Port.CardId != state.CardId ||
            state.Port.Reply is { Length: > 64 }) throw Invalid();
    }

    private static void CheckResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Null) CheckBytes(result.GetProperty("Bytes"), 16);
    }

    private static void CheckBytes(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > 4 * ((maximum + 2) / 3))
            throw Invalid();
    }

    private static ArgumentException Invalid() => new("Invalid expansion X4 state.");
}
