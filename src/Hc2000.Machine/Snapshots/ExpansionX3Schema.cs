using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hc2000.Machine.Expansion;

namespace Hc2000.Machine.Snapshots;

/// <summary>Bounded detached X3 metadata. FIFO bytes are split to respect the HCS string limit.</summary>
internal static class ExpansionX3Schema
{
    internal const string ComponentId = "expansion-x3";
    private const int MetadataBytes = 128 * 1024;
    private const int ChunkChars = 2048;
    private const int MaxFifoChars = 4 * ((ExpansionX3Fifo.Capacity + 2) / 3);

    internal static JsonElement Encode(X3CardMetadata state)
    {
        var node = JsonSerializer.SerializeToNode(state, ExpansionX0Schema.Options)!;
        var channels = node["Streamer"]!["Playback"]!["Channels"]!.AsArray();
        foreach (var channel in channels)
        {
            var fifo = channel!["Fifo"]!;
            var contents = fifo["Contents"]!.GetValue<string>();
            var chunks = new JsonArray();
            for (var offset = 0; offset < contents.Length; offset += ChunkChars)
                chunks.Add(contents.Substring(offset, Math.Min(ChunkChars, contents.Length - offset)));
            fifo["Contents"] = chunks;
        }
        var result = JsonSerializer.SerializeToElement(node);
        if (Encoding.UTF8.GetByteCount(result.GetRawText()) > MetadataBytes)
            throw new HcsException(HcsError.LimitExceeded, "expansion-x3 metadata");
        return result;
    }

    internal static X3CardMetadata Read(JsonElement element)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(element.GetRawText()) > MetadataBytes) throw Invalid();
            var channels = element.GetProperty("Streamer").GetProperty("Playback").GetProperty("Channels");
            if (channels.ValueKind != JsonValueKind.Array || channels.GetArrayLength() != ExpansionX3Clock.ChannelCount)
                throw Invalid();
            CheckBytes(element.GetProperty("Port").GetProperty("Staging"), 32);
            var reply = element.GetProperty("Port").GetProperty("Reply");
            if (reply.ValueKind != JsonValueKind.Null) CheckBytes(reply, 48);
            var decoded = JsonNode.Parse(element.GetRawText())!;
            var decodedChannels = decoded["Streamer"]!["Playback"]!["Channels"]!.AsArray();
            var index = 0;
            foreach (var channel in channels.EnumerateArray())
            {
                var chunks = channel.GetProperty("Fifo").GetProperty("Contents");
                if (chunks.ValueKind != JsonValueKind.Array || chunks.GetArrayLength() > (MaxFifoChars + ChunkChars - 1) / ChunkChars)
                    throw Invalid();
                var bytes = new StringBuilder();
                var count = chunks.GetArrayLength();
                var item = 0;
                foreach (var chunk in chunks.EnumerateArray())
                {
                    if (chunk.ValueKind != JsonValueKind.String || chunk.GetString() is not { Length: > 0 and <= ChunkChars } text ||
                        ++item < count && text.Length != ChunkChars || bytes.Length + text.Length > MaxFifoChars) throw Invalid();
                    bytes.Append(text);
                }
                decodedChannels[index++]!["Fifo"]!["Contents"] = bytes.ToString();
            }
            var state = decoded.Deserialize<X3CardMetadata>(ExpansionX0Schema.Options) ?? throw Invalid();
            if (!JsonElement.DeepEquals(element, Encode(state))) throw Invalid();
            ValidateShape(state);
            return state;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or
            KeyNotFoundException or NullReferenceException or OverflowException or FormatException)
        {
            throw new HcsException(HcsError.StateInconsistent, "expansion-x3 metadata: " + error.GetType().Name);
        }
    }

    internal static JsonElement ValidateCaptured(X3CardMetadata state)
    {
        ValidateShape(state);
        return Encode(state);
    }

    private static void ValidateShape(X3CardMetadata state)
    {
        if (state.Version != 1 || state.CardId is 0 or > 255 || state.ClockDevice < 0 || state.PumpDevice < 0 ||
            state.ClockDevice == state.PumpDevice || state.Streamer is not { Version: 1 } || state.Streamer.CardId != state.CardId ||
            state.Streamer.Playback is not { Version: 1, Channels.Length: ExpansionX3Clock.ChannelCount } ||
            state.Streamer.Playback.Clock is not { Version: 1, Channels.Length: ExpansionX3Clock.ChannelCount } ||
            state.Streamer.Playback.Channels.Any(channel => channel is null || !ExpansionX3Fifo.ValidState(channel.Fifo)) ||
            state.Streamer.Playback.Clock.Channels.Any(channel => channel is null) ||
            state.Port is not { Version: 1 } || state.Port.CardId != state.CardId ||
            state.Port.Staging is not { Length: <= 32 } || state.Port.Reply is { Length: > 48 }) throw Invalid();
    }

    private static void CheckBytes(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 4 * ((maximum + 2) / 3)) throw Invalid();
    }

    private static ArgumentException Invalid() => new("Invalid expansion X3 state.");
}
