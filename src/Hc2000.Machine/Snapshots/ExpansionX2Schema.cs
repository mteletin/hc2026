using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hc2000.Machine.Expansion;

namespace Hc2000.Machine.Snapshots;

/// <summary>Bounded X2 metadata; the two card planes remain part of the existing RAM asset.</summary>
internal static class ExpansionX2Schema
{
    internal const string ComponentId = "expansion-x2";
    private const int MetadataBytes = 256 * 1024;
    private const int PixelChunkChars = 2048;
    private const int PixelChars = 4 * ((ExpansionX2Surface.Width * ExpansionX2Surface.Height + 2) / 3);

    internal static JsonElement Encode(X2CardMetadata state)
    {
        var node = JsonSerializer.SerializeToNode(state, ExpansionX0Schema.Options)!;
        var display = node["Engine"]!["Display"]!;
        var pixels = display["Pixels"]!.GetValue<string>();
        var chunks = new JsonArray();
        for (var offset = 0; offset < pixels.Length; offset += PixelChunkChars)
            chunks.Add(pixels.Substring(offset, Math.Min(PixelChunkChars, pixels.Length - offset)));
        display["Pixels"] = chunks;
        var element = JsonSerializer.SerializeToElement(node);
        if (Encoding.UTF8.GetByteCount(element.GetRawText()) > MetadataBytes)
            throw new HcsException(HcsError.LimitExceeded, "expansion-x2 metadata");
        return element;
    }

    internal static X2CardMetadata Read(JsonElement element)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(element.GetRawText()) > MetadataBytes) throw Invalid();
            var engine = element.GetProperty("Engine");
            var chunks = engine.GetProperty("Display").GetProperty("Pixels");
            if (chunks.ValueKind != JsonValueKind.Array || chunks.GetArrayLength() != (PixelChars + PixelChunkChars - 1) / PixelChunkChars)
                throw Invalid();
            var pixels = new StringBuilder(PixelChars);
            foreach (var chunk in chunks.EnumerateArray())
            {
                if (chunk.ValueKind != JsonValueKind.String || chunk.GetString() is not { } value ||
                    value.Length != Math.Min(PixelChunkChars, PixelChars - pixels.Length)) throw Invalid();
                pixels.Append(value);
            }
            CheckBytes(element.GetProperty("Port").GetProperty("Staging"), 32);
            if (element.GetProperty("Port").TryGetProperty("QueryReceipt", out var receiptProp) && receiptProp.ValueKind != JsonValueKind.Null)
                CheckBytes(receiptProp, 20);
            if (engine.GetProperty("BackPalette").GetArrayLength() != ExpansionX2Surface.PaletteSize ||
                engine.GetProperty("Display").GetProperty("Palette").GetArrayLength() != ExpansionX2Surface.PaletteSize)
                throw Invalid();
            var decoded = JsonNode.Parse(element.GetRawText())!;
            decoded["Engine"]!["Display"]!["Pixels"] = pixels.ToString();
            var state = decoded.Deserialize<X2CardMetadata>(ExpansionX0Schema.Options) ?? throw Invalid();
            if (!JsonElement.DeepEquals(element, Encode(state))) throw Invalid();
            ValidateShape(state);
            return state;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or
            KeyNotFoundException or NullReferenceException or OverflowException or FormatException)
        {
            throw new HcsException(HcsError.StateInconsistent, "expansion-x2 metadata: " + error.GetType().Name);
        }
    }

    internal static JsonElement ValidateCaptured(X2CardMetadata state)
    {
        ValidateShape(state);
        return Encode(state);
    }

    private static void ValidateShape(X2CardMetadata state)
    {
        if (state.Version != 1 || state.CardId is 0 or > 255 || state.DeviceId < 0 ||
            state.BackFirst > 65536 - 5 || state.FrontFirst > 65536 - 5 ||
            state.BackFirst < state.FrontFirst + 5 && state.FrontFirst < state.BackFirst + 5 ||
            state.Engine is null || state.Engine.Version != 1 || !Enum.IsDefined(state.Engine.State) ||
            state.Engine.BackPalette is not { Length: ExpansionX2Surface.PaletteSize } ||
            state.Engine.BackPalette.Any(colour => (colour >> 24) != 0xff) ||
            !ExpansionX2Surface.ValidState(state.Engine.Display)) throw Invalid();
        ExpansionX2Port.ValidateState(state.Port, state.Engine.State);
    }

    private static void CheckBytes(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 4 * ((maximum + 2) / 3))
            throw Invalid();
    }

    private static ArgumentException Invalid() => new("Invalid expansion X2 state.");
}
