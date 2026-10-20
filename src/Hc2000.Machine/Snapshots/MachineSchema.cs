using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hc2000.Machine.Tape.Snapshots;

namespace Hc2000.Machine.Snapshots;

/// <summary>The compiled HC2000 machine schema (KB hc2000 doc spec-04-machine): the exact rosters of
/// stores, components and state tables the logical .hcs file carries, plus their field and value
/// checks. Composition-dependent consistency (rom binding, event/state pairing, device rosters)
/// is enforced by <see cref="MachineSnapshot.Load"/> against the live machine.</summary>
public sealed partial class MachineSchema(MachineProfile profile, HcsWavPolicy? wavPolicy = null)
    : HcsSchema(profile.Name, checked((uint)profile.Version), Definition(profile))
{
    private static readonly string[] Components =
        ["drive-a", "drive-b", "fdc", "frame-clock", "keyboard", "memory", "tape", "ula"];

    private static readonly Dictionary<string, string[]> Tables = new(StringComparer.Ordinal)
    {
        ["frame-clock"] = ["completedFrames", "intAsserted"],
        ["ula"] = ["feLatch", "flashInverted", "beeperTick", "beeperSpeaker", "beeperMicrophone", "tapeIn"],
        ["keyboard"] = ["pressed", "joysticks"],
        ["memory"] = ["port7e", "cpm", "shadow"],
        ["tape"] = ["state", "blockIndex", "stage", "pilotConsumed", "nextBit", "half",
            "waveformLevel", "idealTarget", "remaining", "mediaIdentity", "pendingToken"],
        ["fdc"] = ["phase", "mainStatus", "specify1", "specify2", "pcn", "latch", "motorTimerRunning",
            "lastCommand", "lastSt0", "lastSt1", "lastSt2", "lastSt3", "instant", "timing",
            "driveB80TrackJumper", "headLoadedUnits", "pendingInterrupts"],
        ["drive-a"] = ["headPosition", "headLoaded", "motorOn", "rotationOrigin", "writeProtected",
            "diskChanged", "dirtySectors"],
        ["drive-b"] = ["headPosition", "headLoaded", "motorOn", "rotationOrigin", "writeProtected",
            "diskChanged", "dirtySectors"],
    };

    /// <summary>The deterministic profile definition embedded in the file and bound by its
    /// fingerprint: machine identity, turbo, IO policy, memory and video choices.
    /// A file built for a different machine profile fails here with ProfileMismatch.</summary>
    public static JsonElement Definition(MachineProfile p)
    {
        var palette = new JsonArray();
        foreach (var v in p.Video.Palette) palette.Add(JsonValue.Create(v));
        var o = new JsonObject
        {
            ["name"] = p.Name,
            ["version"] = p.Version,
            ["cpuTurboMultiplier"] = p.CpuTurboMultiplier,
            ["io"] = Kebab(p.Io),
            ["memory"] = new JsonObject
            {
                ["romLayout"] = Kebab(p.Memory.RomLayout),
                ["ramBytes"] = p.Memory.RamBytes,
                ["romBytes"] = p.Memory.RomBytes,
                ["shadowRomBytes"] = p.Memory.ShadowRomBytes,
                ["shadowRamBytes"] = p.Memory.ShadowRamBytes,
                ["romProvenance"] = p.Memory.RomProvenance
            },
            ["video"] = new JsonObject
            {
                ["fullRaster"] = p.Video.FullRaster,
                ["palette"] = palette,
                ["flashPeriodNumerator"] = p.Video.FlashPeriodNumerator,
                ["flashPeriodDenominator"] = p.Video.FlashPeriodDenominator
            }
        };
        if (p.Memory.RamPaged is { } paged) o["memory"]!["pagedPolicy"] = paged.PolicyId;
        return JsonDocument.Parse(o.ToJsonString()).RootElement.Clone();
    }

    private static JsonElement[] Arr(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array) Error(HcsError.InvalidFormat, "array");
        return e.EnumerateArray().ToArray();
    }

    private static string Kebab(System.Enum e)
    {
        string s = e.ToString();
        var sb = new StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (i > 0 && char.IsUpper(c)) sb.Append('-');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    protected override void ValidateModels(JsonElement manifest)
    {
        foreach (var o in Arr(manifest.GetProperty("components")))
        {
            string id = Id(o.GetProperty("id"));
            if (!Components.Contains(id)) Error(HcsError.UnsupportedVersion, "component");
            if (Id(o.GetProperty("modelId")) != id)
                Error(HcsError.UnsupportedVersion, "component");
            var mv = UInt(o.GetProperty("modelVersion"));
            if (id == "fdc" ? mv != 2 : id == "tape" ? mv is not (1 or 2) : mv != 1)
                Error(HcsError.UnsupportedVersion, "component");
            var s = o.GetProperty("state");
            if (id == "tape" && mv == 2)
            {
                try { TapeComponentCodec.Read(s); }
                catch (Exception e) when (e is JsonException or ArgumentException or OverflowException)
                { Error(HcsError.InvalidFormat, "tape2"); }
                continue;
            }
            if (id == "memory" && s.TryGetProperty("paged", out _)) Fields(s, [.. Tables[id], "paged"]);
            else Fields(s, [.. Tables[id]]);
            switch (id)
            {
                case "frame-clock":
                    Counter(s.GetProperty("completedFrames"));
                    CheckBool(s, "intAsserted");
                    break;
                case "ula":
                    UInt(s.GetProperty("feLatch"), 31); CheckBool(s, "flashInverted");
                    Counter(s.GetProperty("beeperTick"));
                    CheckBool(s, "beeperSpeaker"); CheckBool(s, "beeperMicrophone"); CheckBool(s, "tapeIn");
                    break;
                case "keyboard":
                    CheckKeyArray(s, "pressed", 50, 1);
                    CheckKeyArray(s, "joysticks", 2, 31);
                    break;
                case "memory":
                    UInt(s.GetProperty("port7e"), 15);
                    CheckBool(s, "cpm"); CheckBool(s, "shadow");
                    ValidatePagedState(s);
                    break;
                case "tape":
                    if (Text(s.GetProperty("state")) is not
                        ("unloaded" or "stopped" or "playing" or "at-end"))
                        Error(HcsError.InvalidFormat, "state");
                    UInt(s.GetProperty("blockIndex")); UInt(s.GetProperty("stage"), 6);
                    UInt(s.GetProperty("pilotConsumed")); UInt(s.GetProperty("nextBit"));
                    UInt(s.GetProperty("half"), 1); CheckBool(s, "waveformLevel");
                    Decimal64(s, "idealTarget"); Decimal64(s, "remaining");
                    Counter(s.GetProperty("mediaIdentity")); Counter(s.GetProperty("pendingToken"));
                    break;
                case "fdc":
                    var phase = Text(s.GetProperty("phase"));
                    if (phase is not ("reset" or "command")) Error(HcsError.StateUnsupported, "phase");
                    UInt(s.GetProperty("mainStatus"), 255);
                    UInt(s.GetProperty("specify1"), 255); UInt(s.GetProperty("specify2"), 255);
                    CheckIdArray(s, "pcn", 4);
                    UInt(s.GetProperty("latch"), 31); CheckBool(s, "motorTimerRunning");
                    CheckIdArray(s, "lastCommand", 9);
                    for (int i = 0; i < 4; i++) CheckNullableSt(s, $"lastSt{i}");
                    CheckBool(s, "instant"); CheckBool(s, "driveB80TrackJumper");
                    var timing = s.GetProperty("timing");
                    Fields(timing, "masterHz", "fdcz", "mfmBitsPerSecond", "rpm", "indexPulseMicroseconds");
                    foreach (var p in timing.EnumerateObject()) UInt(p.Value);
                    var units = Arr(s.GetProperty("headLoadedUnits"));
                    long prior = -1;
                    foreach (var u in units) { uint v = UInt(u, 3); if (v <= prior) Error(HcsError.StateInconsistent, "units"); prior = v; }
                    var pi = s.GetProperty("pendingInterrupts");
                    foreach (var p in pi.EnumerateArray()) { UInt(p.GetProperty("s"), 255); UInt(p.GetProperty("c"), 255); }
                    break;
                case "drive-a":
                case "drive-b":
                    UInt(s.GetProperty("headPosition")); CheckBool(s, "headLoaded");
                    CheckBool(s, "motorOn"); Counter(s.GetProperty("rotationOrigin"));
                    CheckBool(s, "writeProtected"); CheckBool(s, "diskChanged");
                    foreach (var d in Arr(s.GetProperty("dirtySectors")))
                    {
                        Fields(d, "c", "s", "i");
                        UInt(d.GetProperty("c"), 101); UInt(d.GetProperty("s"), 1); UInt(d.GetProperty("i"), 28);
                    }
                    break;
            }
        }
        if (Arr(manifest.GetProperty("pendingOutput")).Length != 0)
            Error(HcsError.StateUnsupported, "pendingOutput");
    }

    private static void CheckBool(JsonElement e, string k)
    {
        if (e.GetProperty(k).ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            Error(HcsError.InvalidFormat, k);
    }

    private static void CheckKeyArray(JsonElement e, string k, int count, uint max)
    {
        var items = Arr(e.GetProperty(k));
        if (items.Length != count) Error(HcsError.InvalidFormat, k);
        foreach (var item in items) UInt(item, max);
    }

    private static void CheckIdArray(JsonElement e, string k, int maxCount)
    {
        var items = Arr(e.GetProperty(k));
        if (items.Length > maxCount) Error(HcsError.InvalidFormat, k);
        foreach (var item in items) UInt(item, 255);
    }

    private static void CheckNullableSt(JsonElement e, string k)
    {
        var p = e.GetProperty(k);
        if (p.ValueKind == JsonValueKind.Null) return;
        UInt(p, 255);
    }

    /// <summary>Signed decimal64 string (the tape ideal position and remaining numerator may be negative).</summary>
    private static void Decimal64(JsonElement e, string k)
    {
        string v = Text(e.GetProperty(k));
        if (System.Text.RegularExpressions.Regex.IsMatch(v, "\\A-?(?:0|[1-9][0-9]*)\\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            && long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return;
        Error(HcsError.InvalidFormat, k);
    }
}
