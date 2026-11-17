using System.Text.Json;
using System.Text.Json.Nodes;
using Hc2000.Machine.Network;

namespace Hc2000.Machine.Snapshots;

internal static class CpNetStagingSchema
{
    internal const string ComponentId = "cpnet-staging";

    internal static JsonObject Encode() => new()
    {
        ["page"] = CpNetX0Card.StagingPage,
        ["cardId"] = CpNetX0Card.CardId,
        ["sourceOffset"] = 0,
        ["sourceCapacity"] = CpNetX0Card.StagingCapacity,
        ["mainMappable"] = true,
        ["direction"] = "ram-to-card"
    };

    internal static bool Matches(JsonElement value) =>
        JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(Encode()));
}
