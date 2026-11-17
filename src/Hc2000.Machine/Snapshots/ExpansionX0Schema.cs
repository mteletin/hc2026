using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Hc2000.Machine.Expansion;
using Hc2000.Machine.Network;

namespace Hc2000.Machine.Snapshots;

/// <summary>
/// Bounded metadata for the X0 shared platform (guest-service dispatcher + shared physical DMA).
/// A separate component so the expansion-cpu-step1 metadata stays byte-identical. A save without
/// this component restores the explicit empty engine (the v1-save rule) and nothing else.
/// </summary>
internal static class ExpansionX0Schema
{
    internal const string ComponentId = "expansion-x0";
    internal const int MetadataBytes = 1024 * 1024;
    internal const int EndpointStateBytes = 4096;
    private const int ChunkChars = 2048;
    private const int MaxStringChars = 4096;
    internal static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal static bool Enabled(MachineProfile profile) =>
        profile.Name is "hc2000-v2-paged-expansion-v1" or "hc2000-v2-paged-expansion-x5-v1" or "hc2000-v2-cpnet-v1" or "hc2000-v2-combined-workstation-v1" or "hc2000-v2-combined-workstation-v2"
            or "hc2000-v2-paged-expansion-x5-hdd-guest-v1" or "hc2000-v2-full-workstation-v1";

    internal static JsonElement Encode(ExpansionX0.ExpansionX0State state)
    {
        var node = JsonSerializer.SerializeToNode(state, Options)!;
        foreach (var channel in node["Dma"]!["Channels"]!.AsArray())
        {
            Chunk(channel!, "Data");
            Chunk(channel!, "EndpointState");
        }
        foreach (var card in node["Services"]!["Cards"]!.AsArray()) Chunk(card!, "Result");
        var element = JsonSerializer.SerializeToElement(node);
        if (Encoding.UTF8.GetByteCount(element.GetRawText()) > MetadataBytes)
            throw new HcsException(HcsError.LimitExceeded, "expansion-x0 metadata");
        return element;
    }

    internal static JsonElement ValidateCaptured(ExpansionX0.ExpansionX0State state, long now)
    {
        var encoded = Encode(state);
        ValidateState(state, now);
        return encoded;
    }

    internal static ExpansionX0.ExpansionX0State Read(JsonElement element)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(element.GetRawText()) > MetadataBytes) throw Invalid();
            var decoded = JsonNode.Parse(element.GetRawText())!;
            CheckChannelSize(element, decoded);
            var s = decoded.Deserialize<ExpansionX0.ExpansionX0State>(Options) ?? throw Invalid();
            // Reject duplicate or ignored fields and any noncanonical state.
            if (!JsonElement.DeepEquals(element, Encode(s))) throw Invalid();
            ValidateState(s, long.MinValue);
            return s;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or
            KeyNotFoundException or NullReferenceException or OverflowException or FormatException)
        {
            throw new HcsException(HcsError.StateInconsistent, "expansion-x0 metadata: " + e.GetType().Name);
        }
    }

    private static void CheckChannelSize(JsonElement element, JsonNode decoded)
    {
        var index = 0;
        var channels = decoded["Dma"]!["Channels"]!.AsArray();
        foreach (var channel in element.GetProperty("Dma").GetProperty("Channels").EnumerateArray())
        {
            CheckSize(channel.GetProperty("Staging"), ExpansionDma.DescriptorBytes, "staging");
            if (channel.GetProperty("Data").ValueKind != JsonValueKind.Null)
                Unchunk(channel.GetProperty("Data"), channels[index]!, "Data", ExpansionDma.PageBytes);
            Unchunk(channel.GetProperty("EndpointState"), channels[index]!, "EndpointState", EndpointStateBytes);
            index++;
        }
        index = 0;
        var cards = decoded["Services"]!["Cards"]!.AsArray();
        foreach (var card in element.GetProperty("Services").GetProperty("Cards").EnumerateArray())
            Unchunk(card.GetProperty("Result"), cards[index++]!, "Result", ExpansionServiceDispatcher.MaxServiceBytes);
        if (element.TryGetProperty("CpNet", out var cpnet) && cpnet.ValueKind != JsonValueKind.Null)
            CheckSize(cpnet.GetProperty("RxBytes"), CpNetFrame.MaxFrameBytes, "cp-net-rx");
    }

    private static void Chunk(JsonNode parent, string key)
    {
        if (parent[key] is not JsonValue value || value.GetValue<string>() is not { Length: > MaxStringChars } text) return;
        var chunks = new JsonArray();
        for (var offset = 0; offset < text.Length; offset += ChunkChars)
            chunks.Add(text.Substring(offset, Math.Min(ChunkChars, text.Length - offset)));
        parent[key] = chunks;
    }

    private static void Unchunk(JsonElement source, JsonNode parent, string key, int maximum)
    {
        if (source.ValueKind == JsonValueKind.String)
        {
            CheckSize(source, maximum, key);
            if (source.GetString()!.Length > MaxStringChars) throw Invalid();
            return;
        }
        if (source.ValueKind != JsonValueKind.Array) throw Invalid();
        var maxChars = 4 * ((maximum + 2) / 3);
        var count = source.GetArrayLength();
        if (count == 0 || count > (maxChars + ChunkChars - 1) / ChunkChars) throw Invalid();
        var text = new StringBuilder();
        var index = 0;
        foreach (var chunk in source.EnumerateArray())
        {
            if (chunk.ValueKind != JsonValueKind.String || chunk.GetString() is not { Length: > 0 and <= ChunkChars } value ||
                ++index < count && value.Length != ChunkChars || text.Length + value.Length > maxChars) throw Invalid();
            text.Append(value);
        }
        if (text.Length <= MaxStringChars) throw Invalid();
        parent[key] = text.ToString();
    }

    private static void CheckSize(JsonElement value, int maxBytes, string what)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 4 * ((maxBytes + 2) / 3))
            throw new ArgumentException(what + " exceeds its bound.");
    }

    private static void ValidateState(ExpansionX0.ExpansionX0State s, long now)
    {
        if (s.CpNet is { } cpnet && (cpnet.Model != CpNetX0Card.Version || cpnet.Phase > 4 ||
            cpnet.Error > CpNetX0Card.ErrorTooLate || cpnet.RxCount < 0 || cpnet.RxBytes is null ||
            cpnet.RxBytes.Length != cpnet.RxCount || cpnet.RxCount > CpNetFrame.MaxFrameBytes || cpnet.Cursor < 0 ||
            (cpnet.RxCount > 0 && cpnet.Cursor >= cpnet.RxCount) ||
            (cpnet.Phase == 4 ? cpnet.RxCount == 0 : cpnet.RxCount != 0 || cpnet.Cursor != 0)))
            throw Invalid();
        if (s.Version != ExpansionX0.StateVersion || s.Dma.Version != ExpansionX0.StateVersion ||
            s.Services.Version != ExpansionX0.StateVersion) throw Invalid();
        if (s.Dma.SetupTicks < 0 || s.Dma.SetupTicks > int.MaxValue / 2 || s.Dma.RateNumerator <= 0 ||
            s.Dma.RateNumerator > int.MaxValue / 2 || s.Dma.RateDenominator <= 0 || s.Dma.RateDenominator > int.MaxValue / 2)
            throw Invalid();
        if (s.Dma.Channels.Length > ExpansionDma.MaxCards) throw Invalid();
        foreach (var c in s.Dma.Channels)
        {
            if (c.CardId == 0 || c.EndpointEpoch == 0 || !Enum.IsDefined((X0DmaState)c.State) ||
                c.Staging is not { Length: ExpansionDma.DescriptorBytes } ||
                c.StagingCount < 0 || c.StagingCount > ExpansionDma.DescriptorBytes ||
                !Enum.IsDefined((X0DmaRefusal)c.Refusal) || c.EndpointState is null || c.EndpointState.Length > EndpointStateBytes ||
                c.Data is { Length: > ExpansionDma.PageBytes } ||
                c.DueTick < 0 || c.Token < 0) throw Invalid();
            switch ((X0DmaState)c.State)
            {
                case X0DmaState.Idle:
                    if (c.StagingCount != 0 || c.HasRecord || c.Data is not null || c.Token != 0 ||
                        c.Refusal != (byte)X0DmaRefusal.None) throw Invalid();
                    break;
                case X0DmaState.Programming:
                    if (c.HasRecord || c.Data is not null || c.Token != 0) throw Invalid();
                    break;
                case X0DmaState.Queued:
                    if (!c.HasRecord || c.Data is not { Length: > 0 } || c.Offset >= ExpansionDma.PageBytes ||
                        c.Count == 0 || c.Count != (uint)c.Data.Length || c.Offset + c.Count > ExpansionDma.PageBytes ||
                        (c.Direction != X0DmaDirection.RamToCard && c.Direction != X0DmaDirection.CardToRam) ||
                        c.DueTick <= now || c.Token < 0) throw Invalid();
                    break;
                case X0DmaState.Done or X0DmaState.Error or X0DmaState.Cancelled or X0DmaState.TooLate:
                    if (c.Data is not null || c.Token != 0) throw Invalid();
                    break;
                default:
                    throw Invalid();
            }
        }
        if (s.Services.Cards.Length > ExpansionServiceDispatcher.MaxCards) throw Invalid();
        var seen = new HashSet<ushort>();
        foreach (var card in s.Services.Cards)
        {
            if (card.CardId == 0 || !Enum.IsDefined((X0ServiceState)card.State) || card.Deadline < 0 || card.SubmitTick < 0 ||
                card.Result.Length > ExpansionServiceDispatcher.MaxServiceBytes || !seen.Add(card.CardId)) throw Invalid();
            switch ((X0ServiceState)card.State)
            {
                case X0ServiceState.Idle:
                    if (card.Deadline != 0 || card.Completed || card.Result.Length != 0) throw Invalid();
                    break;
                case X0ServiceState.Pending:
                    if (card.Deadline <= card.SubmitTick || card.Completed != (card.Result.Length > 0)) throw Invalid();
                    break;
                case X0ServiceState.Done:
                    if (!card.Completed || card.Result.Length == 0 || card.Deadline < card.SubmitTick) throw Invalid();
                    break;
                case X0ServiceState.Cancelled or X0ServiceState.TooLate:
                    if (card.Deadline <= card.SubmitTick || card.Result.Length != 0 || card.Completed) throw Invalid();
                    break;
                case X0ServiceState.InvalidState:
                    if (card.Deadline != 0 || card.Completed || card.Result.Length != 0) throw Invalid();
                    break;
                default:
                    throw Invalid();
            }
        }
    }

    private static ArgumentException Invalid() => new("Invalid expansion X0 state.");
}
