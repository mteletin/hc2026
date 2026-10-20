using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hc2000.Machine.Expansion;
using Hc2000.Machine.Snapshots;
using Hc2000.Z80;
using Hc2000.Z80.Frontier;
using Xunit;
using Cpu = Hc2000.Z80.Z80;

namespace Hc2000.Machine.Tests;

public sealed class ExpansionSnapshotValidationTests
{
    [Fact]
    public void ValidDetachedMetadataReachesTheUnrelatedMemoryModelSentinelWithoutAllocatingRam()
    {
        var error = Reject(Detached());
        Assert.Equal("UnsupportedVersion: component", error.Message);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("main-kind")]
    [InlineData("short-roster")]
    [InlineData("long-roster")]
    [InlineData("kind")]
    [InlineData("unit-id")]
    [InlineData("resource-epoch")]
    [InlineData("execution-epoch")]
    [InlineData("map-epoch")]
    [InlineData("main-page")]
    [InlineData("overlap")]
    [InlineData("clock-anchor")]
    [InlineData("remainder")]
    [InlineData("stopped-continuation")]
    [InlineData("running-no-continuation")]
    [InlineData("paused-paused-resume")]
    [InlineData("cpu-stage")]
    [InlineData("cpu-dead-temporary")]
    [InlineData("cpu-register-alias")]
    [InlineData("cpu-too-large")]
    [InlineData("missing-main-event")]
    [InlineData("wrong-main-event-phase")]
    [InlineData("wrong-main-event-tick")]
    [InlineData("parked-event")]
    [InlineData("mailbox-size")]
    [InlineData("mailbox-dead-byte")]
    [InlineData("mailbox-overflow")]
    [InlineData("mailbox-error")]
    [InlineData("root-response-cursor")]
    [InlineData("root-response-frame")]
    [InlineData("root-pending-epochs")]
    [InlineData("extra-field")]
    [InlineData("missing-field")]
    [InlineData("exchange-owner")]
    [InlineData("handover-halt")]
    [InlineData("exchange-previous-bank")]
    public void MalformedExpansionIsRefusedBeforeStoresOrRamAreRead(string change)
    {
        var manifest = Detached();
        var state = manifest["components"]![0]!["state"]!;
        var unit = state["Units"]![0]!;
        var mailbox = unit["Mailbox"]!;
        switch (change)
        {
            case "version": state["Version"] = 2; break;
            case "main-kind": state["MainKind"] = "compute"; break;
            case "short-roster": state["Units"]!.AsArray().RemoveAt(15); break;
            case "long-roster": state["Units"]!.AsArray().Add(unit.DeepClone()); break;
            case "kind": unit["Kind"] = "main"; break;
            case "unit-id": unit["Grant"]!["UnitId"] = 2; break;
            case "resource-epoch": unit["Grant"]!["ResourceEpoch"] = 0; break;
            case "execution-epoch": unit["ExecutionEpoch"] = 0; break;
            case "map-epoch": unit["Grant"]!["MapEpoch"] = 0; break;
            case "main-page": unit["Grant"]!["Pages"]!["Page0"] = 255; break;
            case "overlap": unit["Grant"]!["Pages"]!["Page0"] = 513; break;
            case "clock-anchor": unit["MasterAnchor"] = 1; break;
            case "remainder": unit["Remainder"] = 1; break;
            case "stopped-continuation": unit["Cpu"] = state["Main"]!.DeepClone(); break;
            case "running-no-continuation": unit["Lifecycle"] = (int)ExpansionLifecycle.Running; break;
            case "paused-paused-resume":
                unit["Lifecycle"] = (int)ExpansionLifecycle.Paused;
                unit["SavedLifecycle"] = (int)ExpansionLifecycle.Paused; break;
            case "cpu-stage": state["Main"]!["Stage"] = 65535; break;
            case "cpu-dead-temporary":
                var temporary = new byte[32]; temporary[30] = 1;
                state["Main"]!["Temporaries"] = Convert.ToBase64String(temporary); break;
            case "cpu-register-alias": state["Main"]!["Registers"]!["AF"] = 1; break;
            case "cpu-too-large": state["Main"]!["extra"] = new string('x', 4096); break;
            case "missing-main-event": manifest["events"]!.AsArray().Clear(); break;
            case "wrong-main-event-phase": manifest["events"]![0]!["phase"] = 4; break;
            case "wrong-main-event-tick": manifest["events"]![0]!["dueTick"] = "21"; break;
            case "parked-event":
                var extra = manifest["events"]![0]!.DeepClone();
                extra["deviceId"] = "device-1001"; extra["sequence"] = "2";
                manifest["events"]!.AsArray().Add(extra); manifest["clock"]!["nextSequence"] = "3"; break;
            case "mailbox-size": mailbox["Inbox"] = Convert.ToBase64String(new byte[72]); break;
            case "mailbox-dead-byte":
                var dead = new byte[73]; dead[72] = 1;
                mailbox["Inbox"] = Convert.ToBase64String(dead); break;
            case "mailbox-overflow": mailbox["Overflow"] = true; break;
            case "mailbox-error": mailbox["LastError"] = 2; break;
            case "root-response-cursor": state["Root"]!["ResponseCursor"] = 1; break;
            case "root-response-frame": state["Root"]!["Response"] = Convert.ToBase64String(new byte[40]); break;
            case "root-pending-epochs": state["Root"]!["PendingId"] = 1; state["Root"]!["PendingOperation"] = 2; break;
            case "extra-field": unit["card"] = "not admitted"; break;
            case "missing-field": unit.AsObject().Remove("ExecutionEpoch"); break;
            case "exchange-owner": unit["ExchangeOwner"] = 1; break;
            case "handover-halt": unit["HandoverHalt"] = true; break;
            case "exchange-previous-bank": unit["ExchangePreviousBank"] = 1; break;
            default: throw new ArgumentException(change);
        }
        Assert.StartsWith("StateInconsistent: expansion metadata:", Reject(manifest).Message);
    }

    private static HcsException Reject(JsonObject manifest) => Assert.Throws<HcsException>(() =>
        HcsCodec.Write(Stream.Null, JsonSerializer.SerializeToElement(manifest), [],
            new MachineSchema(MachineProfile.Hc2000V2PagedExpansionV1)));

    // A literal wire fixture, not a smaller arena or a second capture path. The ordinary public
    // writer validates metadata before examining blobs. Memory model 2 is an explicit later sentinel.
    private static JsonObject Detached()
    {
        var profile = MachineProfile.Hc2000V2PagedExpansionV1;
        var definition = MachineSchema.Definition(profile);
        var main = new Cpu(new NoBus()); _ = main.PeekNextFrontier();
        var frontier = main.CaptureFrontier(); var r = frontier.Registers;
        var logical = new CpuLogicalState(r.AF, r.BC, r.DE, r.HL, r.IX, r.IY, r.SP, r.PC,
            r.AFShadow, r.BCShadow, r.DEShadow, r.HLShadow, r.WZ, r.I, r.R, r.Q,
            r.IFF1, r.IFF2, frontier.Halted, frontier.IntLine, frontier.NmiPending,
            frontier.LdAirPending, frontier.InterruptBug, r.IM, (byte)frontier.EiDelay, frontier.TStates);
        var state = new
        {
            Version = 1, MainKind = "main", Main = frontier,
            Units = Enumerable.Range(1, 16).Select(id => new
            {
                Kind = "compute", Grant = new ExpansionPageGrant((ushort)id, 1, 1,
                    new((ushort)(508 + id * 4), (ushort)(509 + id * 4), (ushort)(510 + id * 4), (ushort)(511 + id * 4))),
                ExecutionEpoch = 1UL, Lifecycle = ExpansionLifecycle.ResetStopped, SavedLifecycle = ExpansionLifecycle.ResetStopped,
                CpuAnchor = 0L, MasterAnchor = 0L, LastMasterTick = 0L, Remainder = 0L,
                RequestedMailboxEffect = ExpansionMailboxEffect.None, Cpu = new Cpu(new NoBus()).CaptureFrontier(),
                Mailbox = new { Inbox = new byte[73], Outbox = new byte[73], Staging = new byte[73],
                    InboxCursor = 0, StagingCount = 0, HasInbox = false, HasOutbox = false,
                    Collecting = false, Overflow = false, LastError = (byte)0 },
                ExchangeOwner = (byte)0, HandoverHalt = false, ExchangePreviousBank = (ushort)0
            }).ToArray(),
            Root = new { Selected = (ushort)0, Latched = (ushort)0, Staging = Array.Empty<byte>(),
                Collecting = false, Overflow = false, Response = (byte[]?)null, ResponseCursor = 0,
                PendingId = (ushort)0, PendingOperation = (byte)0, LastResult = ExpansionResult.Accepted }
        };
        static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
        return new JsonObject
        {
            ["profile"] = new JsonObject { ["id"] = profile.Name, ["version"] = 1,
                ["definition"] = JsonNode.Parse(definition.GetRawText()), ["fingerprint"] = HcsCodec.ProfileFingerprint(definition) },
            ["assets"] = new JsonArray(), ["stores"] = new JsonArray(),
            ["components"] = new JsonArray(
                new JsonObject { ["id"] = "expansion-cpu-step1", ["modelId"] = "expansion-cpu-step1", ["modelVersion"] = 1,
                    ["state"] = JsonSerializer.SerializeToNode(state, new JsonSerializerOptions { IncludeFields = true }) },
                new JsonObject { ["id"] = "memory", ["modelId"] = "memory", ["modelVersion"] = 2,
                    ["state"] = JsonNode.Parse("""
                    {"port7e":0,"cpm":false,"shadow":false,"paged":{"v":2,"view":"m-transfer","bank":0,
                     "screen":false,"rom":false,"locked":false,"transfer":false,"ramBytes":"1073741824",
                     "ramSha256":"0000000000000000000000000000000000000000000000000000000000000000"}}
                    """) }),
            ["cpu"] = logical.ToJson(true),
            ["clock"] = new JsonObject { ["epoch"] = "0", ["now"] = "0", ["masterHz"] = "17500000",
                ["dividerNumerator"] = "5", ["dividerDenominator"] = "1", ["lastCpuTStates"] = "0",
                ["mapperWhole"] = "0", ["mapperRemainder"] = "0", ["nextSequence"] = "2",
                ["deviceGenerations"] = new JsonArray(Enumerable.Range(1000, 17).Select(id => (JsonNode)new JsonObject
                    { ["id"] = "device-" + id, ["generation"] = "0" }).ToArray()) },
            ["events"] = new JsonArray(new JsonObject { ["dueTick"] = N(frontier.Pending!.Value.AtCpuTState * 5),
                ["phase"] = 5, ["deviceId"] = "device-1000", ["kind"] = "kind-0", ["generation"] = "0",
                ["sequence"] = "1", ["payload"] = new JsonObject { ["value"] = "0" } }),
            ["pendingOutput"] = new JsonArray()
        };
    }

    private sealed class NoBus : IBus
    {
        public byte Read(ushort address) => throw new InvalidOperationException();
        public void Write(ushort address, byte value) => throw new InvalidOperationException();
        public byte In(ushort port) => throw new InvalidOperationException();
        public void Out(ushort port, byte value) => throw new InvalidOperationException();
        public void Contend(ushort address, int tstates) => throw new InvalidOperationException();
        public byte IntVector() => throw new InvalidOperationException();
    }
}
