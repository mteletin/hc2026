using Hc2000.App.Input;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Video;
using Hc2000.Machine.Timing;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace Hc2000.Machine.Tests;

/// <summary>
/// wi-118 step 5a: prove the guest-relevant punctuation set <c>: . = [ ]</c> delivers through the
/// single existing faithful input path - KeyboardInputAdapter.Paste -> InputQueue ->
/// InputController -> faithful KeyboardMatrix - and that the guest CP/M accepts them in a real
/// command, <c>PIP B:ROUND.COM=A:PIP.COM[OV]</c>, on the step4 boot contract (faithful
/// CPM22-HC.DSK warm-booted to A&gt;). No second input implementation is added: this test drives
/// the app's own input classes against the machine's keyboard matrix.
///
/// Evidence recorded per run: exact key sequences (host char -> KeyMap.Character chord -> HC keys),
/// guest CONOUT echo and consumption, PIP outcome bytes, frame hash, and the pinned ROM/disk/build
/// provenance.
/// </summary>
public sealed class PunctuationInputAcceptanceTests
{
    private readonly ITestOutputHelper _output;
    public PunctuationInputAcceptanceTests(ITestOutputHelper output) => _output = output;

    // The five guest-relevant punctuation characters from the item contract.
    private static readonly char[] Punctuation = { ':', '.', '=', '[', ']' };

    // Exact guest command from the item (colon, dots, equals, and the [OV] brackets are the five).
    private const string PipCommand = "PIP B:ROUND.COM=A:PIP.COM[OV]";

    // Per-edge paste interval. The CP/M keyboard scan (ED44/ED87) debounces a new key for 5
    // scans before it registers as a press; the proven TypeKey harness cadence is 8 hold + 8
    // release frames = 16 frames per edge. We paste through the SAME faithful PasteText path
    // with that longer, proven-safe interval so each chord (SymbolShift+letter, CapsShift+letter)
    // clears the guest's typematic debounce. This is a harness timing choice, not a second
    // input implementation: KeyboardInputAdapter's own default is 6 frames/edge.
    private const long PasteIntervalTicks = 16 * 355_680L;

    [Fact]
    public void PunctuationDeliversThroughFaithfulInputPathAndGuestPipAcceptsIt()
    {
        var root = StorageAssetTests.Root();
        var haveAssets = File.Exists(Path.Combine(root, "roms/hc2000/v1/basic.rom"))
                        && File.Exists(Path.Combine(root, "roms/hc2000/v2/cpm-hc2kzip.bin"))
                        && File.Exists(Path.Combine(root, "disks/cpm/CPM22-HC.DSK"));
        if (!haveAssets) return;

        var diskSource = Path.Combine(root, "disks/cpm", "CPM22-HC.DSK");
        var diskBytes = File.ReadAllBytes(diskSource);
        var diskSha = Convert.ToHexString(SHA256.HashData(diskBytes)).ToLowerInvariant();

        // Private per-run evidence directory (wi-126 pattern: no shared /tmp paths).
        var evidenceDir = Path.Combine(Path.GetTempPath(), $"wi118-step5a-{Environment.ProcessId}");
        Directory.CreateDirectory(evidenceDir);

        // Put ROUND.COM on a separate CP/M-formatted image for drive B so the guest's PIP has real
        // work: it must read A:PIP.COM, write B:ROUND.COM (proving B: is accepted) and list
        // A:PIP.COM in the [OV] listing. Drive B is a distinct image so nothing is shared with A.
        var driveB = Dsk.Create(Dpb.Presets().Single(p => p.Name == "cpm-hc80").Geometry, DiskFormat.Extended);
        CpmFs.Open(driveB, "cpm-hc80").Put("ROUND.COM", new byte[16]);
        var driveBBefore = driveB.Save();

        // Pinned provenance.
        var romSha = new (string rel, string sha)[]
        {
            ("roms/hc2000/v2/basic.rom", ShaFile(Path.Combine(root, "roms/hc2000/v2/basic.rom"))),
            ("roms/hc2000/v2/cpm-hc2kzip.bin", ShaFile(Path.Combine(root, "roms/hc2000/v2/cpm-hc2kzip.bin"))),
            ("roms/hc2000/v2/if1.rom", ShaFile(Path.Combine(root, "roms/hc2000/v2/if1.rom"))),
        };
        var buildId = $"hc2000-emu branch @ {GitHead()}";

        // ── Leg 1 (host surface): each punctuation char maps to a bounded, defined key chord. ──
        var mapping = new System.Text.StringBuilder();
        foreach (var ch in Punctuation)
        {
            var chord = KeyMap.Character(ch);
            Assert.NotNull(chord);
            Assert.All(chord!.Keys, k => Assert.True(Enum.IsDefined(k), $"{k} is not a defined HC key"));
            mapping.Append($"  {ch} -> ");
            mapping.AppendLine(string.Join(" + ", chord.Keys.Select(k => k.ToString())));
        }
        _output.WriteLine("KeyMap.Character mapping for the five punctuation chars:\n" + mapping);

        // The full command must be pasteable end-to-end by the adapter without a single rejection:
        // this is the host-side guarantee that the guest command string is deliverable.
        var probeQueue = new InputQueue();
        var probe = new KeyboardInputAdapter(probeQueue);
        var rejections = new List<string>();
        probe.Rejected += rejections.Add;
        Assert.True(probe.Paste(PipCommand + "\n"),
            "KeyboardInputAdapter.Paste rejected the guest PIP command");
        Assert.Empty(rejections);

        // ── Leg 2 (guest): the same five characters, driven through the existing faithful path,
        // land in the matrix as distinct chords and the guest CP/M accepts the whole command. ──
        var boot = new MachineBootAcceptanceTests.BootHarness(
            MachineBootAcceptanceTests.BootHarness.Config.ForCpmWarmBoot("hc2000-v2", "CPM22-HC.DSK"));
        // The BootHarness constructor wires both drives onto one controller at epoch: drive A gets
        // the canonical disk and drive B gets the ROUND.COM pre-placement, because
        // FloppyController.DriveB is get-only. Mount must happen before any frame runs.
        boot.Floppy!.DriveB.Mount(driveB);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# wi-118 step 5a evidence: punctuation input via the faithful path, guest PIP acceptance");
        sb.AppendLine($"run: {DateTime.UtcNow:O}");
        sb.AppendLine($"build: {buildId}");
        sb.AppendLine($"disk: CPM22-HC.DSK sha256={diskSha} (canonical, unmodified bytes; ROUND.COM pre-placed on drive B only)");
        foreach (var (rel, sha) in romSha) sb.AppendLine($"{rel}: sha256={sha}");
        sb.AppendLine();
        sb.AppendLine("KeyMap.Character (existing host mapping, no new implementation):");
        sb.AppendLine(mapping.ToString());

        // Boot BASIC to the editor (step4 contract), settle, then warm-boot into CP/M A>.
        Assert.True(MachineBootAcceptanceTests.BootHarness.ReachesEditor(boot, maxFrames: 1000),
            "BASIC did not reach the editor before the warm-boot command (step4 contract).");
        for (var f = 0; f < 30; f++) boot.RunFrame();
        var basic7e = boot.Memory.Latches.Port7E;

        // PRINT USR 14446 through the physical key path (existing harness helpers). The harness
        // applies each key exactly once, with no paste edges in flight at the same time.
        boot.TypeKey(HcKey.P);
        boot.TypeKey(HcKey.Space);
        boot.TypeExtendedChord(); // L = Caps+Symbol
        boot.TypeKey(HcKey.L);
        boot.TypeKey(HcKey.Space);
        boot.TypeKey(HcKey.D1);
        boot.TypeKey(HcKey.D4);
        boot.TypeKey(HcKey.D4);
        boot.TypeKey(HcKey.D4);
        boot.TypeKey(HcKey.D6);
        boot.TypeKey(HcKey.Enter);

        var deadline = boot.MasterTick + checked(10 * FrameTiming.MasterHz);
        boot.ArmConsoleHook();
        var console = new InputQueue();
        var adapter = new KeyboardInputAdapter(console);
        var rejected = new List<string>();
        adapter.Rejected += rejected.Add;
        var controller = new InputController(console);
        // Paste only once the guest CP/M is actually at A> (the CCP prompt is polling the keyboard).
        // Pasting earlier would have the edges land in BASIC/during the warm-boot transition, where
        // the machine is not sampling the matrix for line input and drops them. The existing faithful
        // input path owns every matrix mutation; the test only decides when to hand the command in.
        bool pasted = false;
        bool cpmReached = false;
        var typedFrom = boot.MasterTick;
        // Faithful per-edge paste: at most one keyboard edge per frame boundary while the guest
        // boots to A> and consumes the command. The existing path owns all matrix mutations.
        for (var frame = 0; frame < 3000; frame++)
        {
            boot.RunFrame();
            controller.ApplyPending(boot.MasterTick, boot.Keyboard);
            if (boot.MasterTick > deadline) break;
            if (!cpmReached
                && boot.Memory.Latches.Port7E != basic7e
                && boot.VideoTextCount() >= 40)
            {
                cpmReached = true;
                typedFrom = boot.MasterTick;
                sb.AppendLine($"CP/M A> entered at tick {typedFrom}");
                // Settle a few frames so the CCP prompt is actively polling before we type in.
                for (var s = 0; s < 40 && !pasted; s++)
                {
                    boot.RunFrame();
                    controller.ApplyPending(boot.MasterTick, boot.Keyboard);
                    if (s == 39)
                    {
                        // Hand the command in through the app's own PasteText/InputQueue classes at the
                        // proven-safe 16-frame interval (above, not below, the guest's 5-scan debounce).
                        var rejectedChar = PipCommand.Any(c => KeyMap.Character(c) is null);
                        Assert.False(rejectedChar, "every punctuation character must map through the existing KeyMap");
                        console.Enqueue(new PasteText(PipCommand + "\n", PasteIntervalTicks));
                        pasted = true;
                        sb.AppendLine($"PIP command handed to the input path at tick {boot.MasterTick} (A> settled)");
                    }
                }
            }
        }
        Assert.True(cpmReached, "CP/M did not reach A> within the 10 emulated second deadline.");
        Assert.True(pasted, "the PIP command was never handed to the input path at A>.");
        Assert.True(rejected.Count == 0, $"a punctuation character failed the host mapping: {string.Join(", ", rejected)}");

        // Let the guest finish: PIP reads A:PIP.COM, writes B:ROUND.COM and prints its summary.
        for (var frame = 0; frame < 2000; frame++)
        {
            boot.RunFrame();
            controller.ApplyPending(boot.MasterTick, boot.Keyboard);
        }

        // Paste fully consumed by the existing controller: queue empty, no active paste, matrix released.
        Assert.False(controller.IsPasting, "the paste was not fully consumed by the existing input path");
        var released = true;
        for (ushort port = 0x00fe; port > 0x00f7; port--)
            released &= boot.Keyboard.Read(port) == 0x1f;
        Assert.True(released, "a key is still held in the matrix after the paste");

        // Guest echo: the CCP line input echoes the entire command including the five punctuation
        // characters; PIP then consumes it and prints its summary.
        var echo = boot.ConsoleText();
        sb.AppendLine();
        sb.AppendLine("Guest CONOUT (echo + PIP summary):");
        sb.AppendLine(echo);
        Assert.Contains(PipCommand, echo, StringComparison.OrdinalIgnoreCase);
        foreach (var ch in Punctuation)
            Assert.Contains(ch.ToString(), echo, StringComparison.OrdinalIgnoreCase);

        // Guest consumption: PIP acted on the command. It read A:PIP.COM and, because of [OV],
        // overwrote B:ROUND.COM with the real PIP.COM image. Assert the B: image changed from the
        // 16 zero bytes and now equals the host-read A:PIP.COM (the copy happened in-guest).
        var pipIn = CpmFs.Open(Dsk.Load(diskBytes)).Get("PIP.COM");
        _output.WriteLine($"A:PIP.COM host read OK: {pipIn.Length} bytes sha256={Sha(pipIn)}");
        var roundOnB = CpmFs.Open(Dsk.Load(driveB.Save()), "cpm-hc80").Get("ROUND.COM");
        sb.AppendLine();
        sb.AppendLine($"A:PIP.COM (host read): {pipIn.Length} bytes sha256={Sha(pipIn)}");
        sb.AppendLine($"B:ROUND.COM before: 16 zero bytes (drive B image sha256={Sha(driveBBefore)})");
        sb.AppendLine($"B:ROUND.COM after guest PIP: {roundOnB.Length} bytes sha256={Sha(roundOnB)}");
        Assert.NotEqual(driveBBefore, driveB.Save());
        Assert.NotEqual(16, roundOnB.Length);
        Assert.Equal(pipIn, roundOnB);

        var frame0 = boot.RunFrame();
        sb.AppendLine();
        sb.AppendLine($"settled frame {frame0.Width}x{frame0.Height} sha256={boot.FrameHash(frame0)}");
        sb.AppendLine($"screen: {boot.ScreenText().Replace("\n", " | ")}");

        var evidencePath = Path.Combine(evidenceDir, "step5a-evidence.txt");
        File.WriteAllText(evidencePath, sb.ToString());
        WriteBmp(Path.Combine(evidenceDir, "step5a-settled.bmp"), frame0);
        File.WriteAllBytes(Path.Combine(evidenceDir, "b-round-after.dsk"), driveB.Save());
        _output.WriteLine(sb.ToString());
    }

    private static string ShaFile(string path)
    {
        var b = File.ReadAllBytes(path);
        return Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    }

    private static string Sha(byte[] b) =>
        Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static string GitHead()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "rev-parse HEAD")
            { RedirectStandardOutput = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
            return p.StandardOutput.ReadToEnd().Trim();
        }
        catch { return "unknown"; }
    }

    private static void WriteBmp(string path, VideoFrame frame)
    {
        var pixelBytes = checked(frame.Width * frame.Height * 4);
        var bmp = new byte[54 + pixelBytes];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), frame.Width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -frame.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), pixelBytes);
        // Pixels are packed RGBA32 (little-endian uint per pixel); BMP rows are bottom-up BGRX.
        var px = frame.Pixels.ToArray();
        for (int y = frame.Height - 1, o = 54; y >= 0; y--, o += frame.Width * 4)
            for (var x = 0; x < frame.Width; x++)
            {
                var pixel = px[y * frame.Width + x];
                bmp[o + x * 4] = (byte)(pixel & 0xff);           // R -> B plane
                bmp[o + x * 4 + 1] = (byte)((pixel >> 8) & 0xff);   // G
                bmp[o + x * 4 + 2] = (byte)((pixel >> 16) & 0xff);  // B -> R plane
                bmp[o + x * 4 + 3] = 0;
            }
        File.WriteAllBytes(path, bmp);
    }
}
