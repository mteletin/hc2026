using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hc2000.Machine.Snapshots;

/// <summary>Logical Z80 register and interrupt state captured at a drained instruction boundary.
/// This record is the CPU half of the .hcs logical state: no raw memory, no host objects;
/// the manifest <c>cpu</c> section is the only CPU surface in the file.</summary>
public sealed record CpuLogicalState(
    ushort Af, ushort Bc, ushort De, ushort Hl, ushort Ix, ushort Iy, ushort Sp, ushort Pc,
    ushort AfShadow, ushort BcShadow, ushort DeShadow, ushort HlShadow, ushort Wz,
    byte I, byte R, byte Q,
    bool Iff1, bool Iff2, bool Halted, bool IntLine, bool NmiPending, bool LastLdAiOrR, bool NmosLdAirInterruptBug,
    int Im, byte EiDelay, long TStates)
{
    private static readonly string[] Words =
        ["af", "bc", "de", "hl", "ix", "iy", "sp", "pc", "afShadow", "bcShadow", "deShadow", "hlShadow", "wz"];
    private static readonly string[] Bytes = ["i", "r", "q"];
    private static readonly string[] Booleans =
        ["iff1", "iff2", "halted", "intLine", "nmiPending", "lastLdAiOrR", "nmosLdAirInterruptBug"];
    private static readonly string[] Fields =
        [.. Words, .. Bytes, .. Booleans, "im", "eiDelay", "tStates", "modelId", "modelVersion"];

    /// <summary>The exact HCS <c>cpu</c> section: modelId "nmos-z80", modelVersion 1,
    /// words/bytes/im/eiDelay as unsigned integers and tStates as a decimal64 string.</summary>
    public JsonObject ToJson(bool frontier = false)
    {
        var o = new JsonObject();
        o["af"] = Af; o["bc"] = Bc; o["de"] = De; o["hl"] = Hl;
        o["ix"] = Ix; o["iy"] = Iy; o["sp"] = Sp; o["pc"] = Pc;
        o["afShadow"] = AfShadow; o["bcShadow"] = BcShadow; o["deShadow"] = DeShadow;
        o["hlShadow"] = HlShadow; o["wz"] = Wz;
        o["i"] = I; o["r"] = R; o["q"] = Q;
        o["iff1"] = Iff1; o["iff2"] = Iff2; o["halted"] = Halted;
        o["intLine"] = IntLine; o["nmiPending"] = NmiPending;
        o["lastLdAiOrR"] = LastLdAiOrR; o["nmosLdAirInterruptBug"] = NmosLdAirInterruptBug;
        o["im"] = (uint)Im; o["eiDelay"] = EiDelay;
        o["tStates"] = TStates.ToString(CultureInfo.InvariantCulture);
        o["modelId"] = frontier ? "nmos-z80-frontier" : "nmos-z80"; o["modelVersion"] = 1;
        return o;
    }

    /// <summary>Strict parse of the <c>cpu</c> section (same field table as the base schema).</summary>
    public static CpuLogicalState FromJson(JsonElement e, bool frontier = false)
    {
        if (e.ValueKind != JsonValueKind.Object) Fail("cpu");
        var names = e.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != Fields.Length || names.Except(Fields, StringComparer.Ordinal).Any()) Fail("cpu fields");
        if (e.GetProperty("modelId").GetString() != (frontier ? "nmos-z80-frontier" : "nmos-z80") || UInt(e, "modelVersion", 1) != 1)
            throw new HcsException(HcsError.UnsupportedVersion, "cpu");
        ushort Word(string k)
        {
            var p = e.GetProperty(k);
            uint n = 0;
            if (p.ValueKind != JsonValueKind.Number || !p.TryGetUInt32(out n) || n > 65535)
                Fail(k);
            return (ushort)n;
        }
        byte Byte(string k)
        {
            var p = e.GetProperty(k);
            uint n = 0;
            if (p.ValueKind != JsonValueKind.Number || !p.TryGetUInt32(out n) || n > 255)
                Fail(k);
            return (byte)n;
        }
        bool Bool(string k)
        {
            var p = e.GetProperty(k);
            if (p.ValueKind == JsonValueKind.True) return true;
            if (p.ValueKind == JsonValueKind.False) return false;
            Fail(k);
            return false;
        }
        static void Fail(string field) => HcsCodec.Fail(HcsError.InvalidFormat, field);
        static uint UInt(JsonElement e, string k, uint max)
        {
            var p = e.GetProperty(k);
            uint n = 0;
            if (p.ValueKind != JsonValueKind.Number || !p.TryGetUInt32(out n) || n > max)
                Fail(k);
            return n;
        }
        return new CpuLogicalState(
            Word("af"), Word("bc"), Word("de"), Word("hl"), Word("ix"), Word("iy"), Word("sp"), Word("pc"),
            Word("afShadow"), Word("bcShadow"), Word("deShadow"), Word("hlShadow"), Word("wz"),
            Byte("i"), Byte("r"), Byte("q"),
            Bool("iff1"), Bool("iff2"), Bool("halted"), Bool("intLine"),
            Bool("nmiPending"), Bool("lastLdAiOrR"), Bool("nmosLdAirInterruptBug"),
            (int)UInt(e, "im", 2), (byte)UInt(e, "eiDelay", frontier ? 2u : 1u), TStates(e));

        static long TStates(JsonElement e)
        {
            var p = e.GetProperty("tStates");
            if (p.ValueKind != JsonValueKind.String || !long.TryParse(p.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var v))
                throw new HcsException(HcsError.InvalidFormat, "tStates");
            return v;
        }
    }
}

/// <summary>The CPU snapshot seam: capture and restore the logical register file at a drained
/// instruction boundary. The Z80 (wi-111) implements it; the .hcs file never depends on the
/// CPU's raw memory or host objects, only on the <c>cpu</c> section.</summary>
public interface ICpuSnapshotSeam
{
    CpuLogicalState Capture();
    void Restore(CpuLogicalState state);
}
