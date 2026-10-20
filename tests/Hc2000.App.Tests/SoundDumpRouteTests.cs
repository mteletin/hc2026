using System.Text;
using System.Text.Json;
using Hc2000.App.Audio;
using Hc2000.App.Settings;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>The sound-dump route (hc2000-app-sound-dump-route): one named fence in the dump path, a
/// CLI flag and an App setting sharing the same name, and one identical refusal sentence. PO ruling
/// board 634859; mechanism ruled by architect-1 (doc note-20261019-091323).</summary>
public sealed class SoundDumpRouteTests : IDisposable
{
    private readonly string root;
    private readonly string home;
    private readonly string outside;

    public SoundDumpRouteTests()
    {
        root = Path.Combine(Path.GetTempPath(), "hf-sounddump-" + Guid.NewGuid().ToString("N"));
        home = Path.Combine(root, "home");
        outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(outside);
    }

    public void Dispose() => Directory.Delete(root, true);

    // The CLI surface: the catalog-owned spelling, kept raw, with the standard refusal lines.

    [Fact]
    public void CliAcceptsTheSoundDumpSpelling()
    {
        var parsed = LaunchOptions.Parse(["--sound-dump", "sound.wav"]);
        Assert.True(parsed.Ok, parsed.StatusLine);
        Assert.Equal("sound.wav", parsed.Options!.SoundDump);
    }

    [Fact]
    public void CliSoundDumpIsAbsentWhenNotGiven()
    {
        var parsed = LaunchOptions.Parse(["--start"]);
        Assert.True(parsed.Ok, parsed.StatusLine);
        Assert.Null(parsed.Options!.SoundDump);
    }

    [Fact]
    public void CliRefusesMissingAndDuplicateValues()
    {
        var missing = LaunchOptions.Parse(["--sound-dump"]);
        Assert.False(missing.Ok);
        Assert.Equal("Missing value for --sound-dump.", missing.StatusLine);
        var duplicate = LaunchOptions.Parse(["--sound-dump", "a.wav", "--sound-dump", "b.wav"]);
        Assert.False(duplicate.Ok);
        Assert.Equal("Duplicate --sound-dump.", duplicate.StatusLine);
    }

    [Fact]
    public void CliRefusesOtherSpellingsAsUnknown()
    {
        var parsed = LaunchOptions.Parse(["--dump-sound", "a.wav"]);
        Assert.False(parsed.Ok);
        Assert.Equal("Unknown option: --dump-sound.", parsed.StatusLine);
    }

    // The fence: the user home is the only root, checked on the fully resolved path.

    [Fact]
    public void RelativePathResolvesUnderHomeNeverCwd()
    {
        var result = SoundDumpGate.Resolve("sound.wav", home);
        Assert.True(result.Ok, result.Refusal);
        Assert.Equal(Path.Combine(home, "sound.wav"), result.Path);
    }

    [Fact]
    public void NestedRelativeAndAbsoluteInsideHomeAreAdmitted()
    {
        Directory.CreateDirectory(Path.Combine(home, "a", "b"));
        Assert.True(SoundDumpGate.Resolve("a/b/x.wav", home).Ok);
        var absolute = SoundDumpGate.Resolve(Path.Combine(home, "a", "b", "y.wav"), home);
        Assert.True(absolute.Ok, absolute.Refusal);
        Assert.Equal(Path.Combine(home, "a", "b", "y.wav"), absolute.Path);
    }

    [Fact]
    public void EscapeIsRefusedWithTheOneSentenceNamingTheHome()
    {
        var dotDot = SoundDumpGate.Resolve("../outside/x.wav", home);
        var absolute = SoundDumpGate.Resolve(Path.Combine(outside, "y.wav"), home);
        var siblingPrefix = SoundDumpGate.Resolve(home + "other/x.wav", home);
        Assert.False(dotDot.Ok);
        Assert.False(absolute.Ok);
        Assert.False(siblingPrefix.Ok);
        Assert.All(new[] { dotDot, absolute, siblingPrefix },
            r => Assert.Equal(SoundDumpGate.Refusal(home), r.Refusal));
        Assert.Contains(home, dotDot.Refusal);
    }

    [Fact]
    public void SymlinkDirectoryEscapeIsResolvedBeforeTheCheck()
    {
        File.CreateSymbolicLink(Path.Combine(home, "link"), outside);
        var result = SoundDumpGate.Resolve("link/x.wav", home);
        Assert.False(result.Ok);
        Assert.Equal(SoundDumpGate.Refusal(home), result.Refusal);
    }

    [Fact]
    public void RelativeSymlinkTargetEscapeIsResolvedBeforeTheCheck()
    {
        // A relative link target is resolved against the link's own directory: ../outside
        // lands in the sibling of the home, not under it.
        File.CreateSymbolicLink(Path.Combine(home, "rel"), Path.Combine(".", "..", "outside"));
        var result = SoundDumpGate.Resolve("rel/x.wav", home);
        Assert.False(result.Ok);
        Assert.Equal(SoundDumpGate.Refusal(home), result.Refusal);
    }

    [Fact]
    public void BrokenSymlinkIsRefused()
    {
        File.CreateSymbolicLink(Path.Combine(home, "broken"), Path.Combine(outside, "gone.wav"));
        var result = SoundDumpGate.Resolve("broken", home);
        Assert.False(result.Ok);
        Assert.Equal(SoundDumpGate.Refusal(home), result.Refusal);
    }

    [Fact]
    public void DotDotAfterSymlinkAppliesToTheLinkTarget()
    {
        Directory.CreateDirectory(Path.Combine(outside, "sub"));
        File.CreateSymbolicLink(Path.Combine(home, "l"), Path.Combine(outside, "sub"));
        // l/.. resolves to <outside>, not <home>.
        var result = SoundDumpGate.Resolve("l/../x.wav", home);
        Assert.False(result.Ok);
        Assert.Equal(SoundDumpGate.Refusal(home), result.Refusal);
    }

    [Fact]
    public void SymlinkedHomeStillAdmitsFilesUnderIt()
    {
        var realHome = Path.Combine(root, "realhome");
        Directory.CreateDirectory(realHome);
        File.CreateSymbolicLink(Path.Combine(root, "hlink"), realHome);
        var result = SoundDumpGate.Resolve("x.wav", Path.Combine(root, "hlink"));
        Assert.True(result.Ok, result.Refusal);
        Assert.Equal(Path.Combine(realHome, "x.wav"), result.Path);
    }

    [Fact]
    public void SymlinkLoopIsRefusedNotHung()
    {
        File.CreateSymbolicLink(Path.Combine(home, "loop-a"), "loop-b");
        File.CreateSymbolicLink(Path.Combine(home, "loop-b"), "loop-a");
        var result = SoundDumpGate.Resolve("loop-a/x.wav", home);
        Assert.False(result.Ok);
        Assert.Equal(SoundDumpGate.Refusal(home), result.Refusal);
    }

    [Fact]
    public void BlankMeansNoDumpAndTheDefaultRootIsTheRealHome()
    {
        Assert.Equal(SoundDumpResolution.None, SoundDumpGate.Resolve(null, home));
        Assert.Equal(SoundDumpResolution.None, SoundDumpGate.Resolve("   ", home));
        var resolved = SoundDumpGate.Resolve("probe.wav"); // no home override
        var realHome = System.IO.Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Assert.True(resolved.Ok, resolved.Refusal);
        Assert.StartsWith(realHome, resolved.Path!, StringComparison.Ordinal);
    }

    // The App setting: the same name, the same fence, shape-only in the record.

    [Fact]
    public void SavedSettingPreservesTheSoundDumpName()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"soundDump\":\"sound.wav\"}", AppSettings.JsonOptions)!;
        settings.Validate();
        var json = JsonSerializer.SerializeToElement(settings, AppSettings.JsonOptions);
        Assert.Equal("sound.wav", json.GetProperty("soundDump").GetString());
        Assert.Null(JsonSerializer.Deserialize<AppSettings>("{}" , AppSettings.JsonOptions)!.SoundDump);
    }

    [Fact]
    public void EmptySoundDumpSettingIsRejectedByShape()
    {
        var ex = Assert.Throws<ArgumentException>(() => new AppSettings { SoundDump = "" }.Validate());
        Assert.Equal("soundDump must be absent or a non-empty string.", ex.Message);
    }

    [Fact]
    public void EditorBlankClearsSoundDump()
    {
        var cleared = new SettingsEditor(new AppSettings { SoundDump = "a.wav" }) { SoundDump = "" };
        Assert.Null(cleared.Build().SoundDump);
        var kept = new SettingsEditor(new AppSettings { SoundDump = "a.wav" }) { SoundDump = "  b.wav " };
        Assert.Equal("b.wav", kept.Build().SoundDump);
    }

    // The dump itself: a tone writes above the noise floor; a sound-off run writes a file that
    // says so. The audio rendering path is untouched.

    [Fact]
    public void DumpedWavCarriesTheToneAboveTheNoiseFloor()
    {
        var path = Path.Combine(home, "dump.wav");
        var inner = new ToneSink(44100);
        using (var capture = new CapturingAudioSink(inner, path, 10000))
        {
            for (var i = 0; i < 441; i++)
                capture.Queue([0.5f * MathF.Sin(2 * MathF.PI * 440 * i / 44100)]);
        }
        var wav = File.ReadAllBytes(path);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        var (fmt, _, data) = ParseChunks(wav);
        Assert.Equal(1, BitConverter.ToInt16(fmt.AsSpan(0, 2))); // PCM
        Assert.Equal(44100, BitConverter.ToInt32(fmt.AsSpan(4, 4)));
        Assert.Equal(441, data.Length);
        var min = data.Min();
        var max = data.Max();
        Assert.True(min < 78, $"quietest byte {min} - no tone in the dump");
        Assert.True(max > 178, $"loudest byte {max} - no tone in the dump");
    }

    [Fact]
    public void SoundOffRunWritesASilentFileThatSaysSo()
    {
        var path = Path.Combine(home, "dump.wav");
        var diagnostics = new List<string>();
        using var output = BeeperAudioOutput.Open(1000, enabled: false, diagnostic: diagnostics.Add, capturePath: path);
        Assert.False(output.Available);
        var wav = File.ReadAllBytes(path);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        var (fmt, list, data) = ParseChunks(wav);
        Assert.Equal(44100, BitConverter.ToInt32(fmt.AsSpan(4, 4)));
        Assert.NotNull(list);
        Assert.Contains("Audio is disabled.", Encoding.ASCII.GetString(list!));
        Assert.Equal(SoundDumpSilentFile.Rate, data.Length);
        Assert.All(data, b => Assert.Equal((byte)128, b)); // the capture's PCM8 silence
        Assert.Empty(diagnostics); // the deck line is for a live sink; the file says it here
    }

    private static (byte[] Fmt, byte[] List, byte[] Data) ParseChunks(byte[] wav)
    {
        byte[]? fmt = null, list = null, data = null;
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, pos, 4);
            var size = BitConverter.ToInt32(wav.AsSpan(pos + 4, 4));
            if (id == "fmt ") fmt = wav.AsSpan(pos + 8, size).ToArray();
            if (id == "LIST") list = wav.AsSpan(pos + 8, size).ToArray();
            if (id == "data") data = wav.AsSpan(pos + 8, size).ToArray();
            pos += 8 + size;
        }
        return (fmt!, list!, data!);
    }

    private sealed class ToneSink(int rate) : IQueuedAudioSink
    {
        public int SampleRate => rate;
        public int QueuedSamples { get; private set; }
        public void Queue(ReadOnlySpan<float> samples) => QueuedSamples += samples.Length;
        public void Pause(bool paused) { }
        public void Clear() => QueuedSamples = 0;
        public void Dispose() { }
    }
}
